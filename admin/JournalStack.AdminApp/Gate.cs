namespace JournalStack.AdminApp;

// The browser-facing protections: the cookie, the Origin check, the session check and the headers on every /admin answer.
public static class Gate
{
    public const string CookieName = "js_admin";

    private const string Policy = "default-src 'self'; frame-ancestors 'none'; form-action 'self'";

    public static CookieOptions CookieFor(HttpContext http, DateTimeOffset? expires)
    {
        return new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = http.Request.IsHttps,
            Path = "/admin",
            Expires = expires,
        };
    }

    // Runs for every path under /admin, so a page added later is protected without anyone remembering to.
    // Only the login and logout paths are exempt from the session check.
    // Private data is never cached, and the policy removes inline script and forms that post elsewhere.
    public static async Task Run(HttpContext http, RequestDelegate next)
    {
        var path = http.Request.Path;
        if (!path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase))
        {
            await next(http);
            return;
        }

        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.ContentSecurityPolicy = Policy;

        // Checked before the session, so a foreign page is refused with 403 whether or not the visitor is signed in.
        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method)
            && !HttpMethods.IsOptions(http.Request.Method) && !SameHostOrigin(http))
        {
            await ErrorAnswer.WriteAsync(http, 403, "csrf_origin", "The Origin header is missing or is not this site.");
            return;
        }

        var exempt = string.Equals(path.Value, "/admin/login", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/admin/logout", StringComparison.OrdinalIgnoreCase);
        if (!exempt)
        {
            var token = http.Request.Cookies[CookieName];
            if (string.IsNullOrEmpty(token))
            {
                http.Response.Redirect("/admin/login");
                return;
            }

            // Throws SignedOutException when the API says the session is gone. The catch-all turns that into the redirect.
            var api = http.RequestServices.GetRequiredService<ApiClient>();
            await api.CheckSessionAsync(token, http.RequestAborted);
        }

        await next(http);
    }

    // A page on another site cannot forge the Origin header of a browser form post.
    private static bool SameHostOrigin(HttpContext http)
    {
        return Uri.TryCreate(http.Request.Headers.Origin.ToString(), UriKind.Absolute, out var origin)
            && string.Equals(origin.Authority, http.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}
