using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace JournalStack.Server;

// The admin API. Its only caller is the admin app, which sends the service key on every request
// and the session token on every request after sign-in. The browser never calls it.
public static class Admin
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

    public const string KeyHeader = "X-Admin-Key";
    public const string SessionHeader = "X-Admin-Session";

    // The admin app accepts form values up to 16 Ki characters, and JSON escapes a non-ASCII character as up to 6 bytes.
    // 128 KiB lets everything the admin app lets through reach the API, so a long tip gets the editor message.
    internal const int AdminBodyLimit = 128 * 1024;

    // A configured key shorter than this counts as not set.
    internal const int MinKeyLength = 32;

    private const int MinPasswordLength = 12;

    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object NoUser = new();

    public static void Map(WebApplication app)
    {
        // Every route under /admin/api needs the key, sign-in and sign-out included.
        // Handlers take HttpContext and read the body by hand. A body-bound parameter would be read
        // before the key filter runs, and before the login lockout counts the attempt.
        var api = app.MapGroup("/admin/api").AddEndpointFilter(RequireKey);

        api.MapPost("/login", Login);

        // Sign-out needs the key but not a valid session, so a dead session still gets 204 and the admin app clears its cookie.
        api.MapPost("/logout", Logout);

        var signedIn = api.MapGroup("").AddEndpointFilter(RequireSession);
        signedIn.MapGet("/session", () => Results.NoContent());
        signedIn.MapGet("/tips", async (NpgsqlDataSource db, HttpContext http) =>
            Results.Ok(await AdminContent.ListTips(db, http.RequestAborted)));
        signedIn.MapPost("/tips", SaveTip);
        signedIn.MapGet("/entries", async (NpgsqlDataSource db, HttpContext http) =>
            Results.Ok(await AdminContent.ListEntries(db, http.RequestAborted)));
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

    // Empty or short counts as not set. Used by the filter and by the startup warning.
    internal static bool KeyIsSet(string? configuredKey)
    {
        return configuredKey is not null && configuredKey.Length >= MinKeyLength;
    }

    // The key check, first on every admin route. The answer is never cached because the login answer carries the token.
    // A key that is not set refuses everything, also an empty presented key.
    private static async ValueTask<object?> RequireKey(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";

        var configured = http.RequestServices.GetRequiredService<IConfiguration>()["ADMIN_API_KEY"];
        if (!KeyIsSet(configured))
        {
            return Problems.Create(503, "admin_key_not_configured", "The admin key is not set on this server.");
        }

        // Compared in constant time, so the time taken does not tell how much of a guess was right.
        var presented = Encoding.UTF8.GetBytes(http.Request.Headers[KeyHeader].ToString());
        if (!CryptographicOperations.FixedTimeEquals(presented, Encoding.UTF8.GetBytes(configured!)))
        {
            return Problems.Create(401, "admin_key", "The admin key is missing or wrong.");
        }

        return await next(context);
    }

    private static async ValueTask<object?> RequireSession(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var token = http.Request.Headers[SessionHeader].ToString();
        if (token.Length == 0 || !await SessionIsValidAsync(http, token))
        {
            return Problems.Create(401, "session_invalid", "The admin session is missing, expired or unknown.");
        }

        return await next(context);
    }

    private static async Task<bool> SessionIsValidAsync(HttpContext http, string token)
    {
        var db = http.RequestServices.GetRequiredService<NpgsqlDataSource>();
        var clock = http.RequestServices.GetRequiredService<TimeProvider>();
        await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
        var found = await conn.QuerySingleOrDefaultAsync<int?>(
            "SELECT 1 FROM admin_sessions WHERE token_hash = @hash AND expires_at > @now",
            new { hash = SessionHash(token, ConfiguredHash(http)), now = clock.GetUtcNow().UtcDateTime });
        return found is not null;
    }

    // Order: hash set, lockout counts the attempt, body read, password check, INSERT, answer.
    // The token is in the answer only after the INSERT returned. A failed INSERT is not caught, so the catch-all
    // middleware answers and no token leaves the server.
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

        var (values, status, error) = await ReadStrings(http, AdminBodyLimit, "password");
        if (status == 413)
        {
            return error!;
        }

        // A body that is not a JSON object, or has no string password, is a wrong password. It was already counted.
        var password = status == 0 ? values[0] : null;
        if (password is null || !PasswordMatches(hash, password))
        {
            return Problems.Create(401, "invalid_credentials", "The password is not right.");
        }

        var token = Tokens.New();
        var expires = clock.GetUtcNow().Add(SessionLifetime);
        await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
        await conn.ExecuteAsync(
            "INSERT INTO admin_sessions (token_hash, expires_at) VALUES (@hash, @expires)",
            new { hash = SessionHash(token, hash), expires = expires.UtcDateTime });

        return Results.Ok(new { token, expiresAt = expires.UtcDateTime });
    }

    // A failed DELETE is not caught, so the answer is a 5xx and the admin app keeps the session.
    // A session that is empty, unknown or expired is 204 as well.
    private static async Task<IResult> Logout(HttpContext http, NpgsqlDataSource db)
    {
        var token = http.Request.Headers[SessionHeader].ToString();
        if (token.Length == 0)
        {
            return Results.NoContent();
        }

        await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
        await conn.ExecuteAsync(
            "DELETE FROM admin_sessions WHERE token_hash = @hash",
            new { hash = SessionHash(token, ConfiguredHash(http)) });
        return Results.NoContent();
    }

    // Same order the person saw in the old pages: text and status are checked first (422), then the id (404), then the write.
    private static async Task<IResult> SaveTip(HttpContext http, NpgsqlDataSource db)
    {
        var (values, _, error) = await ReadStrings(http, AdminBodyLimit, "id", "text", "status");
        if (error is not null)
        {
            return error;
        }

        var rawId = values[0] ?? "";
        var (text, tipStatus, message) = AdminContent.ValidateTip(values[1], values[2]);
        if (message is not null)
        {
            return Problems.Create(422, "invalid_tip", message);
        }

        Guid? id = null;
        if (rawId.Length > 0)
        {
            if (!Guid.TryParse(rawId, out var parsed))
            {
                return Problems.Create(404, "tip_not_found", "That tip no longer exists.");
            }
            id = parsed;
        }

        if (id is null)
        {
            var created = await AdminContent.CreateTip(db, text, tipStatus, http.RequestAborted);
            return Results.Json(new { id = created }, statusCode: 201);
        }
        if (!await AdminContent.UpdateTip(db, id.Value, text, tipStatus, http.RequestAborted))
        {
            return Problems.Create(404, "tip_not_found", "That tip no longer exists.");
        }
        return Results.NoContent();
    }

    // Reads a small JSON object and returns the named string properties. A missing or non-string property is null.
    // Status is 0 on success, 413 when the body is over the limit, 400 when it is not a JSON object.
    internal static async Task<(string?[] Values, int Status, IResult? Error)> ReadStrings(HttpContext http, int limit, params string[] names)
    {
        var body = await Entries.ReadBody(http.Request, limit, http.RequestAborted);
        if (body is null)
        {
            return ([], 413, Problems.Create(413, "payload_too_large", "The request body is too large."));
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ([], 400, Problems.Create(400, "invalid_json", "The body must be a JSON object."));
            }

            return (names.Select(name => Entries.ReadString(root, name)).ToArray(), 0, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return ([], 400, Problems.Create(400, "invalid_json", "The body is not valid JSON."));
        }
    }
}
