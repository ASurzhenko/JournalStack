using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;

namespace JournalStack.Server.Tests;

[Collection("database")]
public class AdminContentTests
{
    private const string NoSuchTip = "That tip no longer exists.";

    private readonly PostgresFixture _db;

    public AdminContentTests(PostgresFixture db)
    {
        _db = db;
    }

    // null means the field is not sent at all.
    private static HttpContent TipForm(string? text, string? status, string? id = null)
    {
        var fields = new List<(string, string)>();
        if (id is not null)
        {
            fields.Add(("id", id));
        }
        if (text is not null)
        {
            fields.Add(("text", text));
        }
        if (status is not null)
        {
            fields.Add(("status", status));
        }
        return AdminRequests.Form(fields.ToArray());
    }

    private static async Task<Guid> CreateTipAsync(TestApp app, HttpClient admin, string text, string status)
    {
        var response = await admin.PostAsync("/admin", TipForm(text, status));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        return await conn.ExecuteScalarAsync<Guid>("SELECT id FROM tips WHERE text = @text ORDER BY updated_at DESC LIMIT 1", new { text });
    }

    private static async Task SaveTipAsync(HttpClient admin, Guid id, string text, string status)
    {
        var response = await admin.PostAsync("/admin", TipForm(text, status, id.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<string?> TodayAsync(HttpClient device)
    {
        var response = await device.GetAsync("/api/v1/tip/today");
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("text").GetString();
    }

    private static List<string> TipTexts(string html)
    {
        return Regex.Matches(html, "<div class=\"text\">(.*?)</div>", RegexOptions.Singleline).Select(match => match.Groups[1].Value).ToList();
    }

    private static List<string> EntryTexts(string html)
    {
        return Regex.Matches(html, "<td class=\"text\">(.*?)</td>", RegexOptions.Singleline).Select(match => match.Groups[1].Value).ToList();
    }

    private async Task<(TestApp App, HttpClient Admin)> StartSignedInAsync()
    {
        var app = await TestApp.StartAdminAsync(_db);
        return (app, app.AdminClient(await app.LoginAsync()));
    }

    [Fact]
    public async Task Creating_a_tip_redirects_to_the_list_and_stores_the_trimmed_text()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;

        var response = await admin.PostAsync("/admin", TipForm("  Drink water  ", "published"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin", response.Headers.Location!.OriginalString);
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        var row = await conn.QuerySingleAsync("SELECT text, status, updated_at FROM tips");
        Assert.Equal("Drink water", (string)row.text);
        Assert.Equal("published", (string)row.status);
        Assert.NotEqual(default, (DateTime)row.updated_at);
        Assert.Contains("Drink water", await AdminRequests.BodyAsync(await admin.GetAsync("/admin")));
    }

    // text, status, the message the editor must show.
    public static TheoryData<string?, string?, string> BadTips()
    {
        const string textMessage = "The text must be 1 to 2000 characters without control characters.";
        const string statusMessage = "The status must be draft or published.";
        return new TheoryData<string?, string?, string>
        {
            { null, "draft", textMessage },
            { "   ", "draft", textMessage },
            { "a\u0001b", "draft", textMessage },
            { new string('x', 2001), "draft", textMessage },
            { "ok", null, statusMessage },
            { "ok", "Published", statusMessage },
            { "ok", "archived", statusMessage },
        };
    }

    [Theory]
    [MemberData(nameof(BadTips))]
    public async Task A_bad_tip_is_a_422_with_its_own_message_and_stores_nothing(string? text, string? status, string message)
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;

        var response = await admin.PostAsync("/admin", TipForm(text, status));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(message, await AdminRequests.BodyAsync(response));
        Assert.Equal(0, await app.CountAsync("tips"));
    }

    // The framework refuses these before the page runs: an oversize value (the form limit) and a NUL (the form decoder).
    // Both end in the catch-all as a 500 with no echo, and nothing is stored.
    [Theory]
    [InlineData("oversize")]
    [InlineData("nul")]
    public async Task A_form_value_the_framework_refuses_is_not_a_success_and_stores_nothing(string kind)
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;
        var text = kind == "oversize" ? new string('x', 17 * 1024) : "a\u0000b";

        var response = await admin.PostAsync("/admin", TipForm(text, "draft"));

        // A 422 would mean the page ran, so the framework did not refuse it. The exact 500 pins that it did.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, await app.CountAsync("tips"));
    }

    [Fact]
    public async Task A_post_that_is_not_a_form_is_treated_as_an_empty_form_and_stores_nothing()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;

        var json = await admin.PostAsync("/admin", AdminRequests.Json(new { text = "from json", status = "draft" }));
        var empty = await admin.PostAsync("/admin", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, json.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Equal(0, await app.CountAsync("tips"));
    }

    [Fact]
    public async Task An_id_that_is_not_a_guid_is_refused_and_does_not_create_a_new_tip()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;

        var response = await admin.PostAsync("/admin", TipForm("valid text", "draft", id: "abc"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(NoSuchTip, await AdminRequests.BodyAsync(response));
        Assert.Equal(0, await app.CountAsync("tips"));
    }

    [Fact]
    public async Task A_refused_save_puts_the_typed_text_back_encoded_and_never_as_markup()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;

        var response = await admin.PostAsync("/admin", TipForm("</textarea><script>x</script>", "archived", id: "\"><script>y</script>"));
        var body = await AdminRequests.BodyAsync(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("&lt;/textarea&gt;", body);
        Assert.DoesNotContain("</textarea><script", body);
        Assert.DoesNotContain("<script>", body);
        Assert.Equal(0, await app.CountAsync("tips"));
    }

    [Fact]
    public async Task Saving_a_tip_again_replaces_it_and_the_last_write_wins()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;
        var id = await CreateTipAsync(app, admin, "first", "draft");
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
    public async Task Saving_an_unknown_tip_shows_a_message_and_creates_nothing()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;

        var response = await admin.PostAsync("/admin", TipForm("x", "draft", Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(NoSuchTip, await AdminRequests.BodyAsync(response));
        Assert.Equal(0, await app.CountAsync("tips"));
    }

    [Fact]
    public async Task The_edit_link_fills_the_editor_and_an_id_that_is_not_in_the_list_says_so()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;
        var id = await CreateTipAsync(app, admin, "fill me", "published");

        var filled = await AdminRequests.BodyAsync(await admin.GetAsync($"/admin?edit={id}"));
        var unknown = await AdminRequests.BodyAsync(await admin.GetAsync($"/admin?edit={Guid.NewGuid()}"));
        var junk = await AdminRequests.BodyAsync(await admin.GetAsync("/admin?edit=abc"));

        Assert.Contains($"name=\"id\" value=\"{id}\"", filled);
        Assert.Contains(">fill me</textarea>", filled);
        Assert.DoesNotContain(NoSuchTip, filled);
        foreach (var page in new[] { unknown, junk })
        {
            Assert.Contains(NoSuchTip, page);
            Assert.Contains("name=\"id\" value=\"\"", page);
        }
    }

    [Fact]
    public async Task The_device_gets_the_published_tip_that_was_saved_last()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;
        var device = app.ClientFor((await app.RegisterAsync()).Token);

        var a = await CreateTipAsync(app, admin, "tip A", "published");
        Assert.Equal("tip A", await TodayAsync(device));

        var b = await CreateTipAsync(app, admin, "tip B", "published");
        Assert.Equal("tip B", await TodayAsync(device));

        await SaveTipAsync(admin, a, "tip A", "published");
        Assert.Equal("tip A", await TodayAsync(device));

        await SaveTipAsync(admin, a, "tip A", "draft");
        Assert.Equal("tip B", await TodayAsync(device));

        await SaveTipAsync(admin, b, "tip B", "draft");
        Assert.Null(await TodayAsync(device));
    }

    [Fact]
    public async Task The_row_marked_as_shown_in_the_app_is_the_tip_the_device_gets()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;
        var device = app.ClientFor((await app.RegisterAsync()).Token);
        await CreateTipAsync(app, admin, "published old", "published");
        await CreateTipAsync(app, admin, "published newer", "published");
        await CreateTipAsync(app, admin, "draft newest", "draft");

        var html = await AdminRequests.BodyAsync(await admin.GetAsync("/admin"));

        Assert.Equal(new[] { "draft newest", "published newer", "published old" }, TipTexts(html));
        var marked = Regex.Match(html, "<div class=\"text\">([^<]*)</div>\\s*<div class=\"marker\">Shown in the app</div>");
        Assert.True(marked.Success);
        Assert.Equal(await TodayAsync(device), marked.Groups[1].Value);
        Assert.Single(Regex.Matches(html, "Shown in the app"));
    }

    [Fact]
    public async Task The_tip_list_has_drafts_and_published_newest_first_and_stops_at_100()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        await conn.ExecuteAsync(
            "INSERT INTO tips (text, status, updated_at) SELECT 'tip ' || n, CASE WHEN n % 2 = 0 THEN 'draft' ELSE 'published' END, now() - n * interval '1 minute' FROM generate_series(1, 101) AS n");

        var html = await AdminRequests.BodyAsync(await admin.GetAsync("/admin"));
        var texts = TipTexts(html);

        Assert.Equal(100, texts.Count);
        Assert.Equal("tip 1", texts[0]);
        Assert.Equal("tip 100", texts[99]);
        Assert.Contains("<td>draft</td>", html);
        Assert.Contains("<td>published</td>", html);
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

        var response = await admin.GetAsync("/admin/entries");
        var html = await AdminRequests.BodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { texts[2], texts[1], texts[0] }, EntryTexts(html).Select(WebUtility.HtmlDecode).ToArray());
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
        Assert.DoesNotContain("<img src=x", html);
        Assert.Equal(3, Regex.Matches(html, deviceId.ToString()[..8]).Count);
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

