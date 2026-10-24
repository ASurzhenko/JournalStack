using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;

namespace JournalStack.Server.Tests;

[Collection("database")]
public class DeviceTests
{
    private readonly PostgresFixture _db;

    public DeviceTests(PostgresFixture db)
    {
        _db = db;
    }

    [Fact]
    public async Task Register_returns_a_token_once_and_stores_only_its_hash()
    {
        await using var app = await TestApp.StartAsync(_db);

        var (deviceId, token) = await app.RegisterAsync();

        Assert.Equal(Tokens.Length, token.Length);
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        var stored = await conn.QuerySingleAsync<byte[]>("SELECT token_hash FROM devices WHERE id = @deviceId", new { deviceId });
        Assert.Equal(Tokens.Hash(token), stored);
        Assert.DoesNotContain(token, Convert.ToBase64String(stored));
        Assert.DoesNotContain(token, app.Logs.All);
    }

    [Fact]
    public async Task Register_response_is_not_cacheable()
    {
        await using var app = await TestApp.StartAsync(_db);

        var response = await app.CreateClient().PostAsync("/api/v1/devices", null);

        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Tip_today_is_204_without_a_published_tip_and_returns_the_latest_published_one()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (_, token) = await app.RegisterAsync();
        var client = app.ClientFor(token);

        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/v1/tip/today")).StatusCode);

        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        await conn.ExecuteAsync("INSERT INTO tips (text, status, updated_at) VALUES ('old', 'published', now() - interval '2 days')");
        await conn.ExecuteAsync("INSERT INTO tips (text, status, updated_at) VALUES ('newest but a draft', 'draft', now())");
        await conn.ExecuteAsync("INSERT INTO tips (text, status, updated_at) VALUES ('current', 'published', now() - interval '1 day')");

        var response = await client.GetAsync("/api/v1/tip/today");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("current", json.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer ")]
    [InlineData("Bearer short")]
    [InlineData("bearer abcdefghijklmnopqrstuvwxyzabcdefghijklmnopq")]
    [InlineData("Bearer ' OR '1'='1'; DROP TABLE devices;--xxxxxxxx")]
    [InlineData("Bearer abcdefghijklmnopqrstuvwxyzabcdefghijklmnopq")]
    public async Task Missing_or_wrong_credentials_get_401_with_a_code(string? header)
    {
        await using var app = await TestApp.StartAsync(_db);
        var client = app.CreateClient();
        if (header is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", header);
        }

        var response = await client.GetAsync("/api/v1/tip/today");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unauthorized", json.GetProperty("code").GetString());
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM devices"));
    }

    [Fact]
    public async Task A_huge_token_is_refused_without_a_database_lookup()
    {
        await using var app = await TestApp.StartAsync(_db);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + new string('a', 30000));

        var response = await client.GetAsync("/api/v1/tip/today");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all {{{")]
    [InlineData("{\"platform\":\"android\",\"appVersion\":\"0.0.1\",\"somethingNew\":[1,2,3]}")]
    public async Task Register_ignores_whatever_body_an_older_or_odd_client_sends(string body)
    {
        await using var app = await TestApp.StartAsync(_db);

        var response = await app.CreateClient().PostAsync("/api/v1/devices", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Tokens.Length, json.GetProperty("token").GetString()!.Length);
    }

    [Fact]
    public async Task Registration_is_capped_for_the_whole_server_and_the_window_resets()
    {
        await using var app = await TestApp.StartAsync(_db);
        var client = app.CreateClient();

        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/v1/devices", null)).StatusCode);
        }
        var refused = await client.PostAsync("/api/v1/devices", null);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("registration_limit", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        Assert.Equal(30, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM devices"));

        app.Clock.Now += TimeSpan.FromSeconds(61);

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/v1/devices", null)).StatusCode);
    }

    [Fact]
    public async Task One_device_is_capped_per_hour_and_other_devices_are_not_affected()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (_, tokenA) = await app.RegisterAsync();
        var (_, tokenB) = await app.RegisterAsync();
        var a = app.ClientFor(tokenA);

        for (var i = 0; i < 600; i++)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await a.GetAsync("/api/v1/tip/today")).StatusCode);
        }
        var refused = await a.GetAsync("/api/v1/tip/today");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("rate_limited", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await app.ClientFor(tokenB).GetAsync("/api/v1/tip/today")).StatusCode);

        app.Clock.Now += TimeSpan.FromMinutes(61);

        Assert.Equal(HttpStatusCode.NoContent, (await a.GetAsync("/api/v1/tip/today")).StatusCode);
    }

    [Fact]
    public async Task Only_the_forwarded_scheme_is_trusted_never_the_forwarded_address()
    {
        await using var app = await TestApp.StartAsync(_db);
        var client = app.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/test/scheme");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.9");

        var body = await (await client.SendAsync(request)).Content.ReadAsStringAsync();

        Assert.StartsWith("https", body);
        Assert.DoesNotContain("203.0.113.9", body);
    }

    [Fact]
    public async Task Health_answers_and_ready_checks_the_database()
    {
        await using var app = await TestApp.StartAsync(_db);
        var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }
}
