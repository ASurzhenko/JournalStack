using System.Net;
using System.Text;
using Dapper;

namespace JournalStack.Server.Tests;

[Collection("database")]
public class AdminAuthTests
{
    private const string PasswordMarker = "WRONGPASSMARKER-1234";

    private readonly PostgresFixture _db;

    public AdminAuthTests(PostgresFixture db)
    {
        _db = db;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, HttpContent? content = null)
    {
        return new HttpRequestMessage(method, path) { Content = content };
    }

    private static string Route(string route)
    {
        return route.Replace("{id:guid}", Guid.NewGuid().ToString());
    }

    [Fact]
    public async Task Login_with_the_right_password_sets_a_safe_cookie_and_stores_only_a_keyed_hash()
    {
        await using var app = await TestApp.StartAdminAsync(_db);

        var response = await app.AdminClient().PostAsync("/admin/api/login", AdminRequests.Json(new { password = TestApp.AdminTestPassword }));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = AdminRequests.SetCookie(response)!.ToLowerInvariant();
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("path=/admin", cookie);
        Assert.DoesNotContain("secure", cookie);
        var expires = app.Clock.Now.AddHours(8).ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains("expires=" + expires.ToLowerInvariant(), cookie);

        var token = AdminRequests.CookieValue(response)!;
        Assert.Equal(Tokens.Length, token.Length);
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        var stored = (await conn.QueryAsync<byte[]>("SELECT token_hash FROM admin_sessions")).Single();
        Assert.Equal(Admin.SessionHash(token, TestApp.AdminTestHash), stored);
        Assert.DoesNotContain(token, Convert.ToBase64String(stored));
        Assert.DoesNotContain(token, Convert.ToHexString(stored), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_cookie_is_secure_only_when_the_request_came_over_https()
    {
        await using var app = await TestApp.StartAdminAsync(_db);

        var plain = await app.AdminClient().PostAsync("/admin/api/login", AdminRequests.Json(new { password = TestApp.AdminTestPassword }));
        var request = Request(HttpMethod.Post, "/admin/api/login", AdminRequests.Json(new { password = TestApp.AdminTestPassword }));
        request.Headers.Add("X-Forwarded-Proto", "https");
        var proxied = await app.AdminClient().SendAsync(request);

        Assert.DoesNotContain("secure", AdminRequests.SetCookie(plain)!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", AdminRequests.SetCookie(proxied)!, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<string, int, string> BadLogins()
    {
        return new TheoryData<string, int, string>
        {
            { "{\"password\":\"" + PasswordMarker + "\"}", 401, "invalid_credentials" },
            { "{}", 401, "invalid_credentials" },
            { "{\"password\":5}", 401, "invalid_credentials" },
            { "", 400, "invalid_json" },
            { "{{{", 400, "invalid_json" },
            { "[]", 400, "invalid_json" },
            { "{\"password\":\"" + new string('x', 1100) + "\"}", 413, "payload_too_large" },
        };
    }

    // Each row starts its own app, so every row has the full five attempts and the clock does not need to move.
    [Theory]
    [MemberData(nameof(BadLogins))]
    public async Task A_bad_login_is_refused_with_its_own_code_and_leaves_no_cookie_no_session_and_no_trace(string body, int status, string code)
    {
        await using var app = await TestApp.StartAdminAsync(_db);

        var response = await app.AdminClient().PostAsync("/admin/api/login", new StringContent(body, Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(code, await AdminRequests.CodeAsync(response));
        Assert.Null(AdminRequests.SetCookie(response));
        Assert.Equal(0, await app.CountAsync("admin_sessions"));
        Assert.DoesNotContain(PasswordMarker, text);
        Assert.DoesNotContain(PasswordMarker, app.Logs.All);
    }

    [Fact]
    public async Task The_sixth_login_in_a_minute_is_locked_even_with_the_right_password_until_the_window_passes()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var client = app.AdminClient();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var wrong = await client.PostAsync("/admin/api/login", AdminRequests.Json(new { password = PasswordMarker }));
            Assert.Equal("invalid_credentials", await AdminRequests.CodeAsync(wrong));
        }
        var locked = await client.PostAsync("/admin/api/login", AdminRequests.Json(new { password = TestApp.AdminTestPassword }));

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal("login_locked", await AdminRequests.CodeAsync(locked));
        Assert.Null(AdminRequests.SetCookie(locked));

        app.Clock.Now = app.Clock.Now.AddSeconds(61);
        var open = await client.PostAsync("/admin/api/login", AdminRequests.Json(new { password = TestApp.AdminTestPassword }));
        Assert.Equal(HttpStatusCode.NoContent, open.StatusCode);
    }

    [Fact]
    public async Task With_no_hash_configured_login_says_so_and_counts_nothing()
    {
        await using var app = await TestApp.StartAdminAsync(_db, hash: "");
        var client = app.AdminClient();

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var response = await client.PostAsync("/admin/api/login", AdminRequests.Json(new { password = TestApp.AdminTestPassword }));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("admin_not_configured", await AdminRequests.CodeAsync(response));
        }
        Assert.Equal(0, await app.CountAsync("admin_sessions"));
    }

    [Fact]
    public async Task A_garbage_hash_never_logs_anyone_in()
    {
        await using var app = await TestApp.StartAdminAsync(_db, hash: "garbage");

        var response = await app.AdminClient().PostAsync("/admin/api/login", AdminRequests.Json(new { password = TestApp.AdminTestPassword }));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("server_error", await AdminRequests.CodeAsync(response));
        Assert.Null(AdminRequests.SetCookie(response));
        Assert.Equal(0, await app.CountAsync("admin_sessions"));
    }

    [Fact]
    public async Task Every_write_route_needs_the_header_and_a_json_body_and_the_set_of_write_routes_is_pinned()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();
        var sessions = await app.CountAsync("admin_sessions");

        var writes = app.AdminRoutes().Where(route => !route.StartsWith("GET ", StringComparison.Ordinal)).Order().ToList();

        Assert.Equal(
            new[] { "POST /admin/api/login", "POST /admin/api/logout", "POST /admin/api/tips", "PUT /admin/api/tips/{id:guid}" },
            writes);
        foreach (var route in writes)
        {
            var parts = route.Split(' ');
            var method = new HttpMethod(parts[0]);

            var noHeader = await app.AdminClient(session, header: false).SendAsync(Request(method, Route(parts[1]), AdminRequests.Json(new { })));
            Assert.Equal(HttpStatusCode.Forbidden, noHeader.StatusCode);
            Assert.Equal("csrf_header", await AdminRequests.CodeAsync(noHeader));

            var wrongRequest = Request(method, Route(parts[1]), AdminRequests.Json(new { }));
            wrongRequest.Headers.Add("X-Requested-With", "other");
            var wrongHeader = await app.AdminClient(session, header: false).SendAsync(wrongRequest);
            Assert.Equal("csrf_header", await AdminRequests.CodeAsync(wrongHeader));

            var plain = await app.AdminClient(session).SendAsync(Request(method, Route(parts[1]), new StringContent("{}", Encoding.UTF8, "text/plain")));
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, plain.StatusCode);
            Assert.Equal("unsupported_media_type", await AdminRequests.CodeAsync(plain));
        }
        Assert.Equal(0, await app.CountAsync("tips"));
        Assert.Equal(sessions, await app.CountAsync("admin_sessions"));
    }

    [Fact]
    public async Task Every_route_except_login_and_logout_needs_a_session()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var protectedRoutes = app.AdminRoutes()
            .Where(route => !route.EndsWith("/login", StringComparison.Ordinal) && !route.EndsWith("/logout", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(protectedRoutes);
        foreach (var route in protectedRoutes)
        {
            var parts = route.Split(' ');
            foreach (var junk in new string?[] { null, new string('a', Tokens.Length), new string('b', 4000) })
            {
                var content = parts[0] == "GET" ? null : AdminRequests.Json(new { });
                var response = await app.AdminClient(junk).SendAsync(Request(new HttpMethod(parts[0]), Route(parts[1]), content));

                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Equal("unauthorized", await AdminRequests.CodeAsync(response));
            }
        }
    }

    [Fact]
    public async Task A_session_lasts_exactly_eight_hours()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();
        var start = app.Clock.Now;

        app.Clock.Now = start.AddHours(8).AddSeconds(-1);
        Assert.Equal(HttpStatusCode.OK, (await app.AdminClient(session).GetAsync("/admin/api/tips")).StatusCode);

        app.Clock.Now = start.AddHours(8);
        var expired = await app.AdminClient(session).GetAsync("/admin/api/tips");
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal("unauthorized", await AdminRequests.CodeAsync(expired));
    }

    [Fact]
    public async Task Logout_ends_the_session_for_good_and_logging_out_twice_is_fine()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();

        var logout = await app.AdminClient(session).PostAsync("/admin/api/logout", AdminRequests.Json(new { }));

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains($"{Admin.CookieName}=;", AdminRequests.SetCookie(logout)!);
        Assert.Equal(0, await app.CountAsync("admin_sessions"));
        var replay = await app.AdminClient(session).GetAsync("/admin/api/tips");
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        var again = await app.AdminClient().PostAsync("/admin/api/logout", AdminRequests.Json(new { }));
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    }

