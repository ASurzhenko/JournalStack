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

    private static HttpContent TipContent(string text = "a tip", string status = "draft")
    {
        return AdminRequests.Form(("text", text), ("status", status));
    }

    [Fact]
    public async Task Login_with_the_right_password_sets_a_safe_cookie_and_stores_only_a_keyed_hash()
    {
        await using var app = await TestApp.StartAdminAsync(_db);

        var response = await app.AdminClient().PostAsync("/admin/login", AdminRequests.Form(("password", TestApp.AdminTestPassword)));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin", response.Headers.Location!.OriginalString);
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
    }

    [Fact]
    public async Task The_cookie_is_secure_only_when_the_request_came_over_https()
    {
        await using var app = await TestApp.StartAdminAsync(_db);

        var plain = await app.AdminClient().PostAsync("/admin/login", AdminRequests.Form(("password", TestApp.AdminTestPassword)));
        var request = Request(HttpMethod.Post, "/admin/login", AdminRequests.Form(("password", TestApp.AdminTestPassword)));
        request.Headers.Add("X-Forwarded-Proto", "https");
        var proxied = await app.AdminClient().SendAsync(request);

        Assert.DoesNotContain("secure", AdminRequests.SetCookie(plain)!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", AdminRequests.SetCookie(proxied)!, StringComparison.OrdinalIgnoreCase);
    }

    // kind: wrong password, no password field, no body and no content type, a JSON body.
    // Each row starts its own app, so every row has the full five attempts and the clock does not need to move.
    [Theory]
    [InlineData("wrong")]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("json")]
    public async Task A_bad_login_is_a_401_with_a_message_and_leaves_no_cookie_no_session_and_no_trace(string kind)
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        HttpContent? content = kind switch
        {
            "wrong" => AdminRequests.Form(("password", PasswordMarker)),
            "missing" => AdminRequests.Form(("other", PasswordMarker)),
            "json" => AdminRequests.Json(new { password = PasswordMarker }),
            _ => null,
        };

        var response = await app.AdminClient().PostAsync("/admin/login", content);
        var body = await AdminRequests.BodyAsync(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("The password is not right.", body);
        Assert.Null(AdminRequests.SetCookie(response));
        Assert.Equal(0, await app.CountAsync("admin_sessions"));
        Assert.DoesNotContain(PasswordMarker, body);
        Assert.DoesNotContain(PasswordMarker, app.Logs.All);
    }

    [Fact]
    public async Task A_form_value_over_16_KiB_is_refused_and_leaves_no_cookie_no_session_and_no_trace()
    {
        await using var app = await TestApp.StartAdminAsync(_db);

        var response = await app.AdminClient().PostAsync("/admin/login", AdminRequests.Form(("password", PasswordMarker + new string('x', 17 * 1024))));
        var body = await AdminRequests.BodyAsync(response);

        // A 401 would mean the page read the value, so the form limit did not refuse it. The exact 500 pins that it did.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Null(AdminRequests.SetCookie(response));
        Assert.Equal(0, await app.CountAsync("admin_sessions"));
        Assert.DoesNotContain(PasswordMarker, body);
        Assert.DoesNotContain(PasswordMarker, app.Logs.All);
    }

    [Fact]
    public async Task The_sixth_login_in_a_minute_is_locked_even_with_the_right_password_until_the_window_passes()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var client = app.AdminClient();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var wrong = await client.PostAsync("/admin/login", AdminRequests.Form(("password", PasswordMarker)));
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }
        var locked = await client.PostAsync("/admin/login", AdminRequests.Form(("password", TestApp.AdminTestPassword)));

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Contains("Too many login attempts", await AdminRequests.BodyAsync(locked));
        Assert.Null(AdminRequests.SetCookie(locked));

        app.Clock.Now = app.Clock.Now.AddSeconds(61);
        var open = await client.PostAsync("/admin/login", AdminRequests.Form(("password", TestApp.AdminTestPassword)));
        Assert.Equal(HttpStatusCode.Redirect, open.StatusCode);
    }

    // The only observable proof that the lockout runs before the form is read: reading it would throw a 500.
    [Fact]
    public async Task The_lockout_answers_before_the_form_is_read_so_a_huge_sixth_attempt_is_a_429_and_not_a_500()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var client = app.AdminClient();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await client.PostAsync("/admin/login", AdminRequests.Form(("password", PasswordMarker)));
        }

        var locked = await client.PostAsync("/admin/login", AdminRequests.Form(("password", new string('x', 17 * 1024))));

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
    }

    [Fact]
    public async Task With_no_hash_configured_login_says_so_and_counts_nothing()
    {
        await using var app = await TestApp.StartAdminAsync(_db, hash: "");
        var client = app.AdminClient();

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var response = await client.PostAsync("/admin/login", AdminRequests.Form(("password", TestApp.AdminTestPassword)));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("Admin login is not set up", await AdminRequests.BodyAsync(response));
        }
        Assert.Equal(0, await app.CountAsync("admin_sessions"));
    }

    [Fact]
    public async Task A_garbage_hash_never_logs_anyone_in()
    {
        await using var app = await TestApp.StartAdminAsync(_db, hash: "garbage");

        var response = await app.AdminClient().PostAsync("/admin/login", AdminRequests.Form(("password", TestApp.AdminTestPassword)));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("server_error", await AdminRequests.CodeAsync(response));
        Assert.Null(AdminRequests.SetCookie(response));
        Assert.Equal(0, await app.CountAsync("admin_sessions"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://evil.example")]
    [InlineData("null")]
    [InlineData("http://localhost:81")]
    public async Task Every_admin_post_needs_an_origin_that_is_this_site_and_a_refused_post_changes_nothing(string? origin)
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();
        var posts = new (string Path, Func<HttpContent> Body)[]
        {
            ("/admin", () => TipContent("ORIGINMARKER")),
            ("/admin/login", () => AdminRequests.Form(("password", TestApp.AdminTestPassword))),
            ("/admin/logout", () => AdminRequests.Form()),
            ("/admin/zzz", () => AdminRequests.Form()),
        };

        foreach (var (path, body) in posts)
        {
            foreach (var cookie in new string?[] { session, null })
            {
                var response = await app.AdminClient(cookie, origin).PostAsync(path, body());

                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("csrf_origin", await AdminRequests.CodeAsync(response));
                Assert.Null(AdminRequests.SetCookie(response));
            }
        }
        Assert.Equal(0, await app.CountAsync("tips"));
        Assert.Equal(1, await app.CountAsync("admin_sessions"));
        Assert.Equal(HttpStatusCode.OK, (await app.AdminClient(session).GetAsync("/admin")).StatusCode);
    }

    [Fact]
    public async Task A_post_with_this_sites_origin_and_no_token_is_accepted()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();

        var response = await app.AdminClient(session, "http://LOCALHOST").PostAsync("/admin", TipContent("accepted"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(1, await app.CountAsync("tips"));
    }

    [Fact]
    public async Task A_get_never_writes_anything()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();
        await app.AdminClient(session).PostAsync("/admin", TipContent("existing"));
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        var id = await conn.ExecuteScalarAsync<Guid>("SELECT id FROM tips");

        foreach (var path in new[] { "/admin", $"/admin?edit={id}", $"/admin?edit={Guid.NewGuid()}", "/admin?edit=abc", "/admin/entries", "/admin/login" })
        {
            var response = await app.AdminClient(session).GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(1, await app.CountAsync("tips"));
        Assert.Equal(1, await app.CountAsync("admin_sessions"));
    }

    [Fact]
    public async Task Every_path_under_admin_except_login_and_logout_needs_a_session_and_near_misses_do_not_slip_through()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        await app.LoginAsync();
        var requests = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, "/admin"),
            (HttpMethod.Get, "/admin/entries"),
            (HttpMethod.Post, "/admin"),
            (HttpMethod.Get, "/admin/zzz"),
            (HttpMethod.Post, "/admin/zzz"),
            (HttpMethod.Get, "/admin/loginx"),
            (HttpMethod.Get, "/admin/login/x"),
            (HttpMethod.Post, "/admin/logout2"),
        };

        foreach (var (method, path) in requests)
        {
            foreach (var junk in new string?[] { null, new string('a', Tokens.Length), new string('b', 4000) })
            {
                var content = method == HttpMethod.Post ? TipContent() : null;
                var response = await app.AdminClient(junk).SendAsync(Request(method, path, content));

                Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
                Assert.Equal("/admin/login", response.Headers.Location!.OriginalString);
            }
        }
        Assert.Equal(0, await app.CountAsync("tips"));
    }

    [Fact]
    public async Task A_session_lasts_exactly_eight_hours()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();
        var start = app.Clock.Now;

        app.Clock.Now = start.AddHours(8).AddSeconds(-1);
        Assert.Equal(HttpStatusCode.OK, (await app.AdminClient(session).GetAsync("/admin")).StatusCode);

        app.Clock.Now = start.AddHours(8);
        var expired = await app.AdminClient(session).GetAsync("/admin");
        Assert.Equal(HttpStatusCode.Redirect, expired.StatusCode);
        Assert.Equal("/admin/login", expired.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Logout_ends_the_session_for_good_and_logging_out_twice_is_fine()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();

        var logout = await app.AdminClient(session).PostAsync("/admin/logout", AdminRequests.Form());

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/admin/login", logout.Headers.Location!.OriginalString);
        Assert.Contains($"{Admin.CookieName}=;", AdminRequests.SetCookie(logout)!);
        Assert.Equal(0, await app.CountAsync("admin_sessions"));
        var replay = await app.AdminClient(session).GetAsync("/admin");
        Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);
        var again = await app.AdminClient().PostAsync("/admin/logout", AdminRequests.Form());
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
    }

    [Fact]
    public async Task When_the_delete_fails_logout_does_not_pretend_and_the_session_stays_valid()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        await conn.ExecuteAsync("ALTER TABLE admin_sessions RENAME TO admin_sessions_gone");

        var logout = await app.AdminClient(session).PostAsync("/admin/logout", AdminRequests.Form());

        Assert.True((int)logout.StatusCode is 500 or 503);
        Assert.Null(AdminRequests.SetCookie(logout));
        await conn.ExecuteAsync("ALTER TABLE admin_sessions_gone RENAME TO admin_sessions");
        Assert.Equal(HttpStatusCode.OK, (await app.AdminClient(session).GetAsync("/admin")).StatusCode);
    }

    [Fact]
    public async Task A_session_survives_a_second_server_on_the_same_database_and_hash()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();
        await using var second = new TestApp(app.Settings);

        var response = await second.AdminClient(session).GetAsync("/admin");

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

        var old = await rotated.AdminClient(session).GetAsync("/admin");
        Assert.Equal(HttpStatusCode.Redirect, old.StatusCode);

        var fresh = await rotated.LoginAsync(newPassword);
        Assert.Equal(HttpStatusCode.OK, (await rotated.AdminClient(fresh).GetAsync("/admin")).StatusCode);
        var oldPassword = await rotated.AdminClient().PostAsync("/admin/login", AdminRequests.Form(("password", TestApp.AdminTestPassword)));
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
    }

    [Fact]
    public async Task When_the_session_insert_fails_login_is_never_a_redirect_and_sends_no_cookie_at_all()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        await conn.ExecuteAsync("ALTER TABLE admin_sessions RENAME TO admin_sessions_gone");

        var response = await app.AdminClient().PostAsync("/admin/login", AdminRequests.Form(("password", TestApp.AdminTestPassword)));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("server_error", await AdminRequests.CodeAsync(response));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task A_preflight_from_another_site_gets_no_cross_site_permission()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var request = Request(HttpMethod.Options, "/admin/login");
        request.Headers.Add("Origin", "http://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");

        var response = await app.AdminClient(origin: null).SendAsync(request);

        Assert.DoesNotContain(response.Headers, header => header.Key.StartsWith("Access-Control-Allow-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Neither_the_password_nor_the_token_nor_the_hash_nor_the_cookie_reach_the_log()
    {
        await using var app = await TestApp.StartAdminAsync(_db);

        await app.AdminClient().PostAsync("/admin/login", AdminRequests.Form(("password", PasswordMarker)));
        var session = await app.LoginAsync();
        await app.AdminClient(session).PostAsync("/admin", TipContent());
        await app.AdminClient(session).PostAsync("/admin/logout", AdminRequests.Form());

        var logs = app.Logs.All;
        Assert.DoesNotContain(PasswordMarker, logs);
        Assert.DoesNotContain(TestApp.AdminTestPassword, logs);
        Assert.DoesNotContain(session, logs);
        Assert.DoesNotContain(TestApp.AdminTestHash, logs);
    }

    // No request may depend on a key ring, so the only cookie the server ever sets is the admin one.
    [Fact]
    public async Task The_only_cookie_the_server_ever_sets_is_the_admin_session_cookie()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var responses = new List<HttpResponseMessage>();

        responses.Add(await app.AdminClient().GetAsync("/admin/login"));
        responses.Add(await app.AdminClient().PostAsync("/admin/login", AdminRequests.Form(("password", TestApp.AdminTestPassword))));
        var session = AdminRequests.CookieValue(responses[1])!;
        var client = app.AdminClient(session);
        responses.Add(await client.GetAsync("/admin"));
        responses.Add(await client.PostAsync("/admin", TipContent()));
        responses.Add(await client.GetAsync("/admin/entries"));
        responses.Add(await client.PostAsync("/admin/logout", AdminRequests.Form()));

        var cookies = responses.SelectMany(AdminRequests.AllSetCookies).ToList();
        Assert.NotEmpty(cookies);
        Assert.All(cookies, cookie => Assert.StartsWith(Admin.CookieName + "=", cookie));
    }
}
