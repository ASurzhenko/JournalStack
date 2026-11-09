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

    private static async Task<int> CountForAsync(TestApp app, string table, string column, Guid deviceId)
    {
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        return await conn.ExecuteScalarAsync<int>($"SELECT count(*) FROM {table} WHERE {column} = @deviceId", new { deviceId });
    }

    private static async Task PostNewEntryAsync(HttpClient client, string text)
    {
        var body = EntryRequests.Body(Guid.NewGuid().ToString(), text, EntryRequests.GoldenCreatedAt);
        Assert.Equal(HttpStatusCode.Created, (await client.PostEntryAsync(body)).StatusCode);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Delete_me_answers_204_and_removes_this_device_and_its_entries_only()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (idA, tokenA) = await app.RegisterAsync();
        var (idB, tokenB) = await app.RegisterAsync();
        var a = app.ClientFor(tokenA);
        var b = app.ClientFor(tokenB);
        await PostNewEntryAsync(a, "ENTRYMARKER-A one");
        await PostNewEntryAsync(a, "ENTRYMARKER-A two");
        await PostNewEntryAsync(b, "entry of B");

        var response = await a.DeleteAsync("/api/v1/devices/me");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, await CountForAsync(app, "devices", "id", idA));
        Assert.Equal(0, await CountForAsync(app, "entries", "device_id", idA));
        Assert.Equal(1, await CountForAsync(app, "devices", "id", idB));
        Assert.Equal(1, await CountForAsync(app, "entries", "device_id", idB));
        Assert.DoesNotContain(tokenA, app.Logs.All);
        Assert.DoesNotContain("ENTRYMARKER-A", app.Logs.All);
    }

    [Fact]
    public async Task After_delete_the_old_token_gets_401_on_every_route_and_stores_nothing()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (_, token) = await app.RegisterAsync();
        var client = app.ClientFor(token);
        await PostNewEntryAsync(client, "before the delete");
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/devices/me")).StatusCode);

        await AssertProblemAsync(await client.GetAsync("/api/v1/tip/today"), HttpStatusCode.Unauthorized, "unauthorized");
        await AssertProblemAsync(await client.GetAsync("/api/v1/entries"), HttpStatusCode.Unauthorized, "unauthorized");
        await AssertProblemAsync(await client.PostEntryAsync(EntryRequests.Golden()), HttpStatusCode.Unauthorized, "unauthorized");
        await AssertProblemAsync(await client.DeleteAsync("/api/v1/devices/me"), HttpStatusCode.Unauthorized, "unauthorized");
        Assert.Equal(0, await app.CountAsync("entries"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer short")]
    [InlineData("Bearer abcdefghijklmnopqrstuvwxyzabcdefghijklmnopq")]
    public async Task Delete_without_a_token_or_with_a_wrong_one_answers_401_and_deletes_nothing(string? header)
    {
        await using var app = await TestApp.StartAsync(_db);
        var (_, token) = await app.RegisterAsync();
        await PostNewEntryAsync(app.ClientFor(token), "must survive");
        var client = app.CreateClient();
        if (header is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", header);
        }

        await AssertProblemAsync(await client.DeleteAsync("/api/v1/devices/me"), HttpStatusCode.Unauthorized, "unauthorized");

        Assert.Equal(1, await app.CountAsync("devices"));
        Assert.Equal(1, await app.CountAsync("entries"));
    }

    [Fact]
    public async Task After_delete_an_old_entry_id_is_free_so_only_the_app_can_stop_a_resend()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (_, tokenA) = await app.RegisterAsync();
        var (idC, tokenC) = await app.RegisterAsync();
        var a = app.ClientFor(tokenA);
        Assert.Equal(HttpStatusCode.Created, (await a.PostEntryAsync(EntryRequests.Golden())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync("/api/v1/devices/me")).StatusCode);

        var again = await app.ClientFor(tokenC).PostEntryAsync(EntryRequests.Golden());

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal(1, await app.CountAsync("entries"));
        Assert.Equal(1, await CountForAsync(app, "entries", "device_id", idC));
    }

    [Fact]
    public async Task When_the_delete_fails_inside_the_handler_the_answer_is_500_and_nothing_is_deleted()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (deviceId, token) = await app.RegisterAsync();
        var client = app.ClientFor(token);
        await PostNewEntryAsync(client, "must survive");
        await using (var conn = await PostgresFixture.OpenAsync(app.Settings))
        {
            await conn.ExecuteAsync(
                "CREATE FUNCTION refuse_delete() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'DELETEMARKER'; END $$");
            await conn.ExecuteAsync("CREATE TRIGGER refuse BEFORE DELETE ON devices FOR EACH ROW EXECUTE FUNCTION refuse_delete()");
        }

        var response = await client.DeleteAsync("/api/v1/devices/me");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("server_error", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("DELETEMARKER", body);
        Assert.DoesNotContain("DELETEMARKER", app.Logs.All);
        Assert.Equal(1, await CountForAsync(app, "devices", "id", deviceId));
        Assert.Equal(1, await CountForAsync(app, "entries", "device_id", deviceId));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/entries")).StatusCode);
    }

    [Fact]
    public async Task Two_deletes_at_once_give_no_server_error_and_the_device_is_gone()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (deviceId, token) = await app.RegisterAsync();
        var client = app.ClientFor(token);
        await PostNewEntryAsync(client, "to be deleted");

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.DeleteAsync("/api/v1/devices/me")));
        var codes = responses.Select(r => r.StatusCode).ToArray();

        Assert.All(codes, code => Assert.True(code == HttpStatusCode.NoContent || code == HttpStatusCode.Unauthorized, code.ToString()));
        Assert.Contains(HttpStatusCode.NoContent, codes);
        Assert.Equal(0, await CountForAsync(app, "devices", "id", deviceId));
        Assert.Equal(0, await CountForAsync(app, "entries", "device_id", deviceId));
    }

    [Fact]
    public async Task A_delete_whose_device_row_is_held_by_an_open_delete_waits_and_then_answers_204()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (deviceId, token) = await app.RegisterAsync();
        await using var holder = await PostgresFixture.OpenAsync(app.Settings);
        await using var tx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("DELETE FROM devices WHERE id = @deviceId", new { deviceId }, tx);

        var delete = app.ClientFor(token).DeleteAsync("/api/v1/devices/me");
        await Task.Delay(500);
        Assert.False(delete.IsCompleted);
        await tx.CommitAsync();

        var response = await delete;
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await CountForAsync(app, "devices", "id", deviceId));
    }

    [Fact]
    public async Task A_capped_device_gets_429_on_delete_keeps_its_data_and_can_delete_when_the_window_reopens()
    {
        await using var app = await TestApp.StartAsync(_db);
        var (deviceId, token) = await app.RegisterAsync();
        var client = app.ClientFor(token);
        await PostNewEntryAsync(client, "must survive the cap");
        for (var i = 0; i < 599; i++)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/v1/tip/today")).StatusCode);
        }

        await AssertProblemAsync(await client.DeleteAsync("/api/v1/devices/me"), HttpStatusCode.TooManyRequests, "rate_limited");

        Assert.Equal(1, await CountForAsync(app, "devices", "id", deviceId));
        Assert.Equal(1, await CountForAsync(app, "entries", "device_id", deviceId));
        app.Clock.Now += TimeSpan.FromMinutes(61);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/devices/me")).StatusCode);
        Assert.Equal(0, await CountForAsync(app, "devices", "id", deviceId));
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
