using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;

namespace JournalStack.Server.Tests;

[CollectionDefinition("database")]
public class DatabaseCollection : ICollectionFixture<PostgresFixture>
{
}

// One PostgreSQL container for the whole run. Every test gets its own empty database inside it.
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private int _counter;

    public Task InitializeAsync()
    {
        return _container.StartAsync();
    }

    public Task DisposeAsync()
    {
        return _container.DisposeAsync().AsTask();
    }

    public async Task<Dictionary<string, string?>> NewDatabaseAsync(bool migrate = true)
    {
        var name = $"test{Interlocked.Increment(ref _counter)}";
        await using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await create.ExecuteNonQueryAsync();
        }

        var settings = new Dictionary<string, string?>
        {
            ["PGHOST"] = _container.Hostname,
            ["PGPORT"] = _container.GetMappedPublicPort(5432).ToString(),
            ["PGUSER"] = "postgres",
            ["PGPASSWORD"] = "postgres",
            ["PGDATABASE"] = name,
        };
        if (migrate)
        {
            Assert.Equal(0, Migrator.Run(Database.ConnectionString(Config(settings))));

            // The migrator leaves one idle pooled connection per database. With a database per test they would pile up
            // until the server refuses new ones ("too many clients").
            NpgsqlConnection.ClearAllPools();
        }
        return settings;
    }

    public static IConfiguration Config(Dictionary<string, string?> settings)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    public static async Task<NpgsqlConnection> OpenAsync(Dictionary<string, string?> settings)
    {
        var conn = new NpgsqlConnection(Database.ConnectionString(Config(settings)));
        await conn.OpenAsync();
        return conn;
    }

    public static int UnusedPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

public sealed class FakeClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        return Now;
    }
}

// Collects every log line, with the full exception text, so a test can search it.
public sealed class LogCapture : ILoggerProvider
{
    private readonly List<string> _lines = new();

    public string All
    {
        get
        {
            lock (_lines)
            {
                return string.Join("\n", _lines);
            }
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new CaptureLogger(this, categoryName);
    }

    public void Dispose()
    {
    }

    private sealed class CaptureLogger : ILogger
    {
        private readonly LogCapture _owner;
        private readonly string _category;

        public CaptureLogger(LogCapture owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_owner._lines)
            {
                _owner._lines.Add($"{_category}: {formatter(state, exception)} {exception}");
            }
        }
    }
}

// Routes that exist only in tests, to reach code paths the real API does not have yet.
public sealed class TestRoutes : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // TestServer buffers every body and sets Content-Length itself. A request marked with this header
            // reaches the server without one, as a chunked request through a proxy would.
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.ContainsKey("X-Test-Chunked"))
                {
                    context.Request.ContentLength = null;
                }
                await nextMiddleware(context);
            });
            next(app);
            app.Use(async (context, nextMiddleware) =>
            {
                switch (context.Request.Path.Value)
                {
                    case "/test/scheme":
                        await context.Response.WriteAsync($"{context.Request.Scheme} {context.Connection.RemoteIpAddress}");
                        break;
                    case "/test/body-length":
                        await context.Response.WriteAsync(context.Request.ContentLength?.ToString() ?? "none");
                        break;
                    case "/test/boom":
                        throw new InvalidOperationException("SECRETMESSAGE");
                    case "/test/check-violation":
                        var db = context.RequestServices.GetRequiredService<NpgsqlDataSource>();
                        await using (var conn = await db.OpenConnectionAsync())
                        await using (var cmd = new NpgsqlCommand("INSERT INTO tips (text, status) VALUES (@text, 'draft')", conn))
                        {
                            cmd.Parameters.AddWithValue("text", "ROWMARKER" + new string('x', 2001));
                            await cmd.ExecuteNonQueryAsync();
                        }
                        break;
                    default:
                        await nextMiddleware(context);
                        break;
                }
            });
        };
    }
}

public sealed class TestApp : WebApplicationFactory<Program>
{
    public Dictionary<string, string?> Settings { get; }

    public FakeClock Clock { get; } = new();
    public LogCapture Logs { get; } = new();

