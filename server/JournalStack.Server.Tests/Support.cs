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
            next(app);
            app.Use(async (context, nextMiddleware) =>
            {
                switch (context.Request.Path.Value)
                {
                    case "/test/scheme":
                        await context.Response.WriteAsync($"{context.Request.Scheme} {context.Connection.RemoteIpAddress}");
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
}
