using JournalStack.AdminApp;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Nothing is read from builder.Configuration here. The client reads its address when it is created,
// and the required settings are checked after Build(), so a host that is given its settings later still sees them.
// The 10 second client timeout is the outer bound. The server's database connect timeout (5 seconds) must stay below it,
// or a database that stops answering shows as 504 here instead of 503.
builder.Services.AddHttpClient<ApiClient>((services, client) =>
{
    var config = services.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(config["ADMIN_API_URL"]!);
    client.Timeout = TimeSpan.FromSeconds(10);
})
// A redirect from whatever answers at the API address must never carry the key or the session token elsewhere.
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

// The built-in antiforgery token would die with the key ring on every deploy. Admin POSTs are guarded by the Origin check in Gate.
builder.Services.AddRazorPages(options => options.Conventions.ConfigureFilter(new IgnoreAntiforgeryTokenAttribute()));

// A form value over 16 Ki characters is refused here. The server's entry body limit is the same number.
builder.Services.Configure<FormOptions>(options => options.ValueLengthLimit = 16 * 1024);

var app = builder.Build();

// Stop at startup, not at the first request, when a setting is missing. The messages name the variable, never its value.
if (!Uri.TryCreate(app.Configuration["ADMIN_API_URL"], UriKind.Absolute, out _))
{
    throw new InvalidOperationException("Environment variable ADMIN_API_URL is not set or is not an absolute address.");
}
if ((app.Configuration["ADMIN_API_KEY"] ?? "").Length < 32)
{
    throw new InvalidOperationException("Environment variable ADMIN_API_KEY is not set or is shorter than 32 characters.");
}

// Railway ends TLS in front of the app. Only the scheme is read from the proxy, never the client address.
var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto };
forwarded.KnownIPNetworks.Clear();
forwarded.KnownProxies.Clear();
app.UseForwardedHeaders(forwarded);

// The one place a failure becomes an answer. It logs the exception type and a request id, never the text of the exception.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        // The browser went away. Nothing to answer.
    }
    catch (SignedOutException)
    {
        context.Response.Redirect("/admin/login");
    }
    catch (Exception ex)
    {
        var requestId = context.TraceIdentifier;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Unhandled");
        logger.LogError("Unhandled {ExceptionType}, request {RequestId}", ex.GetType().Name, requestId);

        // A 502 carries the server's own request id, so one id finds the line in both logs.
        var described = ErrorAnswer.Describe(ex);
        if (described is { } d)
        {
            await ErrorAnswer.WriteAsync(context, d.Status, d.Code, d.Message, (ex as ApiException)?.RequestId ?? requestId);
        }
        else
        {
            await ErrorAnswer.WriteAsync(context, 500, "server_error", "Something went wrong in the admin app.", requestId);
        }
    }
});

app.UseStaticFiles(new StaticFileOptions
{
    // The admin stylesheet is the only static file. It may not be framed and is re-checked on every visit.
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; frame-ancestors 'none'";
        context.Context.Response.Headers.CacheControl = "no-cache";
    },
});

app.Use(Gate.Run);

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/", () => Results.Redirect("/admin"));

// The cookie is cleared only after the API answered 204. Any failure keeps both the cookie and the session.
app.MapPost("/admin/logout", async (HttpContext http, ApiClient api) =>
{
    var token = http.Request.Cookies[Gate.CookieName];
    if (!string.IsNullOrEmpty(token))
    {
        await api.LogoutAsync(token, http.RequestAborted);
        http.Response.Cookies.Delete(Gate.CookieName, Gate.CookieFor(http, null));
    }
    return Results.Redirect("/admin/login");
});

app.MapRazorPages();

app.Run();

// The generated Program class is public. The test project also sees the server's Program, so this one is made internal to keep the name unambiguous there.
internal partial class Program
{
}
