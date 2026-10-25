using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace JournalStack.Server.Tests;

[Collection("database")]
public class FailureTests
{
    private readonly PostgresFixture _db;

    public FailureTests(PostgresFixture db)
    {
        _db = db;
    }

    // A port nothing listens on stands in for a database that is down.
    private async Task<TestApp> StartWithDatabaseDownAsync()
    {
        var settings = await _db.NewDatabaseAsync(migrate: false);
        settings["PGPORT"] = PostgresFixture.UnusedPort().ToString();
        return new TestApp(settings);
    }

    [Fact]
    public async Task When_the_database_is_down_register_answers_503_and_never_succeeds()
    {
        await using var app = await StartWithDatabaseDownAsync();

        var response = await app.CreateClient().PostAsync("/api/v1/devices", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("database_unavailable", json.GetProperty("code").GetString());
        Assert.False(json.TryGetProperty("token", out _));
    }

    // The auth filter needs the database before the entry handler runs, so this covers the filter and the middleware mapping.
    // The handler's own persist path is covered by the renamed-table test in EntryTests.
    [Fact]
    public async Task When_the_database_is_down_an_entry_post_answers_503_and_never_2xx()
    {
        await using var app = await StartWithDatabaseDownAsync();

        var response = await app.ClientFor(new string('a', Tokens.Length)).PostEntryAsync(EntryRequests.Golden());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("database_unavailable", json.GetProperty("code").GetString());
    }

    // The session filter needs the database before any handler runs, so this covers the filter and the middleware mapping.
    // The page relies on the 503 to tell "server unavailable" from "signed out".
    [Fact]
    public async Task When_the_database_is_down_an_admin_call_answers_503_and_not_401()
    {
        await using var app = await StartWithDatabaseDownAsync();

        var response = await app.AdminClient(new string('a', Tokens.Length)).GetAsync("/admin/api/tips");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("database_unavailable", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task When_the_database_is_down_health_stays_up_and_ready_says_503()
    {
        await using var app = await StartWithDatabaseDownAsync();
        var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        var ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("database_unavailable", (await ready.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_failed_constraint_leaves_no_row_data_in_the_response_or_the_log()
    {
        await using var app = await TestApp.StartAsync(_db);

        var response = await app.CreateClient().GetAsync("/test/check-violation");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var requestId = JsonDocument.Parse(body).RootElement.GetProperty("requestId").GetString()!;
        Assert.Equal("server_error", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("ROWMARKER", body);
        Assert.DoesNotContain("ROWMARKER", app.Logs.All);
        Assert.Contains("PostgresException", app.Logs.All);
        Assert.Contains(requestId, app.Logs.All);
    }

    [Fact]
    public async Task An_unhandled_exception_logs_its_type_and_a_request_id_but_not_its_message()
    {
        await using var app = await TestApp.StartAsync(_db);

        var response = await app.CreateClient().GetAsync("/test/boom");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var requestId = JsonDocument.Parse(body).RootElement.GetProperty("requestId").GetString()!;
        Assert.DoesNotContain("SECRETMESSAGE", body);
        Assert.DoesNotContain("SECRETMESSAGE", app.Logs.All);
        Assert.Contains("InvalidOperationException", app.Logs.All);
        Assert.Contains(requestId, app.Logs.All);
    }
}
