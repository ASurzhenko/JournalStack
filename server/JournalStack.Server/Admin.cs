using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace JournalStack.Server;

public static class Admin
{
    public const string CookieName = "js_admin";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

    private const int MinPasswordLength = 12;

    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object NoUser = new();

    private const string Policy = "default-src 'self'; frame-ancestors 'none'; form-action 'self'";

    public static void Map(WebApplication app)
    {
        app.Use(Gate);
        app.MapPost("/admin/logout", Logout);
        app.MapRazorPages();
    }

    public static string HashPassword(string password)
    {
        return Hasher.HashPassword(NoUser, password);
    }

    // A hash made by an older runtime still verifies, so a rehash result counts as a match.
    // A stored value that is not a hash at all throws FormatException on purpose: a wrong variable must be loud.
    public static bool PasswordMatches(string hash, string password)
    {
        return Hasher.VerifyHashedPassword(NoUser, hash, password) != PasswordVerificationResult.Failed;
    }

    // The key is the password hash, so a new password makes every stored session useless.
    public static byte[] SessionHash(string token, string passwordHash)
    {
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(passwordHash), Encoding.UTF8.GetBytes(token));
    }

    // `hash-password` mode: reads one line and prints the hash to put in ADMIN_PASSWORD_HASH.
    public static int RunHashPassword()
    {
        var password = Console.ReadLine() ?? "";
        if (password.Length < MinPasswordLength)
        {
            Console.Error.WriteLine($"The password must be at least {MinPasswordLength} characters.");
            return 1;
        }

        Console.WriteLine(HashPassword(password));
        return 0;
    }

    // Empty means not set. With an empty key no stored session matches, so the session checks need no special case.
    internal static string ConfiguredHash(HttpContext http)
    {
        return http.RequestServices.GetRequiredService<IConfiguration>()["ADMIN_PASSWORD_HASH"] ?? "";
    }

    private static CookieOptions CookieFor(HttpContext http, DateTimeOffset? expires)
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

    // Called by the login page after the password matched. The cookie is appended only after the INSERT returns.
    // A failed INSERT is not caught, so the catch-all middleware answers and no session cookie leaves the server.
    internal static async Task StartSessionAsync(HttpContext http, NpgsqlDataSource db, TimeProvider clock, string hash)
    {
        var token = Tokens.New();
        var expires = clock.GetUtcNow().Add(SessionLifetime);

        await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
        await conn.ExecuteAsync(
            "INSERT INTO admin_sessions (token_hash, expires_at) VALUES (@hash, @expires)",
            new { hash = SessionHash(token, hash), expires = expires.UtcDateTime });

        http.Response.Cookies.Append(CookieName, token, CookieFor(http, expires));
    }

    private static async Task<IResult> Logout(HttpContext http, NpgsqlDataSource db)
    {
        var token = http.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(token))
        {
            return Results.Redirect("/admin/login");
        }

        // The cookie is cleared only after the DELETE returns. A failed DELETE leaves the session valid and the cookie in place.
        await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
        await conn.ExecuteAsync(
            "DELETE FROM admin_sessions WHERE token_hash = @hash",
            new { hash = SessionHash(token, ConfiguredHash(http)) });

        http.Response.Cookies.Delete(CookieName, CookieFor(http, null));
        return Results.Redirect("/admin/login");
    }

    // Runs for every path under /admin, so a page added later is protected without anyone remembering to.
    // Only the login and logout paths are exempt from the session check.
    // Private data is never cached, and the policy removes inline script and forms that post elsewhere.
    private static async Task Gate(HttpContext http, RequestDelegate next)
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
            await Problems.Create(403, "csrf_origin", "The Origin header is missing or is not this site.").ExecuteAsync(http);
            return;
        }

        var exempt = string.Equals(path.Value, "/admin/login", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/admin/logout", StringComparison.OrdinalIgnoreCase);
        if (!exempt && !await SessionIsValidAsync(http))
        {
            http.Response.Redirect("/admin/login");
            return;
        }

        await next(http);
    }

    // A page on another site cannot forge the Origin header of a browser form post.
    private static bool SameHostOrigin(HttpContext http)
    {
        return Uri.TryCreate(http.Request.Headers.Origin.ToString(), UriKind.Absolute, out var origin)
            && string.Equals(origin.Authority, http.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> SessionIsValidAsync(HttpContext http)
    {
        var token = http.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var db = http.RequestServices.GetRequiredService<NpgsqlDataSource>();
        var clock = http.RequestServices.GetRequiredService<TimeProvider>();
        await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
        var found = await conn.QuerySingleOrDefaultAsync<int?>(
            "SELECT 1 FROM admin_sessions WHERE token_hash = @hash AND expires_at > @now",
            new { hash = SessionHash(token, ConfiguredHash(http)), now = clock.GetUtcNow().UtcDateTime });
        return found is not null;
    }
}