    public TestApp(Dictionary<string, string?> settings)
    {
        Settings = settings;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Settings));
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IStartupFilter, TestRoutes>();
        });
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
    }

    public static async Task<TestApp> StartAsync(PostgresFixture db)
    {
        return new TestApp(await db.NewDatabaseAsync());
    }

    public async Task<(Guid DeviceId, string Token)> RegisterAsync()
    {
        var response = await CreateClient().PostAsync("/api/v1/devices", null);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return (json.GetProperty("deviceId").GetGuid(), json.GetProperty("token").GetString()!);
    }

    public HttpClient ClientFor(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        return client;
    }

    // Hashing costs about 100 ms, so it is done once.
    public const string AdminTestPassword = "correct horse battery staple";
    public static readonly string AdminTestHash = Admin.HashPassword(AdminTestPassword);

    public static async Task<TestApp> StartAdminAsync(PostgresFixture db, string? hash = null)
    {
        var settings = await db.NewDatabaseAsync();
        settings["ADMIN_PASSWORD_HASH"] = hash ?? AdminTestHash;
        return new TestApp(settings);
    }

    public const string TestOrigin = "http://localhost";

    // The client keeps no cookies and follows no redirects, so a test sees exactly what the server sent.
    // TestServer takes the Host from the request address, so the default Origin is the server's own. null sends none.
    public HttpClient AdminClient(string? session = null, string? origin = TestOrigin)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
        if (origin is not null)
        {
            client.DefaultRequestHeaders.Add("Origin", origin);
        }
        if (session is not null)
        {
            client.DefaultRequestHeaders.Add("Cookie", $"{Admin.CookieName}={session}");
        }
        return client;
    }

    public async Task<string> LoginAsync(string password = AdminTestPassword)
    {
        var response = await AdminClient().PostAsync("/admin/login", AdminRequests.Form(("password", password)));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        return AdminRequests.CookieValue(response)!;
    }

    public async Task<int> CountAsync(string table)
    {
        await using var conn = await PostgresFixture.OpenAsync(Settings);
        return await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, $"SELECT count(*) FROM {table}");
    }
}

public static class AdminRequests
{
    public static StringContent Json(object value)
    {
        return new StringContent(System.Text.Json.JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");
    }

    public static FormUrlEncodedContent Form(params (string Name, string Value)[] fields)
    {
        return new FormUrlEncodedContent(fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)));
    }

    // A page is read as it was sent. Text that was meant to be shown literally must appear encoded here.
    public static async Task<string> BodyAsync(HttpResponseMessage response)
    {
        return await response.Content.ReadAsStringAsync();
    }

    public static async Task<string> DecodedAsync(HttpResponseMessage response)
    {
        return System.Net.WebUtility.HtmlDecode(await BodyAsync(response));
    }

    public static IEnumerable<string> AllSetCookies(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues("Set-Cookie", out var values) ? values : Enumerable.Empty<string>();
    }

    // The value of the admin cookie in a Set-Cookie header, or null when the response sets none.
    public static string? CookieValue(HttpResponseMessage response)
    {
        var line = SetCookie(response);
        return line?.Split(';')[0][(Admin.CookieName.Length + 1)..];
    }

    public static string? SetCookie(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues("Set-Cookie", out var values) ? values.FirstOrDefault() : null;
    }

    public static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return json.GetProperty("code").GetString();
    }
}

// Builders for entry requests. The golden request is fixed so its stored hash can be compared with a digest computed outside the code.
public static class EntryRequests
{
    public const string GoldenId = "0b8e0d4e-6d0f-4f3b-9a53-0f2b7b6f3a11";
    public const string GoldenText = "Hello\nworld";
    public const string GoldenCreatedAt = "2026-10-24T12:00:00Z";
    public const string GoldenDigest = "b72d21ada45386dcd5494bc0bf731d0ee32750ed23a321b995b67670cad060ad";

    public static string Body(string id, string text, string createdAt)
    {
        return System.Text.Json.JsonSerializer.Serialize(new { id, text, clientCreatedAt = createdAt });
    }

    public static string Golden()
    {
        return Body(GoldenId, GoldenText, GoldenCreatedAt);
    }

    public static StringContent Json(string body)
    {
        return new StringContent(body, System.Text.Encoding.UTF8, "application/json");
    }

    public static Task<HttpResponseMessage> PostEntryAsync(this HttpClient client, string body)
    {
        return client.PostAsync("/api/v1/entries", Json(body));
    }
}

// Sends a small body but declares a large Content-Length, to reach the check that runs before the body is read.
public sealed class DeclaredLengthContent : HttpContent
{
    private readonly byte[] _bytes;
    private readonly long _declared;

    public DeclaredLengthContent(string body, long declared)
    {
        _bytes = System.Text.Encoding.UTF8.GetBytes(body);
        _declared = declared;
        Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
    }

    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
    {
        return stream.WriteAsync(_bytes, 0, _bytes.Length);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _declared;
        return true;
    }
}
