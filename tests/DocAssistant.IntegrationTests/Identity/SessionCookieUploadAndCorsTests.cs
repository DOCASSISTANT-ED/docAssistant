using System.Net;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.IntegrationTests.Documents;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;

namespace DocAssistant.IntegrationTests.Identity;

// The two places where the session cookie (docs/decisions.md #60) meets the rest of the
// API: the document upload, which is the request a form on another site could forge, and
// CORS, which decides whether the browser lets the Angular app send the cookie at all.
[Collection(DatabaseCollection.Name)]
public class SessionCookieUploadAndCorsTests(PostgresFixture database)
{
    private const string AppOrigin = "http://app.example";
    private const string OtherOrigin = "http://other.example";

    // ---- Upload: multipart/form-data needs no preflight --------------------------------

    // The forged request the second layer exists for: a plain HTML form can post a file
    // with the cookie attached, and the browser asks nobody first.
    [Fact]
    public async Task UploadThatReliesOnTheCookieIsRejectedWithoutTheHeader()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();

        var response = await api.Client.SendAsync(Upload(cookie: admin.Token, requestedWith: false));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(api.Storage.Files);
        Assert.Empty(api.QueuedItems);
    }

    [Fact]
    public async Task UploadThatReliesOnTheCookieWorksWithTheHeader()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();

        var response = await api.Client.SendAsync(Upload(cookie: admin.Token, requestedWith: true));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(api.Storage.Files);
        Assert.Single(api.QueuedItems);
    }

    // The role in the cookie's token counts exactly as it does in the header.
    [Fact]
    public async Task MemberCannotUploadThroughTheCookieEither()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();

        var response = await api.Client.SendAsync(
            Upload(cookie: api.MemberToken(admin.TenantId), requestedWith: true));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(api.Storage.Files);
    }

    // ---- CORS --------------------------------------------------------------------------

    // Before a cross-origin POST with a custom header the browser asks with OPTIONS. It
    // sends the real request, cookie included, only when the answer names the page's
    // origin and allows credentials.
    [Fact]
    public async Task PreflightFromAConfiguredOriginAllowsCredentialsAndTheHeader()
    {
        await using var api = ApiAllowing(AppOrigin);
        using var client = api.CreateClient();

        var response = await client.SendAsync(Preflight(AppOrigin, "POST", "/auth/logout", "x-requested-with"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(AppOrigin, Header(response, HeaderNames.AccessControlAllowOrigin));
        Assert.Equal("true", Header(response, HeaderNames.AccessControlAllowCredentials));
        Assert.Contains("x-requested-with", Header(response, HeaderNames.AccessControlAllowHeaders), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreflightFromAnotherOriginIsNotAllowed()
    {
        await using var api = ApiAllowing(AppOrigin);
        using var client = api.CreateClient();

        var response = await client.SendAsync(Preflight(OtherOrigin, "POST", "/auth/logout", "x-requested-with"));

        Assert.Null(Header(response, HeaderNames.AccessControlAllowOrigin));
        Assert.Null(Header(response, HeaderNames.AccessControlAllowCredentials));
    }

    // With no origin configured, no site is allowed: the default must not be "everyone".
    [Fact]
    public async Task WithoutConfiguredOriginsNoOriginIsAllowed()
    {
        using var client = database.Api.CreateClient();

        var response = await client.SendAsync(Preflight(AppOrigin, "POST", "/auth/logout", "x-requested-with"));

        Assert.Null(Header(response, HeaderNames.AccessControlAllowOrigin));
    }

    // The answer to the real request needs the same two headers, or the browser sends
    // the request but hides the answer from the page. The origin is named, never "*".
    [Fact]
    public async Task AnswerToAConfiguredOriginNamesItAndAllowsCredentials()
    {
        await using var api = ApiAllowing(AppOrigin);
        using var client = api.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        request.Headers.Add(HeaderNames.Origin, AppOrigin);

        var response = await client.SendAsync(request);

        Assert.Equal(AppOrigin, Header(response, HeaderNames.AccessControlAllowOrigin));
        Assert.Equal("true", Header(response, HeaderNames.AccessControlAllowCredentials));
    }

    [Fact]
    public async Task AnswerToAnotherOriginCarriesNoCorsHeaders()
    {
        await using var api = ApiAllowing(AppOrigin);
        using var client = api.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        request.Headers.Add(HeaderNames.Origin, OtherOrigin);

        var response = await client.SendAsync(request);

        Assert.Null(Header(response, HeaderNames.AccessControlAllowOrigin));
        Assert.Null(Header(response, HeaderNames.AccessControlAllowCredentials));
    }

    // The CSRF check runs after CORS, so its rejection carries the CORS headers and the
    // Angular app can read why the request failed.
    [Fact]
    public async Task TheMissingHeaderRejectionIsReadableByTheApp()
    {
        await using var api = ApiAllowing(AppOrigin);
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var token = api.Services.GetRequiredService<JwtTokenIssuer>()
            .Issue(Guid.NewGuid(), "ayse@example.com", Guid.NewGuid(), "Admin").Token;
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
        request.Headers.Add(HeaderNames.Origin, AppOrigin);
        request.Headers.Add(HeaderNames.Cookie, $"{SessionCookie.Name}={token}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(AppOrigin, Header(response, HeaderNames.AccessControlAllowOrigin));
    }

    // ---- Helpers -----------------------------------------------------------------------

    // POST /documents as a browser would send it: the token in the cookie, no
    // Authorization header. UploadTestApi's client keeps cookies itself, but it never
    // sends the one from registration: that cookie is Secure and the test address is http.
    private static HttpRequestMessage Upload(string cookie, bool requestedWith)
    {
        var file = new ByteArrayContent(UploadFiles.Pdf());
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        var request = new HttpRequestMessage(HttpMethod.Post, "/documents")
        {
            Content = new MultipartFormDataContent { { file, "file", "ik.pdf" } },
        };
        request.Headers.Add(HeaderNames.Cookie, $"{SessionCookie.Name}={cookie}");

        if (requestedWith)
        {
            request.Headers.Add(SessionCookieCsrfProtection.HeaderName, "XMLHttpRequest");
        }

        return request;
    }

    private static HttpRequestMessage Preflight(string origin, string method, string path, string headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add(HeaderNames.Origin, origin);
        request.Headers.Add(HeaderNames.AccessControlRequestMethod, method);
        request.Headers.Add(HeaderNames.AccessControlRequestHeaders, headers);

        return request;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    // A copy of the API (docs/testing.md rule 4) with one allowed origin. Startup recovery
    // and the worker are switched off, as in UploadTestApi: they would pick up other
    // tests' unfinished documents.
    private WebApplicationFactory<Program> ApiAllowing(string origin) =>
        database.Api.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Cors:AllowedOrigins:0", origin);

            builder.ConfigureTestServices(services =>
            {
                var ingestionServices = services
                    .Where(service => service.ServiceType == typeof(IHostedService)
                        && (service.ImplementationType == typeof(IngestionRecoveryService)
                            || service.ImplementationType == typeof(IngestionWorker)))
                    .ToList();

                foreach (var service in ingestionServices)
                {
                    services.Remove(service);
                }
            });
        });
}
