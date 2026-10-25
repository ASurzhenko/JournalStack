using Dapper;
using Npgsql;

namespace JournalStack.Server;

public record AdminTip(Guid Id, string Text, string Status, DateTime UpdatedAt);

public record AdminEntry(Guid Id, Guid DeviceId, string Text, DateTime ClientCreatedAt, DateTime ReceivedAt);

public static class AdminContent
{
    private const string TipColumns = "id, text, status, updated_at AS updatedat";

    public static void Map(RouteGroupBuilder signedIn)
    {
        signedIn.MapGet("/tips", ListTips);
        signedIn.MapPost("/tips", CreateTip);
        signedIn.MapPut("/tips/{id:guid}", UpdateTip);
        signedIn.MapGet("/entries", ListEntries);
    }

    // Returns the cleaned text and the status, or the answer to send back.
    private static async Task<(string Text, string Status, IResult? Error)> ReadTip(HttpContext http)
    {
        var (values, error) = await Admin.ReadStrings(http, Entries.BodyLimit, "text", "status");
        if (error is not null)
        {
            return ("", "", error);
        }

        if (!Entries.TryCleanText(values[0], out var text))
        {
            return ("", "", Problems.Create(422, "invalid_text", $"The text must be 1 to {Entries.MaxTextLength} characters without control characters."));
        }
        if (values[1] != "draft" && values[1] != "published")
        {
            return ("", "", Problems.Create(422, "invalid_status", "The status must be draft or published."));
        }

        return (text, values[1]!, null);
    }

    private static async Task<IResult> ListTips(HttpContext http, NpgsqlDataSource db)
    {
        await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
        var tips = await conn.QueryAsync<AdminTip>(
            "SELECT " + TipColumns + " FROM tips ORDER BY updated_at DESC, id LIMIT 100");
        return Results.Ok(new { tips });
    }

    private static async Task<IResult> CreateTip(HttpContext http, NpgsqlDataSource db)
    {
        var (text, status, error) = await ReadTip(http);
        if (error is not null)
        {
            return error;
        }

        await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
        var tip = await conn.QuerySingleAsync<AdminTip>(
            "INSERT INTO tips (text, status) VALUES (@text, @status) RETURNING " + TipColumns,
            new { text, status });
        return Results.Json(tip, statusCode: 201);
    }

    // Last write wins. updated_at moves on every save, which is what makes a saved published tip the one devices get.
    private static async Task<IResult> UpdateTip(Guid id, HttpContext http, NpgsqlDataSource db)
    {
        var (text, status, error) = await ReadTip(http);
        if (error is not null)
        {
            return error;
        }

        await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
        var tip = await conn.QuerySingleOrDefaultAsync<AdminTip>(
            "UPDATE tips SET text = @text, status = @status, updated_at = now() WHERE id = @id RETURNING " + TipColumns,
            new { id, text, status });
        if (tip is null)
        {
            return Problems.Create(404, "tip_not_found", "There is no tip with this id.");
        }

        return Results.Ok(tip);
    }

    // Newest 50 across all devices, text only. The request hash is not selected.
    private static async Task<IResult> ListEntries(HttpContext http, NpgsqlDataSource db)
    {
        await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
        var entries = await conn.QueryAsync<AdminEntry>(
            "SELECT id, device_id AS deviceid, text, client_created_at AS clientcreatedat, received_at AS receivedat " +
            "FROM entries ORDER BY received_at DESC, id LIMIT 50");
        return Results.Ok(new { entries });
    }
}
