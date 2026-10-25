using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace JournalStack.Server;

public static class Admin
{
    public const string CookieName = "js_admin";
    public const string RequestedWith = "journalstack";
    public const int LoginBodyLimit = 1024;
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

    private const int MinPasswordLength = 12;

    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object NoUser = new();

    public static void Map(WebApplication app)
    {
        var admin = app.MapGroup("/admin/api").AddEndpointFilter(RequireHeaderAndJson);
        admin.MapPost("/login", Login);
        admin.MapPost("/logout", Logout);

        var signedIn = admin.MapGroup("").AddEndpointFilter(RequireSession);
        AdminContent.Map(signedIn);
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

    // Reads a small JSON object and returns the named string properties. A missing or non-string property is null.
    internal static async Task<(string?[] Values, IResult? Error)> ReadStrings(HttpContext http, int limit, params string[] names)
    {
        var body = await Entries.ReadBody(http.Request, limit, http.RequestAborted);
        if (body is null)
        {
            return ([], Problems.Create(413, "payload_too_large", "The request body is too large."));
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ([], Problems.Create(400, "invalid_json", "The body must be a JSON object."));
            }

            return (names.Select(name => Entries.ReadString(root, name)).ToArray(), null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return ([], Problems.Create(400, "invalid_json", "The body is not valid JSON."));
        }
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

    private static async Task<IResult> Login(HttpContext http, NpgsqlDataSource db, Limits limits, TimeProvider clock)
    {
        var hash = ConfiguredHash(http);
        if (hash.Length == 0)
        {
            return Problems.Create(503, "admin_not_configured", "Admin login is not set up on this server.");
        }

        if (!limits.AdminLogins.TryTake("all"))
        {
            return Problems.Create(429, "login_locked", "Too many login attempts. Try again in a minute.");
        }

        var (values, error) = await ReadStrings(http, LoginBodyLimit, "password");
        if (error is not null)
        {
            return error;
        }

        var password = values[0];
        if (password is null || !PasswordMatches(hash, password))
        {
            return Problems.Create(401, "invalid_credentials", "The password is not right.");
        }

        var token = Tokens.New();
        var expires = clock.GetUtcNow().Add(SessionLifetime);

        // The cookie is appended only after the INSERT returns. A failed INSERT is not caught, so the
        // catch-all middleware answers and no session cookie leaves the server.
        await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
        await conn.ExecuteAsync(
            "INSERT INTO admin_sessions (token_hash, expires_at) VALUES (@hash, @expires)",
            new { hash = SessionHash(token, hash), expires = expires.UtcDateTime });

        http.Response.Cookies.Append(CookieName, token, CookieFor(http, expires));
        return Results.NoContent();
    }

    private static async Task<IResult> Logout(HttpContext http, NpgsqlDataSource db)
    {
        var token = http.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(token))
        {
            return Results.NoContent();
        }

        // The cookie is cleared only after the DELETE returns. A failed DELETE leaves the session valid and the cookie in place.
        await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
        await conn.ExecuteAsync(
            "DELETE FROM admin_sessions WHERE token_hash = @hash",
            new { hash = SessionHash(token, ConfiguredHash(http)) });

        http.Response.Cookies.Delete(CookieName, CookieFor(http, null));
        return Results.NoContent();
    }

    // Private data is never cached. A request that changes data needs the custom header and a JSON body:
    // a page on another site cannot send them, because the server grants no cross-site permission.
    private static async ValueTask<object?> RequireHeaderAndJson(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";

        if (!HttpMethods.IsGet(http.Request.Method))
        {
            if (http.Request.Headers["X-Requested-With"] != RequestedWith)
            {
                return Problems.Create(403, "csrf_header", "The X-Requested-With header is missing or wrong.");
            }
            if (!http.Request.HasJsonContentType())
            {
                return Problems.Create(415, "unsupported_media_type", "The body must be application/json.");
            }
        }

        return await next(context);
    }

    private static async ValueTask<object?> RequireSession(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var token = http.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(token))
        {
            return Problems.Create(401, "unauthorized", "Sign in required.");
        }

        var db = http.RequestServices.GetRequiredService<NpgsqlDataSource>();
        var clock = http.RequestServices.GetRequiredService<TimeProvider>();
        await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
        var found = await conn.QuerySingleOrDefaultAsync<int?>(
            "SELECT 1 FROM admin_sessions WHERE token_hash = @hash AND expires_at > @now",
            new { hash = SessionHash(token, ConfiguredHash(http)), now = clock.GetUtcNow().UtcDateTime });
        if (found is null)
        {
            return Problems.Create(401, "unauthorized", "Sign in required.");
        }

        return await next(context);
    }
}
