namespace JournalStack.Server;

// Every error is a ProblemDetails JSON body with a stable `code`. The Unity client reads `code`, never the title.
public static class Problems
{
    public static IResult Create(int status, string code, string title, string? requestId = null)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (requestId is not null)
        {
            extensions["requestId"] = requestId;
        }
        return Results.Problem(statusCode: status, title: title, extensions: extensions);
    }

    public static IResult Unauthorized()
    {
        return Create(401, "unauthorized", "Missing or invalid device token.");
    }
}
