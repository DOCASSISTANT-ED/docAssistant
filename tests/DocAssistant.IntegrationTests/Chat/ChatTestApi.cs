using System.Net.Http.Headers;
using System.Net.Http.Json;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.Api.Modules.Search;
using DocAssistant.IntegrationTests.Identity;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DocAssistant.IntegrationTests.Chat;

// A copy of the API for chat tests (docs/testing.md rule 4): the model and search are
// stand-ins the test controls, everything else is the real app on the real test database.
// A test says what search finds and what the model writes, asks over HTTP, and then looks
// at the stream, at what was stored, and at what the model was given.
//
// Startup recovery and the ingestion worker are switched off, as in UploadTestApi: they
// are not what these tests are about, and this copy must not pick up other tests'
// unfinished documents.
internal sealed class ChatTestApi : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _api;
    private readonly PostgresFixture _database;

    private ChatTestApi(PostgresFixture database)
    {
        _database = database;

        _api = database.Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IChatClient>(Model);
            services.AddScoped<IChunkRetrieval>(_ => Retrieval);

            var ingestionServices = services
                .Where(service => service.ServiceType == typeof(IHostedService)
                    && (service.ImplementationType == typeof(IngestionRecoveryService)
                        || service.ImplementationType == typeof(IngestionWorker)))
                .ToList();

            foreach (var service in ingestionServices)
            {
                services.Remove(service);
            }
        }));

        Client = _api.CreateClient();
    }

    public HttpClient Client { get; }

    public FakeChatModel Model { get; } = new();

    public FakeChunkRetrieval Retrieval { get; } = new();

    public IServiceProvider Services => _api.Services;

    public static ChatTestApi Start(PostgresFixture database) => new(database);

    // Registers a new organization and its first user.
    public async Task<TestUser> RegisterAsync()
    {
        var registered = await TestAccounts.RegisterAsync(Client, TestAccounts.NewEmail());
        registered.EnsureSuccessStatusCode();
        var token = await TestAccounts.ReadTokenAsync(registered);

        var me = await (await TestAccounts.GetCurrentUserAsync(Client, token))
            .Content.ReadFromJsonAsync<CurrentUserResponse>();

        return new TestUser(token, Guid.Parse(me!.UserId!), me.TenantId!.Value);
    }

    // Adds a user to an existing organization, the way an invitation would once that
    // exists: the user and the membership are written directly (as the superuser, setup
    // only), and the token is one the API itself issues for that user. Without userId a
    // new user is created; with it, an existing user becomes a member of one more
    // organization.
    public async Task<TestUser> AddMemberAsync(
        Guid tenantId,
        MembershipRole role = MembershipRole.Member,
        Guid? userId = null)
    {
        var email = TestAccounts.NewEmail();
        var id = userId ?? Guid.CreateVersion7();

        await using (var db = _database.CreateOwnerDbContext())
        {
            if (userId is null)
            {
                db.Users.Add(new User
                {
                    Id = id,
                    Email = email,
                    NormalizedEmail = User.NormalizeEmail(email),
                    PasswordHash = "not-used-in-these-tests",
                });
            }

            db.Memberships.Add(new Membership
            {
                Id = Guid.CreateVersion7(),
                UserId = id,
                TenantId = tenantId,
                Role = role,
            });

            await db.SaveChangesAsync();
        }

        var token = Services.GetRequiredService<JwtTokenIssuer>().Issue(id, email, tenantId, role.ToString()).Token;

        return new TestUser(token, id, tenantId);
    }

    // POST /chat; without a token when token is null. The answer is returned as soon as
    // its headers arrive, so a test can also read the stream piece by piece.
    public Task<HttpResponseMessage> AskAsync(string? token, string question, Guid? conversationId = null) =>
        AskAsync(token, new { conversationId, question });

    // POST /chat with any body, for requests the typed helper cannot express.
    public Task<HttpResponseMessage> AskAsync(string? token, object body, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/chat") { Content = JsonContent.Create(body) };
        Authorize(request, token);

        return Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    // Asks and reads the answer stream to its end; fails the test when the request is refused.
    public async Task<ChatAnswer> AnswerAsync(string token, string question, Guid? conversationId = null)
    {
        using var response = await AskAsync(token, question, conversationId);
        response.EnsureSuccessStatusCode();

        return await ChatAnswer.ReadAsync(response);
    }

    // GET on a path such as /conversations; without a token when token is null.
    public Task<HttpResponseMessage> GetAsync(string? token, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        Authorize(request, token);

        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _api.DisposeAsync();
    }

    private static void Authorize(HttpRequestMessage request, string? token)
    {
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }
}

internal sealed record TestUser(string Token, Guid UserId, Guid TenantId);
