using JournalStack.Server;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;

// `migrate` applies pending database scripts and exits. Railway and compose run it before the server starts.
if (args.Length > 0 && args[0] == "migrate")
{
    var migrateConfig = new ConfigurationBuilder().AddEnvironmentVariables().Build();
    return Migrator.Run(Database.ConnectionString(migrateConfig));
}

// `hash-password` reads one line from the console and prints the hash for ADMIN_PASSWORD_HASH. It needs no database.
if (args.Length > 0 && args[0] == "hash-password")
{
    return Admin.RunHashPassword();
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(sp => NpgsqlDataSource.Create(Database.ConnectionString(sp.GetRequiredService<IConfiguration>())));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Limits>();

var app = builder.Build();

// Stop at startup, not at the first request, when a PG variable is missing.
app.Services.GetRequiredService<NpgsqlDataSource>();

// The server still starts without a hash, so the device API works. Only admin login is off.
if (string.IsNullOrEmpty(app.Configuration["ADMIN_PASSWORD_HASH"]))
{
    app.Logger.LogWarning("ADMIN_PASSWORD_HASH is not set. Admin login is disabled.");
}

// Railway ends TLS in front of the server. Only the scheme is read from the proxy, never the client address.
var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto };
forwarded.KnownNetworks.Clear();
forwarded.KnownProxies.Clear();
app.UseForwardedHeaders(forwarded);

// The framework's own handler logs the whole exception, and a database error can carry row data.
// This one logs the exception type and a request id, and answers with a stable code.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        // The client went away. Nothing to answer.
    }
    catch (Exception ex)
    {
        var requestId = context.TraceIdentifier;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Unhandled");
        logger.LogError("Unhandled {ExceptionType}, request {RequestId}", ex.GetType().Name, requestId);

        var result = ex is NpgsqlException { IsTransient: true }
            ? Problems.Create(503, "database_unavailable", "The database is not reachable. Try again later.", requestId)
            : Problems.Create(500, "server_error", "Something went wrong on the server.", requestId);
        await result.ExecuteAsync(context);
    }
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // The admin pages load only their own files, and may not be framed. They are re-checked on every visit.
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; frame-ancestors 'none'";
        context.Context.Response.Headers.CacheControl = "no-cache";
    },
});

Api.Map(app);

app.Run();
return 0;

public partial class Program
{
}
