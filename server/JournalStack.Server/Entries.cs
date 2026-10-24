using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace JournalStack.Server;

public record EntryRow(Guid Id, Guid DeviceId, string Text, DateTime ClientCreatedAt, DateTime ReceivedAt, byte[] RequestHash);

public static class Entries
{
    // 2000 characters, each escaped as \uXXXX, is about 12 KB, so 16 KiB covers every legal request without padding.
    public const int BodyLimit = 16 * 1024;
    public const int MaxTextLength = 2000;

    // Strict on purpose: a format with an optional offset would accept a time with no offset and read it as server-local.
    private static readonly string[] CreatedAtFormats =
    {
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
    };

    private const string Columns =
        "id, device_id AS deviceid, text, client_created_at AS clientcreatedat, received_at AS receivedat, request_hash AS requesthash";

    public static void Map(RouteGroupBuilder device)
    {
        device.MapPost("/entries", Post);
        device.MapGet("/entries", List);
    }

    // Frozen format. Later fields are appended, never inserted: the image part after the second "\n", then "\nmood=N".
    // The fields are hashed exactly as received, so a change to the trimming rule cannot change what a stored row matches.
    public static byte[] Hash(string rawText, string rawCreatedAt)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(rawText + "\n" + rawCreatedAt + "\n"));
    }

    public static bool TryParseId(string? raw, out Guid id)
    {
        return Guid.TryParseExact(raw, "D", out id) && id != Guid.Empty;
    }

    // Length is counted in UTF-16 units, the same unit as C# and Unity string.Length.
    // Control characters other than newline, carriage return and tab are refused, which also keeps NUL out of PostgreSQL.
    public static bool TryCleanText(string? raw, out string text)
    {
        text = "";
        if (raw is null)
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length < 1 || trimmed.Length > MaxTextLength)
        {
            return false;
        }

        foreach (var c in trimmed)
        {
            if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')
            {
                return false;
            }
        }

        text = trimmed;
        return true;
    }

    // Format only. The value is display data, so a phone with a wrong clock must not cost the entry.
    public static bool TryParseCreatedAt(string? raw, out DateTime utc)
    {
        var ok = DateTimeOffset.TryParseExact(raw, CreatedAtFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed);
        utc = parsed.UtcDateTime;
        return ok;
    }

    private static async Task<IResult> Post(HttpContext http, NpgsqlDataSource db, Limits limits)
    {
        var deviceId = (Guid)http.Items[Api.DeviceIdKey]!;
        if (!limits.EntryPosts.TryTake(deviceId.ToString()))
        {
            return Problems.Create(429, "entry_limit", "Too many entries from this device. Try again later.");
        }

        if (!http.Request.HasJsonContentType())
        {
            return Problems.Create(415, "unsupported_media_type", "The body must be application/json.");
        }

        var body = await ReadBody(http.Request, BodyLimit, http.RequestAborted);
        if (body is null)
        {
            return Problems.Create(413, "payload_too_large", "The request body is too large.");
        }

        string? rawId;
        string? rawText;
        string? rawCreatedAt;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Problems.Create(400, "invalid_json", "The body must be a JSON object.");
            }

            rawId = ReadString(root, "id");
            rawText = ReadString(root, "text");
            rawCreatedAt = ReadString(root, "clientCreatedAt");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return Problems.Create(400, "invalid_json", "The body is not valid JSON.");
        }

        if (!TryParseId(rawId, out var id))
        {
            return Problems.Create(422, "invalid_id", "The id must be a UUID.");
        }
        if (!TryCleanText(rawText, out var text))
        {
            return Problems.Create(422, "invalid_text", $"The text must be 1 to {MaxTextLength} characters without control characters.");
        }
        if (!TryParseCreatedAt(rawCreatedAt, out var createdAt))
        {
            return Problems.Create(422, "invalid_created_at", "clientCreatedAt must be an ISO 8601 time with an offset.");
        }

        var hash = Hash(rawText!, rawCreatedAt!);

        await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, http.RequestAborted);

        var inserted = await conn.QuerySingleOrDefaultAsync<EntryRow>(
            "INSERT INTO entries (id, device_id, text, client_created_at, request_hash) " +
            "VALUES (@id, @deviceId, @text, @createdAt, @hash) ON CONFLICT (id) DO NOTHING RETURNING " + Columns,
            new { id, deviceId, text, createdAt, hash }, tx);
        if (inserted is not null)
        {
            await tx.CommitAsync(http.RequestAborted);
            return Results.Json(ToBody(inserted), statusCode: 201);
        }

        // Nothing was inserted, so the id exists. The transaction is never committed on this path.
        var stored = await conn.QuerySingleOrDefaultAsync<EntryRow>("SELECT " + Columns + " FROM entries WHERE id = @id", new { id }, tx);
        if (stored is null)
        {
            throw new InvalidOperationException("The entry conflicted on insert but was not found afterwards.");
        }
        if (stored.DeviceId != deviceId)
        {
            return Problems.Create(409, "id_taken", "This id is already used.");
        }
        if (!stored.RequestHash.AsSpan().SequenceEqual(hash))
        {
            return Problems.Create(409, "id_conflict", "This id was already used for different content.");
        }

        return Results.Json(ToBody(stored), statusCode: 200);
    }

    private static async Task<IResult> List(HttpContext http, NpgsqlDataSource db)
    {
        var deviceId = (Guid)http.Items[Api.DeviceIdKey]!;

        // Anything that is not a whole number of at least 1 gives the default.
        var limit = 50;
        if (int.TryParse(http.Request.Query["limit"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var asked) && asked >= 1)
        {
            limit = Math.Min(asked, 100);
        }

        await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
        var rows = await conn.QueryAsync<EntryRow>(
            "SELECT " + Columns + " FROM entries WHERE device_id = @deviceId ORDER BY received_at DESC, id LIMIT @limit",
            new { deviceId, limit });
        return Results.Ok(new { entries = rows.Select(ToBody) });
    }

    // Returns null when the body is over the limit. A declared length over the limit is refused before reading;
    // a body without a length (chunked) is counted while it is read.
    private static async Task<byte[]?> ReadBody(HttpRequest request, int limit, CancellationToken cancel)
    {
        if (request.ContentLength > limit)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancel)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    // A missing property and a property that is not a JSON string both read as null.
    private static string? ReadString(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }
        return null;
    }

    private static object ToBody(EntryRow row)
    {
        return new { id = row.Id, text = row.Text, clientCreatedAt = row.ClientCreatedAt, receivedAt = row.ReceivedAt };
    }
}
