using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;

namespace JournalStack.Server.Tests;

[Collection("database")]
public class AdminContentTests
{
    private readonly PostgresFixture _db;

    public AdminContentTests(PostgresFixture db)
    {
        _db = db;
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> CreateTipAsync(HttpClient admin, string text, string status)
    {
        var response = await admin.PostAsync("/admin/api/tips", AdminRequests.Json(new { text, status }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonOf(response)).GetProperty("id").GetGuid();
    }

    private static async Task SaveTipAsync(HttpClient admin, Guid id, string text, string status)
    {
        var response = await admin.PutAsync($"/admin/api/tips/{id}", AdminRequests.Json(new { text, status }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<string?> TodayAsync(HttpClient device)
    {
        var response = await device.GetAsync("/api/v1/tip/today");
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonOf(response)).GetProperty("text").GetString();
    }

    private async Task<(TestApp App, HttpClient Admin)> StartSignedInAsync()
    {
        var app = await TestApp.StartAdminAsync(_db);
        return (app, app.AdminClient(await app.LoginAsync()));
    }

    [Fact]
    public async Task Creating_a_tip_answers_201_with_the_trimmed_text_and_stores_it()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;

        var response = await admin.PostAsync("/admin/api/tips", AdminRequests.Json(new { text = "  Drink water  ", status = "published" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await JsonOf(response);
        Assert.Equal("Drink water", json.GetProperty("text").GetString());
        Assert.Equal("published", json.GetProperty("status").GetString());
        Assert.NotEqual(default, json.GetProperty("updatedAt").GetDateTime());
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        Assert.Equal("Drink water", await conn.ExecuteScalarAsync<string>("SELECT text FROM tips WHERE id = @id", new { id = json.GetProperty("id").GetGuid() }));
    }

    public static TheoryData<string, int, string> BadTips()
    {
        string Tip(string text, string status) => JsonSerializer.Serialize(new { text, status });
        return new TheoryData<string, int, string>
        {
            { "", 400, "invalid_json" },
            { "{{{", 400, "invalid_json" },
            { "[]", 400, "invalid_json" },
            { "{\"status\":\"draft\"}", 422, "invalid_text" },
            { "{\"text\":5,\"status\":\"draft\"}", 422, "invalid_text" },
            { Tip("   ", "draft"), 422, "invalid_text" },
            { Tip("a\u0000b", "draft"), 422, "invalid_text" },
            { Tip(new string('x', 2001), "draft"), 422, "invalid_text" },
            { "{\"text\":\"ok\"}", 422, "invalid_status" },
            { Tip("ok", "Published"), 422, "invalid_status" },
            { Tip("ok", "archived"), 422, "invalid_status" },
            { "{\"text\":\"ok\",\"status\":1}", 422, "invalid_status" },
            { Tip(new string('x', 17 * 1024), "draft"), 413, "payload_too_large" },
        };
    }

    [Theory]
    [MemberData(nameof(BadTips))]
    public async Task A_bad_tip_is_refused_with_its_own_code_and_stores_nothing(string body, int status, string code)
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;

        var response = await admin.PostAsync("/admin/api/tips", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(code, await AdminRequests.CodeAsync(response));
        Assert.Equal(0, await app.CountAsync("tips"));
    }

    [Fact]
    public async Task Saving_a_tip_again_replaces_it_and_the_last_write_wins()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;
        var id = await CreateTipAsync(admin, "first", "draft");
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        var before = await conn.ExecuteScalarAsync<DateTime>("SELECT updated_at FROM tips WHERE id = @id", new { id });

        await SaveTipAsync(admin, id, "second", "published");
        await SaveTipAsync(admin, id, "third", "draft");

        var row = await conn.QuerySingleAsync("SELECT text, status, updated_at FROM tips WHERE id = @id", new { id });
        Assert.Equal("third", (string)row.text);
        Assert.Equal("draft", (string)row.status);
        Assert.True((DateTime)row.updated_at > before);
        Assert.Equal(1, await app.CountAsync("tips"));
    }

    [Fact]
    public async Task Saving_an_unknown_tip_is_404_and_creates_nothing()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;

        var unknown = await admin.PutAsync($"/admin/api/tips/{Guid.NewGuid()}", AdminRequests.Json(new { text = "x", status = "draft" }));
        var notAnId = await admin.PutAsync("/admin/api/tips/abc", AdminRequests.Json(new { text = "x", status = "draft" }));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("tip_not_found", await AdminRequests.CodeAsync(unknown));
        Assert.Equal(HttpStatusCode.NotFound, notAnId.StatusCode);
        Assert.Equal(0, await app.CountAsync("tips"));
    }

    [Fact]
    public async Task The_device_gets_the_published_tip_that_was_saved_last()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;
        var device = app.ClientFor((await app.RegisterAsync()).Token);

        var a = await CreateTipAsync(admin, "tip A", "published");
        Assert.Equal("tip A", await TodayAsync(device));

        var b = await CreateTipAsync(admin, "tip B", "published");
        Assert.Equal("tip B", await TodayAsync(device));

        await SaveTipAsync(admin, a, "tip A", "published");
        Assert.Equal("tip A", await TodayAsync(device));

        await SaveTipAsync(admin, a, "tip A", "draft");
        Assert.Equal("tip B", await TodayAsync(device));

        await SaveTipAsync(admin, b, "tip B", "draft");
        Assert.Null(await TodayAsync(device));
    }

    [Fact]
    public async Task The_first_published_row_of_the_admin_list_is_the_tip_the_device_gets()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;
        var device = app.ClientFor((await app.RegisterAsync()).Token);
        await CreateTipAsync(admin, "published old", "published");
        await CreateTipAsync(admin, "published newer", "published");
        await CreateTipAsync(admin, "draft newest", "draft");

        var tips = (await JsonOf(await admin.GetAsync("/admin/api/tips"))).GetProperty("tips").EnumerateArray().ToList();

        Assert.Equal("draft newest", tips[0].GetProperty("text").GetString());
        var firstPublished = tips.First(tip => tip.GetProperty("status").GetString() == "published");
        Assert.Equal(await TodayAsync(device), firstPublished.GetProperty("text").GetString());
    }

    [Fact]
    public async Task The_tip_list_has_drafts_and_published_newest_first_and_stops_at_100()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        await conn.ExecuteAsync(
            "INSERT INTO tips (text, status, updated_at) SELECT 'tip ' || n, CASE WHEN n % 2 = 0 THEN 'draft' ELSE 'published' END, now() - n * interval '1 minute' FROM generate_series(1, 101) AS n");

        var tips = (await JsonOf(await admin.GetAsync("/admin/api/tips"))).GetProperty("tips").EnumerateArray().ToList();

        Assert.Equal(100, tips.Count);
        Assert.Equal("tip 1", tips[0].GetProperty("text").GetString());
        Assert.Equal("tip 100", tips[99].GetProperty("text").GetString());
        Assert.Contains(tips, tip => tip.GetProperty("status").GetString() == "draft");
        Assert.Contains(tips, tip => tip.GetProperty("status").GetString() == "published");
    }

    [Fact]
    public async Task An_entry_posted_by_a_device_shows_in_the_admin_list_unchanged_and_newest_first()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;
        var (deviceId, token) = await app.RegisterAsync();
        var device = app.ClientFor(token);
        const string script = "<img src=x onerror=alert(1)>";
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var texts = new[] { "ENTRYMARKER oldest", script, "ENTRYMARKER newest" };
        for (var i = 0; i < 3; i++)
        {
            var posted = await device.PostEntryAsync(EntryRequests.Body(ids[i].ToString(), texts[i], "2026-10-24T12:00:00Z"));
            Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        }
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        await conn.ExecuteAsync("UPDATE entries SET received_at = now() - interval '3 hours' WHERE id = @id", new { id = ids[0] });
        await conn.ExecuteAsync("UPDATE entries SET received_at = now() - interval '2 hours' WHERE id = @id", new { id = ids[1] });
        await conn.ExecuteAsync("UPDATE entries SET received_at = now() - interval '1 hours' WHERE id = @id", new { id = ids[2] });

        var response = await admin.GetAsync("/admin/api/entries");
        var entries = (await JsonOf(response)).GetProperty("entries").EnumerateArray().ToList();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { texts[2], texts[1], texts[0] }, entries.Select(entry => entry.GetProperty("text").GetString()).ToArray());
        Assert.All(entries, entry => Assert.Equal(deviceId, entry.GetProperty("deviceId").GetGuid()));
        Assert.DoesNotContain("ENTRYMARKER", app.Logs.All);
    }

    [Fact]
    public async Task The_entries_list_stops_at_50()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;
        var (deviceId, _) = await app.RegisterAsync();
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        await conn.ExecuteAsync(
            "INSERT INTO entries (id, device_id, text, client_created_at, request_hash, received_at) " +
            "SELECT gen_random_uuid(), @deviceId, 'row ' || n, now(), '\\x00'::bytea, now() - n * interval '1 second' FROM generate_series(1, 51) AS n",
            new { deviceId });

        var entries = (await JsonOf(await admin.GetAsync("/admin/api/entries"))).GetProperty("entries").EnumerateArray().ToList();

        Assert.Equal(50, entries.Count);
        Assert.Equal("row 1", entries[0].GetProperty("text").GetString());
        Assert.Equal("row 50", entries[49].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Entries_with_the_earliest_and_latest_client_dates_are_listed()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;
        var device = app.ClientFor((await app.RegisterAsync()).Token);
        await device.PostEntryAsync(EntryRequests.Body(Guid.NewGuid().ToString(), "early", "0001-01-01T00:00:00Z"));
        await device.PostEntryAsync(EntryRequests.Body(Guid.NewGuid().ToString(), "late", "9999-12-31T23:59:59.9999999Z"));

        var response = await admin.GetAsync("/admin/api/entries");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await JsonOf(response)).GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task The_entries_list_has_exactly_five_properties_and_no_request_hash()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;
        var device = app.ClientFor((await app.RegisterAsync()).Token);
        await device.PostEntryAsync(EntryRequests.Golden());

        var entry = (await JsonOf(await admin.GetAsync("/admin/api/entries"))).GetProperty("entries")[0];

        Assert.Equal(
            new[] { "clientCreatedAt", "deviceId", "id", "receivedAt", "text" },
            entry.EnumerateObject().Select(property => property.Name).Order().ToArray());
    }

    [Fact]
    public async Task When_the_tips_table_is_gone_a_write_is_never_2xx_and_the_text_is_not_leaked()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        await conn.ExecuteAsync("ALTER TABLE tips RENAME TO tips_gone");
        var body = AdminRequests.Json(new { text = "TIPMARKER", status = "draft" });

        var created = await admin.PostAsync("/admin/api/tips", body);
        var saved = await admin.PutAsync($"/admin/api/tips/{Guid.NewGuid()}", AdminRequests.Json(new { text = "TIPMARKER", status = "draft" }));

        foreach (var response in new[] { created, saved })
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("server_error", await AdminRequests.CodeAsync(response));
            Assert.DoesNotContain("TIPMARKER", await response.Content.ReadAsStringAsync());
        }
        Assert.DoesNotContain("TIPMARKER", app.Logs.All);
        await conn.ExecuteAsync("ALTER TABLE tips_gone RENAME TO tips");
        Assert.Equal(0, await app.CountAsync("tips"));
    }
}