    [Fact]
    public async Task When_the_delete_fails_logout_does_not_pretend_and_the_session_stays_valid()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        await conn.ExecuteAsync("ALTER TABLE admin_sessions RENAME TO admin_sessions_gone");

        var logout = await app.AdminClient(session).PostAsync("/admin/api/logout", AdminRequests.Json(new { }));

        Assert.True((int)logout.StatusCode is 500 or 503);
        Assert.Null(AdminRequests.SetCookie(logout));
        await conn.ExecuteAsync("ALTER TABLE admin_sessions_gone RENAME TO admin_sessions");
        Assert.Equal(HttpStatusCode.OK, (await app.AdminClient(session).GetAsync("/admin/api/tips")).StatusCode);
    }

    [Fact]
    public async Task A_session_survives_a_second_server_on_the_same_database_and_hash()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();
        await using var second = new TestApp(app.Settings);

        var response = await second.AdminClient(session).GetAsync("/admin/api/tips");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_new_password_hash_ends_every_old_session()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();
        const string newPassword = "a completely different phrase";
        var settings = new Dictionary<string, string?>(app.Settings) { ["ADMIN_PASSWORD_HASH"] = Admin.HashPassword(newPassword) };
        await using var rotated = new TestApp(settings);

        var old = await rotated.AdminClient(session).GetAsync("/admin/api/tips");
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);

        var fresh = await rotated.LoginAsync(newPassword);
        Assert.Equal(HttpStatusCode.OK, (await rotated.AdminClient(fresh).GetAsync("/admin/api/tips")).StatusCode);
        var oldPassword = await rotated.AdminClient().PostAsync("/admin/api/login", AdminRequests.Json(new { password = TestApp.AdminTestPassword }));
        Assert.Equal("invalid_credentials", await AdminRequests.CodeAsync(oldPassword));
    }

    [Fact]
    public async Task When_the_session_insert_fails_login_is_never_2xx_and_sends_no_cookie_at_all()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        await conn.ExecuteAsync("ALTER TABLE admin_sessions RENAME TO admin_sessions_gone");

        var response = await app.AdminClient().PostAsync("/admin/api/login", AdminRequests.Json(new { password = TestApp.AdminTestPassword }));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("server_error", await AdminRequests.CodeAsync(response));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task A_preflight_from_another_site_gets_no_cross_site_permission()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var request = Request(HttpMethod.Options, "/admin/api/tips");
        request.Headers.Add("Origin", "http://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "x-requested-with,content-type");

        var response = await app.AdminClient().SendAsync(request);

        Assert.False(response.IsSuccessStatusCode);
        Assert.DoesNotContain(response.Headers, header => header.Key.StartsWith("Access-Control-Allow-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Neither_the_password_nor_the_token_nor_the_hash_nor_the_cookie_reach_the_log()
    {
        await using var app = await TestApp.StartAdminAsync(_db);

        await app.AdminClient().PostAsync("/admin/api/login", AdminRequests.Json(new { password = PasswordMarker }));
        var session = await app.LoginAsync();
        await app.AdminClient(session).PostAsync("/admin/api/tips", AdminRequests.Json(new { text = "a tip", status = "draft" }));
        await app.AdminClient(session).PostAsync("/admin/api/logout", AdminRequests.Json(new { }));

        var logs = app.Logs.All;
        Assert.DoesNotContain(PasswordMarker, logs);
        Assert.DoesNotContain(TestApp.AdminTestPassword, logs);
        Assert.DoesNotContain(session, logs);
        Assert.DoesNotContain(TestApp.AdminTestHash, logs);
    }
}
