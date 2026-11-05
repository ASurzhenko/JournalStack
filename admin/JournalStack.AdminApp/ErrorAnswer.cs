namespace JournalStack.AdminApp;

// Every failure answer of the admin app goes through here, so the format changes in one place.
public static class ErrorAnswer
{
    public static Task WriteAsync(HttpContext http, int status, string code, string message, string? requestId = null)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (requestId is not null)
        {
            extensions["requestId"] = requestId;
        }
        return Results.Problem(statusCode: status, title: message, extensions: extensions).ExecuteAsync(http);
    }

    // What the person is told for each way the call to the API can fail. Returns null for a failure that is not one of them.
    public static (int Status, string Code, string Message)? Describe(Exception ex)
    {
        switch (ex)
        {
            case HttpRequestException:
                return (503, "api_unreachable", "The server cannot be reached. Try again later.");
            case OperationCanceledException:
                return (504, "api_timeout", "The server did not answer in time. A save may or may not have happened; check the list.");
            case ApiException { Code: "admin_key" }:
                return (502, "admin_key_refused", "The admin app is not set up to call the server.");
            case ApiException { Code: "admin_key_not_configured" }:
                return (503, "admin_key_not_configured", "The server has no admin key set.");
            case ApiException { Code: "database_unavailable" }:
                return (503, "database_unavailable", "The database is not reachable. Try again later.");
            case ApiException { Code: "server_error" }:
                return (502, "api_error", "Something went wrong on the server.");
            case ApiException:
                return (502, "api_error", "The server gave an unexpected answer.");
            default:
                return null;
        }
    }
}
