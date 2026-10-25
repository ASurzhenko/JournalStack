using Dapper;
using Npgsql;

namespace JournalStack.Server;

public record AdminTip(Guid Id, string Text, string Status, DateTime UpdatedAt);

public record AdminEntry(Guid Id, Guid DeviceId, string Text, DateTime ClientCreatedAt, DateTime ReceivedAt);

// The data the admin pages read and write. The SQL is the only place the admin touches the database.
public static class AdminContent
{
    private const string TipColumns = "id, text, status, updated_at AS updatedat";

    // Returns the cleaned text and the status, or the message to show next to the editor.
    public static (string Text, string Status, string? Error) ValidateTip(string? rawText, string? rawStatus)
    {
        if (!Entries.TryCleanText(rawText, out var text))
        {
            return ("", "", $"The text must be 1 to {Entries.MaxTextLength} characters without control characters.");
        }
        if (rawStatus != "draft" && rawStatus != "published")
        {
            return ("", "", "The status must be draft or published.");
        }

        return (text, rawStatus, null);
    }

    public static async Task<List<AdminTip>> ListTips(NpgsqlDataSource db, CancellationToken cancel)
    {
        await using var conn = await db.OpenConnectionAsync(cancel);
        var tips = await conn.QueryAsync<AdminTip>(
            "SELECT " + TipColumns + " FROM tips ORDER BY updated_at DESC, id LIMIT 100");
        return tips.ToList();
    }

    public static async Task CreateTip(NpgsqlDataSource db, string text, string status, CancellationToken cancel)
    {
        await using var conn = await db.OpenConnectionAsync(cancel);
        await conn.ExecuteAsync("INSERT INTO tips (text, status) VALUES (@text, @status)", new { text, status });
    }

    // Last write wins. updated_at moves on every save, which is what makes a saved published tip the one devices get.
    // Returns false when no tip has this id.
    public static async Task<bool> UpdateTip(NpgsqlDataSource db, Guid id, string text, string status, CancellationToken cancel)
    {
        await using var conn = await db.OpenConnectionAsync(cancel);
        var rows = await conn.ExecuteAsync(
            "UPDATE tips SET text = @text, status = @status, updated_at = now() WHERE id = @id",
            new { id, text, status });
        return rows == 1;
    }

    // Newest 50 across all devices, text only. The request hash is not selected.
    public static async Task<List<AdminEntry>> ListEntries(NpgsqlDataSource db, CancellationToken cancel)
    {
        await using var conn = await db.OpenConnectionAsync(cancel);
        var entries = await conn.QueryAsync<AdminEntry>(
            "SELECT id, device_id AS deviceid, text, client_created_at AS clientcreatedat, received_at AS receivedat " +
            "FROM entries ORDER BY received_at DESC, id LIMIT 50");
        return entries.ToList();
    }
}
