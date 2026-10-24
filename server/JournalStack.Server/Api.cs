using Dapper;
using Npgsql;

namespace JournalStack.Server;

public record Tip(Guid Id, string Text, DateTime UpdatedAt);

public static class Api
{
    private const string BearerPrefix = "Bearer ";

    public static void Map(WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.MapGet("/health/ready", async (NpgsqlDataSource db, HttpContext http) =>
        {
            await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
            await conn.ExecuteScalarAsync<int>("SELECT 1");
            return Results.Ok(new { status = "ready" });
        });

        app.MapPost("/api/v1/devices", async (NpgsqlDataSource db, Limits limits, HttpContext http) =>
        {
            if (!limits.Registrations.TryTake("all"))
            {
                return Problems.Create(429, "registration_limit", "Too many registrations. Try again in a minute.");
            }

            var id = Guid.NewGuid();
            var token = Tokens.New();
            await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
            await conn.ExecuteAsync(
                "INSERT INTO devices (id, token_hash) VALUES (@id, @hash)",
                new { id, hash = Tokens.Hash(token) });

            // The token is shown once. No cache may keep a copy.
            http.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { deviceId = id, token }, statusCode: 201);
        });

        var device = app.MapGroup("/api/v1").AddEndpointFilter(RequireDevice);

        device.MapGet("/tip/today", async (NpgsqlDataSource db, HttpContext http) =>
        {
            await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
            var tip = await conn.QuerySingleOrDefaultAsync<Tip>(
                "SELECT id, text, updated_at AS updatedat FROM tips WHERE status = 'published' ORDER BY updated_at DESC, id LIMIT 1");
            return tip is null ? Results.NoContent() : Results.Ok(tip);
        });
    }

    private static async ValueTask<object?> RequireDevice(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.Ordinal) || header.Length != BearerPrefix.Length + Tokens.Length)
        {
            return Problems.Unauthorized();
        }

        var db = http.RequestServices.GetRequiredService<NpgsqlDataSource>();
        await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
        var deviceId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT id FROM devices WHERE token_hash = @hash",
            new { hash = Tokens.Hash(header[BearerPrefix.Length..]) });
        if (deviceId is null)
        {
            return Problems.Unauthorized();
        }

        var limits = http.RequestServices.GetRequiredService<Limits>();
        if (!limits.DeviceRequests.TryTake(deviceId.Value.ToString()))
        {
            return Problems.Create(429, "rate_limited", "Too many requests from this device. Try again later.");
        }

        return await next(context);
    }
}
