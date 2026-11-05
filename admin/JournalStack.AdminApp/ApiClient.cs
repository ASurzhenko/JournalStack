using System.Net.Http.Json;
using System.Text.Json;

namespace JournalStack.AdminApp;

public record AdminTip(Guid Id, string Text, string Status, DateTime UpdatedAt);

public record AdminEntry(Guid Id, Guid DeviceId, string Text, DateTime ClientCreatedAt, DateTime ReceivedAt);

public record LoginResult(string Token, DateTime ExpiresAt);

// The API refused with a code the page may want to read. Status is what the API answered.
public sealed class ApiException : Exception
{
    public ApiException(int status, string code, string title, string? requestId)
        : base($"The API answered {status} {code}.")
    {
        Status = status;
        Code = code;
        Title = title;
        RequestId = requestId;
    }

    public int Status { get; }
    public string Code { get; }
    public string Title { get; }
    public string? RequestId { get; }
}

// The only outcome that signs the person out: the API said 401 session_invalid.
public sealed class SignedOutException : Exception
{
    public SignedOutException() : base("The admin session is no longer valid.")
    {
    }
}

// One method per route of the server's admin API. Every request carries the service key; signed-in requests carry the session token.
// Decisions are made on the `code` the API sends, never on the HTTP status alone.
public sealed class ApiClient
{
    private readonly HttpClient _http;
    private readonly string _key;
    private readonly ILogger<ApiClient> _logger;

    public ApiClient(HttpClient http, IConfiguration config, ILogger<ApiClient> logger)
    {
        _http = http;
        _key = config["ADMIN_API_KEY"] ?? "";
        _logger = logger;
    }

    public async Task<LoginResult> LoginAsync(string? password, CancellationToken cancel)
    {
        using var response = await SendAsync(HttpMethod.Post, "/admin/api/login", null, new { password }, cancel);
        return (await response.Content.ReadFromJsonAsync<LoginResult>(JsonSerializerOptions.Web, cancel))!;
    }

    public async Task LogoutAsync(string session, CancellationToken cancel)
    {
        using var response = await SendAsync(HttpMethod.Post, "/admin/api/logout", session, null, cancel);
    }

    public async Task CheckSessionAsync(string session, CancellationToken cancel)
    {
        using var response = await SendAsync(HttpMethod.Get, "/admin/api/session", session, null, cancel);
    }

    public async Task<List<AdminTip>> ListTipsAsync(string session, CancellationToken cancel)
    {
        using var response = await SendAsync(HttpMethod.Get, "/admin/api/tips", session, null, cancel);
        return (await response.Content.ReadFromJsonAsync<List<AdminTip>>(JsonSerializerOptions.Web, cancel))!;
    }

    // The three values go to the API as typed. The API checks them and answers 422 or 404 with a message for the editor.
    public async Task SaveTipAsync(string session, string id, string? text, string? status, CancellationToken cancel)
    {
        using var response = await SendAsync(HttpMethod.Post, "/admin/api/tips", session, new { id, text, status }, cancel);
    }

    public async Task<List<AdminEntry>> ListEntriesAsync(string session, CancellationToken cancel)
    {
        using var response = await SendAsync(HttpMethod.Get, "/admin/api/entries", session, null, cancel);
        return (await response.Content.ReadFromJsonAsync<List<AdminEntry>>(JsonSerializerOptions.Web, cancel))!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? session, object? body, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Admin-Key", _key);
        if (session is not null)
        {
            request.Headers.Add("X-Admin-Session", session);
        }
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await _http.SendAsync(request, cancel);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw await RefusalAsync(response, cancel);
        }
    }

    private async Task<Exception> RefusalAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        var status = (int)response.StatusCode;
        string code = "", title = "";
        string? requestId = null;
        try
        {
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancel);
            code = json.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "";
            title = json.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
            requestId = json.TryGetProperty("requestId", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            // Not a JSON answer from our API. The code stays empty and the caller reports an unexpected answer.
        }

        if (status == 401 && code == "session_invalid")
        {
            return new SignedOutException();
        }
        if (code == "admin_key")
        {
            _logger.LogWarning("The server refused ADMIN_API_KEY. The key on the admin app and on the server differ.");
        }
        return new ApiException(status, code, title, requestId);
    }
}