        var texts = EntryTexts(await AdminRequests.BodyAsync(await admin.GetAsync("/admin/entries")));

        Assert.Equal(50, texts.Count);
        Assert.Equal("row 1", texts[0]);
        Assert.Equal("row 50", texts[49]);
    }

    [Fact]
    public async Task Entries_with_the_earliest_and_latest_client_dates_are_listed()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;
        var device = app.ClientFor((await app.RegisterAsync()).Token);
        await device.PostEntryAsync(EntryRequests.Body(Guid.NewGuid().ToString(), "early", "0001-01-01T00:00:00Z"));
        await device.PostEntryAsync(EntryRequests.Body(Guid.NewGuid().ToString(), "late", "9999-12-31T23:59:59.9999999Z"));

        var response = await admin.GetAsync("/admin/entries");
        var html = await AdminRequests.BodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, EntryTexts(html).Count);
        Assert.Contains("0001-01-01 00:00:00", html);
        Assert.Contains("9999-12-31 23:59:59", html);
    }

    [Fact]
    public async Task The_entries_page_never_shows_the_request_hash()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;
        var device = app.ClientFor((await app.RegisterAsync()).Token);
        await device.PostEntryAsync(EntryRequests.Golden());

        var html = await AdminRequests.BodyAsync(await admin.GetAsync("/admin/entries"));

        Assert.Contains("Hello", html);
        Assert.DoesNotContain(EntryRequests.GoldenDigest, html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task When_the_tips_table_is_gone_a_write_is_never_a_redirect_and_the_text_is_not_leaked()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        await conn.ExecuteAsync("ALTER TABLE tips RENAME TO tips_gone");

        var created = await admin.PostAsync("/admin", TipForm("TIPMARKER", "draft"));
        var saved = await admin.PostAsync("/admin", TipForm("TIPMARKER", "draft", Guid.NewGuid().ToString()));

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
