using DocAssistant.Api.Modules.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;

namespace DocAssistant.UnitTests.Identity;

// Whether the session cookie is Secure depends on the environment, not on the request
// (docs/decisions.md #60). Integration tests always run in the "Testing" environment, so
// the environments are compared here.
public class SessionCookieTests
{
    private static readonly AccessToken Token = new("token-value", DateTimeOffset.UtcNow.AddHours(1));

    // Local development runs on http://localhost; a Secure cookie would never be sent back.
    [Fact]
    public void InDevelopmentTheCookieIsNotSecure()
    {
        var http = HttpContextIn(Environments.Development);

        SessionCookie.Append(http, Token);

        Assert.False(SetCookie(http).Secure);
    }

    // Everywhere else it is Secure, even when the request reached the app over plain
    // HTTP, as it does behind a reverse proxy.
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void OutsideDevelopmentTheCookieIsSecureEvenOverHttp(string environment)
    {
        var http = HttpContextIn(environment);
        http.Request.Scheme = "http";

        SessionCookie.Append(http, Token);

        Assert.True(SetCookie(http).Secure);
    }

    // The cookie that removes the session must match the one that set it, or the browser
    // keeps the old one.
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void DeleteUsesTheSameAttributesAsAppend(string environment)
    {
        var appended = HttpContextIn(environment);
        var deleted = HttpContextIn(environment);

        SessionCookie.Append(appended, Token);
        SessionCookie.Delete(deleted);

        var set = SetCookie(appended);
        var removed = SetCookie(deleted);
        Assert.Equal(set.Name, removed.Name);
        Assert.Equal(set.Path, removed.Path);
        Assert.Equal(set.Secure, removed.Secure);
        Assert.Equal(set.HttpOnly, removed.HttpOnly);
        Assert.Equal(set.SameSite, removed.SameSite);
    }

    private static DefaultHttpContext HttpContextIn(string environment) => new()
    {
        RequestServices = new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new FakeEnvironment(environment))
            .BuildServiceProvider(),
    };

    private static SetCookieHeaderValue SetCookie(HttpContext http) =>
        SetCookieHeaderValue.Parse(http.Response.Headers.SetCookie.Single());

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "DocAssistant.Api";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
