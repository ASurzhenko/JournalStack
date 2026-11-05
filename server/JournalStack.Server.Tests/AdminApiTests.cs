using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;

namespace JournalStack.Server.Tests;

// The admin API, called directly the way the admin app calls it: JSON with the service key and the session token in headers.
[Collection("database")]
public class AdminApiTests
{
    private const string PasswordMarker = "WRONGPASSMARKER-1234";
    private const string NoSuchTip = "That tip no longer exists.";

    private readonly PostgresFixture _db;

    public AdminApiTests(PostgresFixture db)
    {
        _db = db;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, HttpContent? content = null)
    {
        return new HttpRequestMessage(method, path) { Content = content };
    }

    private static HttpContent Raw(string body)
    {
        return new StringContent(body, System.Text.Encoding.UTF8, "application/json");
    }

    // null means the property is not sent at all.
    private static HttpContent TipBody(string? text, string? status, string? id = null)
    {
        var fields = new Dictionary<string, string>();
        if (id is not null)
        {
            fields["id"] = id;
        }
        if (text is not null)
        {
            fields["text"] = text;
        }
        if (status is not null)
        {
            fields["status"] = status;
        }
        return AdminRequests.Json(fields);
    }

    private static async Task<Guid> CreateTipAsync(HttpClient api, string text, string status)
    {
        var response = await api.PostAsync("/admin/api/tips", TipBody(text, status));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task SaveTipAsync(HttpClient api, Guid id, string text, string status)
    {
        var response = await api.PostAsync("/admin/api/tips", TipBody(text, status, id.ToString()));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
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

    private async Task<(TestApp App, HttpClient Api)> StartSignedInAsync()
    {
        var app = await TestApp.StartAdminAsync(_db);
        return (app, app.AdminApi(session: await app.ApiLoginAsync()));
    }

    [Fact]
    public async Task Login_with_the_right_password_answers_a_token_sets_no_cookie_and_stores_only_a_keyed_hash()
    {
        await using var app = await TestApp.StartAdminAsync(_db);

        var response = await app.AdminApi().PostAsync("/admin/api/login", AdminRequests.Json(new { password = TestApp.AdminTestPassword }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = json.GetProperty("token").GetString()!;
        Assert.Equal(Tokens.Length, token.Length);
        Assert.Equal(app.Clock.Now.AddHours(8).UtcDateTime, json.GetProperty("expiresAt").GetDateTime().ToUniversalTime());
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        var stored = (await conn.QueryAsync<byte[]>("SELECT token_hash FROM admin_sessions")).Single();
        Assert.Equal(Admin.SessionHash(token, TestApp.AdminTestHash), stored);
        Assert.DoesNotContain(token, Convert.ToBase64String(stored));
        Assert.DoesNotContain(token, Convert.ToHexString(stored), StringComparison.OrdinalIgnoreCase);
    }

    // kind: wrong password, no password property, an empty object, a body that is not an object, text that is not JSON, an empty body.
    // Each row starts its own app, so every row has the full five attempts and the clock does not need to move.
    [Theory]
    [InlineData("wrong")]
    [InlineData("missing")]
    [InlineData("object")]
    [InlineData("array")]
    [InlineData("notjson")]
    [InlineData("empty")]
    [InlineData("number")]
    public async Task A_bad_login_is_a_401_invalid_credentials_and_leaves_no_session_and_no_trace(string kind)
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        HttpContent content = kind switch
        {
            "wrong" => AdminRequests.Json(new { password = PasswordMarker }),
            "missing" => AdminRequests.Json(new { other = PasswordMarker }),
            "object" => Raw("{}"),
            "array" => Raw("[\"" + PasswordMarker + "\"]"),
            "notjson" => Raw(PasswordMarker),
            "number" => AdminRequests.Json(new { password = 12345 }),
            _ => Raw(""),
        };

        var response = await app.AdminApi().PostAsync("/admin/api/login", content);
        var body = await AdminRequests.BodyAsync(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_credentials", await AdminRequests.CodeAsync(response));
        Assert.Equal(0, await app.CountAsync("admin_sessions"));
        Assert.DoesNotContain(PasswordMarker, body);
        Assert.DoesNotContain(PasswordMarker, app.Logs.All);
    }

    // A body that cannot be a password is still an attempt: five of them lock the sixth, which has the right password.
    [Fact]
    public async Task A_body_that_is_not_an_object_and_one_with_no_password_are_counted_attempts()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var api = app.AdminApi();

        await api.PostAsync("/admin/api/login", Raw("[1]"));
        await api.PostAsync("/admin/api/login", Raw("{}"));
        await api.PostAsync("/admin/api/login", Raw("not json"));
        await api.PostAsync("/admin/api/login", Raw(""));
        await api.PostAsync("/admin/api/login", Raw("{\"other\":1}"));
        var locked = await api.PostAsync("/admin/api/login", AdminRequests.Json(new { password = TestApp.AdminTestPassword }));

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal("login_locked", await AdminRequests.CodeAsync(locked));
    }

    [Fact]
    public async Task A_login_body_over_128_KiB_is_413_and_leaves_no_session_and_no_trace()
    {
        await using var app = await TestApp.StartAdminAsync(_db);

        var response = await app.AdminApi().PostAsync("/admin/api/login", AdminRequests.Json(new { password = PasswordMarker + new string('x', 128 * 1024) }));
        var body = await AdminRequests.BodyAsync(response);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("payload_too_large", await AdminRequests.CodeAsync(response));
        Assert.Equal(0, await app.CountAsync("admin_sessions"));
        Assert.DoesNotContain(PasswordMarker, body);
        Assert.DoesNotContain(PasswordMarker, app.Logs.All);
    }

    [Fact]
    public async Task The_sixth_login_in_a_minute_is_locked_even_with_the_right_password_until_the_window_passes()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var api = app.AdminApi();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var wrong = await api.PostAsync("/admin/api/login", AdminRequests.Json(new { password = PasswordMarker }));
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }
        var locked = await api.PostAsync("/admin/api/login", AdminRequests.Json(new { password = TestApp.AdminTestPassword }));

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal("login_locked", await AdminRequests.CodeAsync(locked));
        Assert.Equal(0, await app.CountAsync("admin_sessions"));

        app.Clock.Now = app.Clock.Now.AddSeconds(61);
        var open = await api.PostAsync("/admin/api/login", AdminRequests.Json(new { password = TestApp.AdminTestPassword }));
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
    }

    // The proof that the lockout runs before the body is read (D-019): reading it would answer 413.
    [Fact]
    public async Task The_lockout_answers_before_the_body_is_read_so_a_huge_sixth_attempt_is_a_429_and_not_a_413()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var api = app.AdminApi();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await api.PostAsync("/admin/api/login", AdminRequests.Json(new { password = PasswordMarker }));
        }

