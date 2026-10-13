namespace DocAssistant.Api.Modules.Identity;

// The cookie that carries the access token in a browser (docs/decisions.md #60). The token
// itself is unchanged; the cookie is only another way of sending it.
//
// HttpOnly: page scripts cannot read it, so a script injected into the page (XSS) cannot
// steal the token. SameSite=Strict: the browser does not send it with requests started by
// other sites. Secure: sent over HTTPS only.
public static class SessionCookie
{
    public const string Name = "docassistant_session";

    // Expires together with the token: a cookie that outlives it would only be rejected.
    public static void Append(HttpContext http, AccessToken token) =>
        http.Response.Cookies.Append(Name, token.Token, Options(http, token.ExpiresAt));

    private static CookieOptions Options(HttpContext http, DateTimeOffset expiresAt) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Expires = expiresAt,

        // Decided by the environment, not by whether this request came over HTTPS: behind
        // a reverse proxy the app itself sees plain HTTP. Only local development, where
        // the API runs on http://localhost, goes without.
        Secure = !http.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment(),
    };
}
