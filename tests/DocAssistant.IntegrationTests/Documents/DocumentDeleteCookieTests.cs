using System.Net;
using DocAssistant.Api.Modules.Documents;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;

namespace DocAssistant.IntegrationTests.Documents;

// Deleting the way the web app does it: with the session cookie (docs/decisions.md #60).
// A request that changes something and relies on the cookie must carry X-Requested-With,
// which another site cannot add; without it a page elsewhere could make a signed-in
// admin's browser delete a document.
[Collection(DatabaseCollection.Name)]
public class DocumentDeleteCookieTests(PostgresFixture database)
{
    private const string AppOrigin = "http://app.example";

    [Fact]
    public async Task ADeleteThatReliesOnTheCookieIsRefusedWithoutTheHeader()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await ManagedDocuments.PrepareAsync(api, database, admin, DocumentStatus.Done);

        using var response = await api.Client.SendAsync(Delete(document.Id, cookie: admin.Token, requestedWith: false));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await ManagedDocuments.RowExistsAsync(database, document.Id));
        Assert.Equal(2, await ManagedDocuments.ChunkCountAsync(database, document.Id));
        Assert.Empty(api.Storage.DeletedKeys);
    }

    [Fact]
    public async Task ADeleteThatReliesOnTheCookieWorksWithTheHeader()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await ManagedDocuments.PrepareAsync(api, database, admin, DocumentStatus.Done);

        using var response = await api.Client.SendAsync(Delete(document.Id, cookie: admin.Token, requestedWith: true));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await ManagedDocuments.RowExistsAsync(database, document.Id));
    }

    // The role in the cookie's token counts exactly as it does in the header.
    [Fact]
    public async Task AMemberCannotDeleteThroughTheCookieEither()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await ManagedDocuments.PrepareAsync(api, database, admin, DocumentStatus.Done);

        using var response = await api.Client.SendAsync(
            Delete(document.Id, cookie: await api.MemberTokenAsync(admin.TenantId), requestedWith: true));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await ManagedDocuments.RowExistsAsync(database, document.Id));
    }

    // Reading changes nothing, so the list needs no extra header.
    [Fact]
    public async Task TheListThroughTheCookieNeedsNoHeader()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await ManagedDocuments.UploadAsync(api, admin.Token);

        var request = new HttpRequestMessage(HttpMethod.Get, "/documents");
        request.Headers.Add(HeaderNames.Cookie, $"{SessionCookie.Name}={admin.Token}");
        using var response = await api.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(document.Id.ToString(), await response.Content.ReadAsStringAsync());
    }

    // In development the web app and the API are on different addresses, so the browser
    // asks before a DELETE with a custom header. The answer must allow the method, the
    // header and the cookie, or deleting from the documents page would fail in the browser.
    [Fact]
    public async Task TheBrowserIsAllowedToSendTheDeleteFromTheApp()
    {
        await using var api = ApiAllowing(AppOrigin);
        using var client = api.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Options, $"/documents/{Guid.NewGuid()}");
        request.Headers.Add(HeaderNames.Origin, AppOrigin);
        request.Headers.Add(HeaderNames.AccessControlRequestMethod, "DELETE");
        request.Headers.Add(HeaderNames.AccessControlRequestHeaders, "x-requested-with");
        using var response = await client.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(AppOrigin, Single(response, HeaderNames.AccessControlAllowOrigin));
        Assert.Equal("true", Single(response, HeaderNames.AccessControlAllowCredentials));
        Assert.Contains("DELETE", Single(response, HeaderNames.AccessControlAllowMethods), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-requested-with", Single(response, HeaderNames.AccessControlAllowHeaders), StringComparison.OrdinalIgnoreCase);
    }

    private static HttpRequestMessage Delete(Guid documentId, string cookie, bool requestedWith)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/documents/{documentId}");
        request.Headers.Add(HeaderNames.Cookie, $"{SessionCookie.Name}={cookie}");

        if (requestedWith)
        {
            request.Headers.Add(SessionCookieCsrfProtection.HeaderName, "XMLHttpRequest");
        }

        return request;
    }

    private static string Single(HttpResponseMessage response, string header) =>
        Assert.Single(response.Headers.GetValues(header));

    // A copy of the API that accepts the app's origin, with ingestion switched off as in
    // the other document tests.
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