        var locked = await api.PostAsync("/admin/api/login", AdminRequests.Json(new { password = new string('x', 129 * 1024) }));

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
    }

    [Fact]
    public async Task With_no_hash_configured_login_says_so_and_counts_nothing()
    {
        await using var unset = await TestApp.StartAdminAsync(_db, hash: "");
        var api = unset.AdminApi();

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var response = await api.PostAsync("/admin/api/login", AdminRequests.Json(new { password = TestApp.AdminTestPassword }));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("admin_not_configured", await AdminRequests.CodeAsync(response));
        }
        Assert.Equal(0, await unset.CountAsync("admin_sessions"));
    }

    [Fact]
    public async Task A_garbage_hash_never_logs_anyone_in()
    {
        await using var app = await TestApp.StartAdminAsync(_db, hash: "garbage");

        var response = await app.AdminApi().PostAsync("/admin/api/login", AdminRequests.Json(new { password = TestApp.AdminTestPassword }));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("server_error", await AdminRequests.CodeAsync(response));
        Assert.Equal(0, await app.CountAsync("admin_sessions"));
    }

    [Fact]
    public async Task A_session_lasts_exactly_eight_hours()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.ApiLoginAsync();
        var start = app.Clock.Now;

        app.Clock.Now = start.AddHours(8).AddSeconds(-1);
        Assert.Equal(HttpStatusCode.NoContent, (await app.AdminApi(session: session).GetAsync("/admin/api/session")).StatusCode);

        app.Clock.Now = start.AddHours(8);
        var expired = await app.AdminApi(session: session).GetAsync("/admin/api/session");
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal("session_invalid", await AdminRequests.CodeAsync(expired));
    }

    [Fact]
    public async Task Logout_ends_the_session_for_good_and_logging_out_twice_is_fine()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.ApiLoginAsync();

        var logout = await app.AdminApi(session: session).PostAsync("/admin/api/logout", null);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(0, await app.CountAsync("admin_sessions"));
        var replay = await app.AdminApi(session: session).GetAsync("/admin/api/session");
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        var again = await app.AdminApi(session: session).PostAsync("/admin/api/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    }

    // Logout needs the key but not a session, so a dead session still gets its cookie cleared by the admin app.
    [Fact]
    public async Task Logout_with_an_empty_unknown_or_expired_session_is_204()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.ApiLoginAsync();
        app.Clock.Now = app.Clock.Now.AddHours(9);

        foreach (var token in new string?[] { null, "", new string('a', Tokens.Length), session })
        {
            var response = await app.AdminApi(session: token).PostAsync("/admin/api/logout", null);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
    }

    [Fact]
    public async Task When_the_delete_fails_logout_does_not_pretend_and_the_session_stays_valid()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.ApiLoginAsync();
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        await conn.ExecuteAsync("ALTER TABLE admin_sessions RENAME TO admin_sessions_gone");

        var logout = await app.AdminApi(session: session).PostAsync("/admin/api/logout", null);

        Assert.True((int)logout.StatusCode is 500 or 503);
        await conn.ExecuteAsync("ALTER TABLE admin_sessions_gone RENAME TO admin_sessions");
        Assert.Equal(HttpStatusCode.NoContent, (await app.AdminApi(session: session).GetAsync("/admin/api/session")).StatusCode);
    }

    [Fact]
    public async Task A_session_survives_a_second_server_on_the_same_database_and_hash()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.ApiLoginAsync();
        await using var second = new TestApp(app.Settings);

        var response = await second.AdminApi(session: session).GetAsync("/admin/api/session");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task A_new_password_hash_ends_every_old_session()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.ApiLoginAsync();
        const string newPassword = "a completely different phrase";
        var settings = new Dictionary<string, string?>(app.Settings) { ["ADMIN_PASSWORD_HASH"] = Admin.HashPassword(newPassword) };
        await using var rotated = new TestApp(settings);

        var old = await rotated.AdminApi(session: session).GetAsync("/admin/api/session");
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);

        var fresh = await rotated.ApiLoginAsync(newPassword);
        Assert.Equal(HttpStatusCode.NoContent, (await rotated.AdminApi(session: fresh).GetAsync("/admin/api/session")).StatusCode);
        var oldPassword = await rotated.AdminApi().PostAsync("/admin/api/login", AdminRequests.Json(new { password = TestApp.AdminTestPassword }));
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
    }

    [Fact]
    public async Task When_the_session_insert_fails_login_answers_a_server_error_and_no_token()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        await conn.ExecuteAsync("ALTER TABLE admin_sessions RENAME TO admin_sessions_gone");

        var response = await app.AdminApi().PostAsync("/admin/api/login", AdminRequests.Json(new { password = TestApp.AdminTestPassword }));
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("server_error", json.GetProperty("code").GetString());
        Assert.False(json.TryGetProperty("token", out _));
    }

    // The server has no pages and no cookie of its own any more.
    [Fact]
    public async Task The_server_never_sets_a_cookie_on_any_admin_answer()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var responses = new List<HttpResponseMessage>();

        responses.Add(await app.AdminApi().PostAsync("/admin/api/login", AdminRequests.Json(new { password = TestApp.AdminTestPassword })));
        var session = await app.ApiLoginAsync();
        var api = app.AdminApi(session: session);
        responses.Add(await api.GetAsync("/admin/api/session"));
        responses.Add(await api.PostAsync("/admin/api/tips", TipBody("a tip", "draft")));
        responses.Add(await api.GetAsync("/admin/api/tips"));
        responses.Add(await api.GetAsync("/admin/api/entries"));
        responses.Add(await api.PostAsync("/admin/api/logout", null));
        responses.Add(await app.AdminApi(key: null).GetAsync("/admin/api/tips"));

        Assert.Empty(responses.SelectMany(AdminRequests.AllSetCookies));
    }

    [Fact]
    public async Task Neither_the_password_nor_the_token_nor_the_hash_nor_the_key_reach_the_server_log()
    {
        await using var app = await TestApp.StartAdminAsync(_db);

        await app.AdminApi().PostAsync("/admin/api/login", AdminRequests.Json(new { password = PasswordMarker }));
        await app.AdminApi(key: "a-wrong-key-WRONGKEYMARKER-0123456789").GetAsync("/admin/api/tips");
        var session = await app.ApiLoginAsync();
        var api = app.AdminApi(session: session);
        await api.PostAsync("/admin/api/tips", TipBody("a tip", "draft"));
        await api.PostAsync("/admin/api/logout", null);

        var logs = app.Logs.All;
        Assert.DoesNotContain(PasswordMarker, logs);
        Assert.DoesNotContain(TestApp.AdminTestPassword, logs);
        Assert.DoesNotContain(session, logs);
        Assert.DoesNotContain(TestApp.AdminTestHash, logs);
        Assert.DoesNotContain(TestApp.AdminTestKey, logs);
        Assert.DoesNotContain("WRONGKEYMARKER", logs);
    }

    // A server started with a key that is too short warns by variable name and never prints the value.
    [Fact]
    public async Task A_short_key_makes_the_server_warn_by_name_without_printing_the_key()
    {
        const string shortKey = "SHORTKEYMARKER-123";
        await using var app = await TestApp.StartAdminAsync(_db, key: shortKey);

        await app.AdminApi(key: shortKey).GetAsync("/admin/api/tips");

        Assert.Contains("ADMIN_API_KEY", app.Logs.All);
        Assert.DoesNotContain("SHORTKEYMARKER", app.Logs.All);
    }

    // ---- content rules ----

    [Fact]
    public async Task Creating_a_tip_answers_201_with_its_id_and_stores_the_trimmed_text()
    {
        var (app, api) = await StartSignedInAsync();
        await using var _ = app;

        var response = await api.PostAsync("/admin/api/tips", TipBody("  Drink water  ", "published"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        var row = await conn.QuerySingleAsync("SELECT id, text, status, updated_at FROM tips");
        Assert.Equal(id, (Guid)row.id);
        Assert.Equal("Drink water", (string)row.text);
        Assert.Equal("published", (string)row.status);
        Assert.NotEqual(default, (DateTime)row.updated_at);
        var list = await (await api.GetAsync("/admin/api/tips")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Drink water", list[0].GetProperty("text").GetString());
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
            { "a\u0000b", "draft", textMessage },
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
        var (app, api) = await StartSignedInAsync();
        await using var _ = app;

        var response = await api.PostAsync("/admin/api/tips", TipBody(text, status));
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("invalid_tip", json.GetProperty("code").GetString());
        Assert.Equal(message, json.GetProperty("title").GetString());
        Assert.Equal(0, await app.CountAsync("tips"));
    }

    // The tip text of 16 Ki characters with non-ASCII letters is about 96 KiB of JSON, and must reach the rules.
    [Fact]
    public async Task A_long_non_ascii_tip_from_the_admin_form_reaches_the_rules_and_gets_the_editor_message()
    {
        var (app, api) = await StartSignedInAsync();
        await using var _ = app;

        var response = await api.PostAsync("/admin/api/tips", TipBody(new string('Ж', 16 * 1024), "draft"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("invalid_tip", await AdminRequests.CodeAsync(response));
    }

    [Fact]
    public async Task A_tip_body_over_128_KiB_is_413_and_stores_nothing()
    {
        var (app, api) = await StartSignedInAsync();
        await using var _ = app;

        var response = await api.PostAsync("/admin/api/tips", TipBody(new string('x', 129 * 1024), "draft"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, await app.CountAsync("tips"));
    }

    [Fact]
    public async Task A_tip_body_that_is_not_a_json_object_is_a_400_and_stores_nothing()
    {
        var (app, api) = await StartSignedInAsync();
        await using var _ = app;

        foreach (var body in new[] { "[1]", "not json", "" })
        {
            var response = await api.PostAsync("/admin/api/tips", Raw(body));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_json", await AdminRequests.CodeAsync(response));
        }
        Assert.Equal(0, await app.CountAsync("tips"));
    }

    [Fact]
    public async Task An_id_that_is_not_a_guid_is_a_404_and_does_not_create_a_new_tip()
    {
        var (app, api) = await StartSignedInAsync();
        await using var _ = app;

        var response = await api.PostAsync("/admin/api/tips", TipBody("valid text", "draft", id: "abc"));
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("tip_not_found", json.GetProperty("code").GetString());
        Assert.Equal(NoSuchTip, json.GetProperty("title").GetString());
        Assert.Equal(0, await app.CountAsync("tips"));
    }

    // The text rule is checked before the id, as the pages always did.
    [Fact]
    public async Task A_bad_text_with_a_bad_id_is_a_422_not_a_404()
    {
        var (app, api) = await StartSignedInAsync();
        await using var _ = app;

        var response = await api.PostAsync("/admin/api/tips", TipBody("   ", "draft", id: "abc"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Saving_a_tip_again_replaces_it_and_the_last_write_wins()
    {
        var (app, api) = await StartSignedInAsync();
        await using var _ = app;
        var id = await CreateTipAsync(api, "first", "draft");
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        var before = await conn.ExecuteScalarAsync<DateTime>("SELECT updated_at FROM tips WHERE id = @id", new { id });

        await SaveTipAsync(api, id, "second", "published");
        await SaveTipAsync(api, id, "third", "draft");

        var row = await conn.QuerySingleAsync("SELECT text, status, updated_at FROM tips WHERE id = @id", new { id });
        Assert.Equal("third", (string)row.text);
        Assert.Equal("draft", (string)row.status);
        Assert.True((DateTime)row.updated_at > before);
        Assert.Equal(1, await app.CountAsync("tips"));
    }

    [Fact]
    public async Task Saving_an_unknown_tip_is_a_404_and_creates_nothing()
    {
        var (app, api) = await StartSignedInAsync();
        await using var _ = app;

        var response = await api.PostAsync("/admin/api/tips", TipBody("x", "draft", Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("tip_not_found", await AdminRequests.CodeAsync(response));
        Assert.Equal(0, await app.CountAsync("tips"));
    }

    [Fact]
    public async Task The_device_gets_the_published_tip_that_was_saved_last()
    {
        var (app, api) = await StartSignedInAsync();
        await using var _ = app;
        var device = app.ClientFor((await app.RegisterAsync()).Token);

        var a = await CreateTipAsync(api, "tip A", "published");
        Assert.Equal("tip A", await TodayAsync(device));

        var b = await CreateTipAsync(api, "tip B", "published");
        Assert.Equal("tip B", await TodayAsync(device));

        await SaveTipAsync(api, a, "tip A", "published");
        Assert.Equal("tip A", await TodayAsync(device));

        await SaveTipAsync(api, a, "tip A", "draft");
        Assert.Equal("tip B", await TodayAsync(device));

        await SaveTipAsync(api, b, "tip B", "draft");
        Assert.Null(await TodayAsync(device));
    }

    [Fact]
    public async Task The_tip_list_has_drafts_and_published_newest_first_and_stops_at_100()
    {
        var (app, api) = await StartSignedInAsync();
        await using var _ = app;
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        await conn.ExecuteAsync(
            "INSERT INTO tips (text, status, updated_at) SELECT 'tip ' || n, CASE WHEN n % 2 = 0 THEN 'draft' ELSE 'published' END, now() - n * interval '1 minute' FROM generate_series(1, 101) AS n");

        var list = await (await api.GetAsync("/admin/api/tips")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(100, list.GetArrayLength());
        Assert.Equal("tip 1", list[0].GetProperty("text").GetString());
        Assert.Equal("tip 100", list[99].GetProperty("text").GetString());
        Assert.Equal("published", list[0].GetProperty("status").GetString());
        Assert.Equal("draft", list[1].GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_entry_posted_by_a_device_is_listed_unchanged_newest_first_with_exactly_five_properties()
    {
        var (app, api) = await StartSignedInAsync();
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

        var response = await api.GetAsync("/admin/api/entries");
        var list = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { texts[2], texts[1], texts[0] }, list.EnumerateArray().Select(e => e.GetProperty("text").GetString()).ToArray());
        Assert.All(list.EnumerateArray(), e => Assert.Equal(deviceId, e.GetProperty("deviceId").GetGuid()));
        Assert.Equal(
            new[] { "clientCreatedAt", "deviceId", "id", "receivedAt", "text" },
            list[0].EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("ENTRYMARKER", app.Logs.All);
    }

    [Fact]
    public async Task The_entries_list_stops_at_50_and_never_carries_the_request_hash()
    {
        var (app, api) = await StartSignedInAsync();
        await using var _ = app;
        var (deviceId, token) = await app.RegisterAsync();
        await app.ClientFor(token).PostEntryAsync(EntryRequests.Golden());
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        await conn.ExecuteAsync(
            "INSERT INTO entries (id, device_id, text, client_created_at, request_hash, received_at) " +
            "SELECT gen_random_uuid(), @deviceId, 'row ' || n, now(), '\\x00'::bytea, now() - n * interval '1 second' FROM generate_series(1, 51) AS n",
            new { deviceId });

        var body = await AdminRequests.BodyAsync(await api.GetAsync("/admin/api/entries"));
        var list = JsonDocument.Parse(body).RootElement;

        Assert.Equal(50, list.GetArrayLength());
        Assert.DoesNotContain(EntryRequests.GoldenDigest, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("requestHash", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Entries_with_the_earliest_and_latest_client_dates_are_listed()
    {
        var (app, api) = await StartSignedInAsync();
        await using var _ = app;
        var device = app.ClientFor((await app.RegisterAsync()).Token);
        await device.PostEntryAsync(EntryRequests.Body(Guid.NewGuid().ToString(), "early", "0001-01-01T00:00:00Z"));
        await device.PostEntryAsync(EntryRequests.Body(Guid.NewGuid().ToString(), "late", "9999-12-31T23:59:59.9999999Z"));

        var response = await api.GetAsync("/admin/api/entries");
        var list = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, list.GetArrayLength());
    }

    [Fact]
    public async Task When_the_tips_table_is_gone_a_write_is_a_server_error_and_the_text_is_not_leaked()
    {
        var (app, api) = await StartSignedInAsync();
        await using var _ = app;
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        await conn.ExecuteAsync("ALTER TABLE tips RENAME TO tips_gone");

        var created = await api.PostAsync("/admin/api/tips", TipBody("TIPMARKER", "draft"));
        var saved = await api.PostAsync("/admin/api/tips", TipBody("TIPMARKER", "draft", Guid.NewGuid().ToString()));

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

    [Fact]
    public async Task The_old_admin_pages_and_files_are_gone_from_the_server()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var client = app.CreateClient();

        foreach (var path in new[] { "/admin", "/admin/login", "/admin/entries", "/admin/admin.css", "/admin/admin.js", "/admin/index.html" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/admin/login", AdminRequests.Form(("password", "x")))).StatusCode);
    }

    // ---- the service key ----

    // Every route, each with a valid session, so the only thing missing is the key.
    private static readonly (HttpMethod Method, string Path)[] AllRoutes =
    {
        (HttpMethod.Post, "/admin/api/login"),
        (HttpMethod.Post, "/admin/api/logout"),
        (HttpMethod.Get, "/admin/api/session"),
        (HttpMethod.Get, "/admin/api/tips"),
        (HttpMethod.Post, "/admin/api/tips"),
        (HttpMethod.Get, "/admin/api/entries"),
    };

    private static HttpContent BodyFor(string path)
    {
        return path.EndsWith("/login", StringComparison.Ordinal)
            ? AdminRequests.Json(new { password = TestApp.AdminTestPassword })
            : TipBody("KEYMARKER", "draft");
    }

    [Fact]
    public async Task Every_admin_route_without_the_key_is_401_admin_key_and_changes_nothing()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.ApiLoginAsync();
        var sessions = await app.CountAsync("admin_sessions");

        foreach (var (method, path) in AllRoutes)
        {
            var response = await app.AdminApi(key: null, session: session).SendAsync(Request(method, path, method == HttpMethod.Post ? BodyFor(path) : null));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("admin_key", await AdminRequests.CodeAsync(response));
        }
        Assert.Equal(sessions, await app.CountAsync("admin_sessions"));
        Assert.Equal(0, await app.CountAsync("tips"));
    }

    // Five keyless logins with the right password are all refused by the key, and none of them is counted.
    [Fact]
    public async Task Keyless_logins_are_not_counted_so_a_keyed_login_in_the_same_minute_succeeds()
    {
        await using var app = await TestApp.StartAdminAsync(_db);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var refused = await app.AdminApi(key: null).PostAsync("/admin/api/login", AdminRequests.Json(new { password = TestApp.AdminTestPassword }));
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal("admin_key", await AdminRequests.CodeAsync(refused));
        }
        Assert.Equal(0, await app.CountAsync("admin_sessions"));

        var keyed = await app.AdminApi().PostAsync("/admin/api/login", AdminRequests.Json(new { password = TestApp.AdminTestPassword }));
        Assert.Equal(HttpStatusCode.OK, keyed.StatusCode);
        Assert.False(string.IsNullOrEmpty((await keyed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()));
    }

    // The right key with a space on either side, another case, one character short or in another header does not open any route.
    [Fact]
    public async Task Only_the_exact_key_in_the_key_header_is_accepted()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.ApiLoginAsync();
        var key = TestApp.AdminTestKey;
        var wrongKeys = new[]
        {
            "not-the-key-0123456789abcdef0123",
            key[..^1],
            key.ToUpperInvariant(),
            " " + key,
            key + " ",
            key + "x",
        };

        foreach (var wrong in wrongKeys)
        {
            var response = await app.AdminApi(key: wrong, session: session).GetAsync("/admin/api/tips");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("admin_key", await AdminRequests.CodeAsync(response));
        }

        var request = Request(HttpMethod.Get, "/admin/api/tips");
        request.Headers.Add("Authorization", key);
        request.Headers.Add(Admin.SessionHeader, session);
        var wrongHeader = await app.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongHeader.StatusCode);
        Assert.Equal("admin_key", await AdminRequests.CodeAsync(wrongHeader));

        Assert.Equal(HttpStatusCode.OK, (await app.AdminApi(session: session).GetAsync("/admin/api/tips")).StatusCode);
    }

    // A key that is not set must refuse everything, also an empty key sent by the caller.
    [Theory]
    [InlineData("")]
    [InlineData("shortkey-31-characters-0123456")]
    public async Task A_key_that_is_not_set_or_is_short_refuses_every_route_even_when_the_caller_sends_the_same_key(string configured)
    {
        await using var app = await TestApp.StartAdminAsync(_db, key: configured);

        foreach (var sent in new string?[] { null, "", configured, TestApp.AdminTestKey })
        {
            foreach (var (method, path) in AllRoutes)
            {
                var response = await app.AdminApi(key: sent, session: "x").SendAsync(Request(method, path, method == HttpMethod.Post ? BodyFor(path) : null));

                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.Equal("admin_key_not_configured", await AdminRequests.CodeAsync(response));
            }
        }
        Assert.Equal(0, await app.CountAsync("admin_sessions"));
    }

    [Fact]
    public async Task With_the_right_key_and_no_junk_or_expired_session_every_signed_in_route_is_401_session_invalid()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var expired = await app.ApiLoginAsync();
        app.Clock.Now = app.Clock.Now.AddHours(9);
        var signedIn = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, "/admin/api/session"),
            (HttpMethod.Get, "/admin/api/tips"),
            (HttpMethod.Post, "/admin/api/tips"),
            (HttpMethod.Get, "/admin/api/entries"),
        };

        foreach (var (method, path) in signedIn)
        {
            foreach (var token in new string?[] { null, "", new string('a', Tokens.Length), new string('b', 4000), expired })
            {
                var response = await app.AdminApi(session: token).SendAsync(Request(method, path, method == HttpMethod.Post ? BodyFor(path) : null));

                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Equal("session_invalid", await AdminRequests.CodeAsync(response));
            }
        }
        Assert.Equal(0, await app.CountAsync("tips"));
    }

    [Fact]
    public async Task Every_answer_of_a_mapped_admin_route_is_not_cached()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.ApiLoginAsync();
        var responses = new List<HttpResponseMessage>();

        foreach (var (method, path) in AllRoutes)
        {
            responses.Add(await app.AdminApi(key: null).SendAsync(Request(method, path, method == HttpMethod.Post ? BodyFor(path) : null)));
            responses.Add(await app.AdminApi(session: "junk").SendAsync(Request(method, path, method == HttpMethod.Post ? BodyFor(path) : null)));
            responses.Add(await app.AdminApi(session: session).SendAsync(Request(method, path, method == HttpMethod.Post ? BodyFor(path) : null)));
        }
        responses.Add(await app.AdminApi().PostAsync("/admin/api/login", AdminRequests.Json(new { password = "wrong" })));

        Assert.All(responses, response => Assert.Equal("no-store", string.Join(",", response.Headers.GetValues("Cache-Control"))));
        // The login answer carries the session token.
        var login = await app.AdminApi().PostAsync("/admin/api/login", AdminRequests.Json(new { password = TestApp.AdminTestPassword }));
        Assert.Equal("no-store", string.Join(",", login.Headers.GetValues("Cache-Control")));
    }

    [Fact]
    public async Task A_device_token_is_not_an_admin_session_and_an_admin_session_is_not_a_device_token()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var (_, deviceToken) = await app.RegisterAsync();
        var session = await app.ApiLoginAsync();

        var asSession = await app.AdminApi(session: deviceToken).GetAsync("/admin/api/tips");
        var asDevice = await app.ClientFor(session).GetAsync("/api/v1/tip/today");

        Assert.Equal(HttpStatusCode.Unauthorized, asSession.StatusCode);
        Assert.Equal("session_invalid", await AdminRequests.CodeAsync(asSession));
        Assert.Equal(HttpStatusCode.Unauthorized, asDevice.StatusCode);
    }

    // ---- the database does not answer ----

    // A host that takes no connection: the connect timeout (5 seconds) produces the answer. A refusal at once would pass
    // an upper-bound-only check even without the timeout, so the lower bound is what proves it.
    [Fact]
    public async Task A_database_host_that_does_not_answer_gives_503_after_the_connect_timeout()
    {
        var settings = await _db.NewDatabaseAsync(migrate: false);
        settings["PGHOST"] = "10.255.255.1";
        settings["ADMIN_API_KEY"] = TestApp.AdminTestKey;
        settings["ADMIN_PASSWORD_HASH"] = TestApp.AdminTestHash;
        await using var app = new TestApp(settings);
        var api = app.AdminApi(session: new string('a', Tokens.Length));

        var watch = Stopwatch.StartNew();
        var response = await api.GetAsync("/admin/api/session");
        watch.Stop();

        Assert.True(
            watch.Elapsed > TimeSpan.FromSeconds(4),
            $"The answer came after {watch.Elapsed.TotalSeconds:0.0} s. This address refused at once on this machine, so the test cannot show the connect timeout here.");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The answer took {watch.Elapsed.TotalSeconds:0.0} s.");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("database_unavailable", await AdminRequests.CodeAsync(response));
    }
}
