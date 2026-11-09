using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using JournalStack.AdminApp;

namespace JournalStack.Server.Tests;

// The admin app, end to end: its own test host, whose API client talks to the server's test host.
// Failure tests replace the server with a fake handler.
[Collection("database")]
public class AdminAppTests
{
    private const string PasswordMarker = "WRONGPASSMARKER-1234";
    private const string NoSuchTip = "That tip no longer exists.";
    private const string OtherKey = "another-service-key-0123456789ab";

    private readonly PostgresFixture _db;

    public AdminAppTests(PostgresFixture db)
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

    // Both logs: the server's and the admin app's.
    private static string AllLogs(TestApp app)
    {
        return app.Logs.All + "\n" + app.AdminHost.Logs.All;
    }

    // The exception text of a host that refused to start, including the inner exceptions.
    private static async Task<string> StartupFailureAsync(AdminAppHost host)
    {
        var ex = await Record.ExceptionAsync(() =>
        {
            host.CreateClient();
            return Task.CompletedTask;
        });
        Assert.NotNull(ex);
        var text = new StringBuilder();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            text.AppendLine(current.Message);
        }
        return text.ToString();
    }

    // ---- startup ----

    [Fact]
    public async Task N14_the_host_starts_when_both_settings_are_valid()
    {
        await using var app = await TestApp.StartAdminAsync(_db);

        var response = await app.AdminHost.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task N14_startup_refuses_a_missing_address_a_missing_key_and_a_short_key_and_never_prints_the_key()
    {
        const string shortKey = "SHORTKEYMARKER-123";
        await using var app = await TestApp.StartAdminAsync(_db);

        await using var noUrl = new AdminAppHost(app, url: null);
        await using var noKey = new AdminAppHost(app, key: null);
        await using var emptyKey = new AdminAppHost(app, key: "");
        await using var shortHost = new AdminAppHost(app, key: shortKey);

        Assert.Contains("ADMIN_API_URL", await StartupFailureAsync(noUrl));
        Assert.Contains("ADMIN_API_KEY", await StartupFailureAsync(noKey));
        Assert.Contains("ADMIN_API_KEY", await StartupFailureAsync(emptyKey));
        var shortText = await StartupFailureAsync(shortHost);
        Assert.Contains("ADMIN_API_KEY", shortText);
        Assert.DoesNotContain(shortKey, shortText);
        Assert.DoesNotContain(shortKey, shortHost.Logs.All);
    }

    // ---- sign-in and the cookie ----

    [Fact]
    public async Task Login_with_the_right_password_sets_a_safe_cookie_that_the_server_knows_by_a_keyed_hash()
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
        Assert.DoesNotContain(PasswordMarker, AllLogs(app));
    }

    // T2: a post that is not a form, and a form with no password field, are attempts the server counts.
    [Fact]
    public async Task T2_a_non_form_post_and_a_form_without_a_password_are_401_and_counted_at_the_server()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var client = app.AdminClient();

        var jsonPost = await client.PostAsync("/admin/login", AdminRequests.Json(new { password = PasswordMarker }));
        var noField = await client.PostAsync("/admin/login", AdminRequests.Form(("other", "x")));
        var noBody = await client.PostAsync("/admin/login", null);
        await client.PostAsync("/admin/login", AdminRequests.Form(("other", "x")));
        await client.PostAsync("/admin/login", null);
        var locked = await client.PostAsync("/admin/login", AdminRequests.Form(("password", TestApp.AdminTestPassword)));

        foreach (var response in new[] { jsonPost, noField, noBody })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("The password is not right.", await AdminRequests.BodyAsync(response));
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
    }

    // T1: a behaviour pin. The proof that the lockout runs before the body is read is the server's
    // huge-sixth-body test; the admin app has to read the form to forward the password.
    [Fact]
    public async Task T1_a_form_value_over_16_KiB_is_refused_and_leaves_no_cookie_no_session_and_no_trace()
    {
        await using var app = await TestApp.StartAdminAsync(_db);

        var response = await app.AdminClient().PostAsync("/admin/login", AdminRequests.Form(("password", PasswordMarker + new string('x', 17 * 1024))));
        var body = await AdminRequests.BodyAsync(response);

        // A 401 would mean the value was read and forwarded, so the form limit did not refuse it. The exact 500 pins that it did.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Null(AdminRequests.SetCookie(response));
        Assert.Equal(0, await app.CountAsync("admin_sessions"));
        Assert.DoesNotContain(PasswordMarker, body);
        Assert.DoesNotContain(PasswordMarker, AllLogs(app));
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

    [Fact]
    public async Task With_no_hash_configured_the_sign_in_page_says_so()
    {
        await using var app = await TestApp.StartAdminAsync(_db, hash: "");

        var response = await app.AdminClient().PostAsync("/admin/login", AdminRequests.Form(("password", TestApp.AdminTestPassword)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Admin login is not set up", await AdminRequests.BodyAsync(response));
        Assert.Null(AdminRequests.SetCookie(response));
    }

    [Fact]
    public async Task A_garbage_hash_never_logs_anyone_in_and_the_admin_app_reports_the_server_error()
    {
        await using var app = await TestApp.StartAdminAsync(_db, hash: "garbage");

        var response = await app.AdminClient().PostAsync("/admin/login", AdminRequests.Form(("password", TestApp.AdminTestPassword)));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("api_error", await AdminErrors.CodeAsync(response));
        Assert.Null(AdminRequests.SetCookie(response));
        Assert.Equal(0, await app.CountAsync("admin_sessions"));
    }

    // T3
    [Fact]
    public async Task T3_when_the_session_insert_fails_sign_in_is_never_a_redirect_and_sends_no_cookie_at_all()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        await conn.ExecuteAsync("ALTER TABLE admin_sessions RENAME TO admin_sessions_gone");

        var response = await app.AdminClient().PostAsync("/admin/login", AdminRequests.Form(("password", TestApp.AdminTestPassword)));

        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("api_error", await AdminErrors.CodeAsync(response));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    // ---- the browser-facing gate ----

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
                Assert.Equal("csrf_origin", await AdminErrors.CodeAsync(response));
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
    public async Task Logout_ends_the_session_for_good_clears_the_cookie_and_logging_out_twice_is_fine()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();

        var logout = await app.AdminClient(session).PostAsync("/admin/logout", AdminRequests.Form());

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/admin/login", logout.Headers.Location!.OriginalString);
        Assert.Contains($"{Gate.CookieName}=;", AdminRequests.SetCookie(logout)!);
        Assert.Equal(0, await app.CountAsync("admin_sessions"));
        var replay = await app.AdminClient(session).GetAsync("/admin");
        Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);
        var again = await app.AdminClient().PostAsync("/admin/logout", AdminRequests.Form());
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
    }

    // N19: the server answers 204 to a logout with a dead session, so the dead cookie is cleared.
    [Fact]
    public async Task N19_logout_with_an_expired_session_redirects_to_sign_in_and_clears_the_cookie()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();
        app.Clock.Now = app.Clock.Now.AddHours(9);

        var logout = await app.AdminClient(session).PostAsync("/admin/logout", AdminRequests.Form());

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/admin/login", logout.Headers.Location!.OriginalString);
        Assert.Contains($"{Gate.CookieName}=;", AdminRequests.SetCookie(logout)!);
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
    public async Task Neither_the_password_nor_the_token_nor_the_hash_nor_the_cookie_nor_the_key_reach_either_log()
    {
        await using var app = await TestApp.StartAdminAsync(_db);

        await app.AdminClient().PostAsync("/admin/login", AdminRequests.Form(("password", PasswordMarker)));
        var session = await app.LoginAsync();
        await app.AdminClient(session).PostAsync("/admin", TipContent());
        await app.AdminClient(session).PostAsync("/admin/logout", AdminRequests.Form());

        var logs = AllLogs(app);
        Assert.DoesNotContain(PasswordMarker, logs);
        Assert.DoesNotContain(TestApp.AdminTestPassword, logs);
        Assert.DoesNotContain(session, logs);
        Assert.DoesNotContain(TestApp.AdminTestHash, logs);
        Assert.DoesNotContain(TestApp.AdminTestKey, logs);
    }

    // N13: a key that differs from the server's writes the admin_key_refused warning. It names no key.
    [Fact]
    public async Task N13_a_refused_key_is_logged_as_a_warning_without_either_key_and_the_answer_has_neither_key()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        await using var other = new AdminAppHost(app, key: OtherKey);

        var signIn = await other.AdminClient().PostAsync("/admin/login", AdminRequests.Form(("password", TestApp.AdminTestPassword)));
        var body = await AdminRequests.BodyAsync(signIn);

        Assert.Equal(HttpStatusCode.BadGateway, signIn.StatusCode);
        Assert.Contains("ADMIN_API_KEY", other.Logs.All);
        foreach (var text in new[] { other.Logs.All, app.Logs.All, body })
        {
            Assert.DoesNotContain(OtherKey, text);
            Assert.DoesNotContain(TestApp.AdminTestKey, text);
            Assert.DoesNotContain(TestApp.AdminTestPassword, text);
        }
    }

    // No request may depend on a key ring, so the only cookie the admin app ever sets is the admin one. The server sets none.
    [Fact]
    public async Task The_only_cookie_the_admin_app_ever_sets_is_the_admin_session_cookie()
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
        Assert.All(cookies, cookie => Assert.StartsWith(Gate.CookieName + "=", cookie));
    }

    // ---- headers and pages ----

    // Every admin answer except the stylesheet: login page, signed-in pages, redirects and refused posts.
    [Fact]
    public async Task Every_admin_answer_is_not_cached_and_carries_the_content_policy()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();
        var signedIn = app.AdminClient(session);

        var responses = new[]
        {
            await app.AdminClient().GetAsync("/admin/login"),
            await app.AdminClient().GetAsync("/admin"),
            await app.AdminClient().PostAsync("/admin/login", AdminRequests.Form(("password", "wrong"))),
            await app.AdminClient(session, origin: null).PostAsync("/admin", AdminRequests.Form()),
            await signedIn.GetAsync("/admin"),
            await signedIn.GetAsync("/admin/entries"),
            await signedIn.GetAsync("/admin/zzz"),
            await signedIn.PostAsync("/admin/logout", AdminRequests.Form()),
        };

        Assert.Equal(
            new[] { 200, 302, 401, 403, 200, 200, 404, 302 },
            responses.Select(response => (int)response.StatusCode).ToArray());
        foreach (var response in responses)
        {
            Assert.Equal("no-store", string.Join(",", response.Headers.GetValues("Cache-Control")));
            var policy = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));
            Assert.Contains("default-src 'self'", policy);
            Assert.Contains("frame-ancestors 'none'", policy);
            Assert.Contains("form-action 'self'", policy);
        }
    }

    // The policy blocks inline script and style, so the pages must not contain any: it keeps the policy honest.
    [Fact]
    public async Task The_rendered_pages_contain_no_script_no_event_attribute_and_no_style_attribute()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();
        var signedIn = app.AdminClient(session);
        await signedIn.PostAsync("/admin", AdminRequests.Form(("text", "a tip"), ("status", "published")));

        var pages = new[]
        {
            await AdminRequests.BodyAsync(await app.AdminClient().GetAsync("/admin/login")),
            await AdminRequests.BodyAsync(await signedIn.GetAsync("/admin")),
            await AdminRequests.BodyAsync(await signedIn.GetAsync("/admin/entries")),
        };

        foreach (var html in pages)
        {
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(@"\son\w+\s*=", html);
            Assert.DoesNotMatch(@"\sstyle\s*=", html);
            Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("a tip", pages[1]);
    }

    [Fact]
    public async Task The_stylesheet_is_served_and_the_old_script_the_old_page_and_the_api_path_are_not_there()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();
        var client = app.AdminClient(session);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/admin.css")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.AdminClient().GetAsync("/admin/admin.css")).StatusCode);
        foreach (var path in new[] { "/admin/admin.js", "/admin/index.html", "/admin/api/tips" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        }
    }

    [Fact]
    public async Task The_root_redirects_to_admin_and_health_answers_without_calling_the_server()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var down = new FakeHandler((_, _) => throw new HttpRequestException("down"));
        await using var host = new AdminAppHost(app, handler: down);

        var root = await host.AdminClient().GetAsync("/");
        var health = await host.AdminClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
        Assert.Equal("/admin", root.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    // ---- tips and entries through the pages ----

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
    public async Task An_entry_posted_by_a_device_shows_in_the_admin_list_encoded_and_newest_first()
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
        Assert.DoesNotContain("ENTRYMARKER", AllLogs(app));
    }

    // T5: the dates go through the JSON round trip into the admin app's record and come out as the same text as before.
    [Fact]
    public async Task T5_entries_with_the_earliest_and_latest_client_dates_are_listed_with_the_same_text_as_before()
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
        Assert.Contains("0001-01-01 00:00:00 UTC", html);
        Assert.Contains("9999-12-31 23:59:59 UTC", html);
    }

    // T4: fail closed across the hop.
    [Fact]
    public async Task T4_when_the_tips_table_is_gone_a_write_is_never_a_redirect_and_the_text_is_not_leaked()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;
        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        await conn.ExecuteAsync("ALTER TABLE tips RENAME TO tips_gone");

        var created = await admin.PostAsync("/admin", TipForm("TIPMARKER", "draft"));
        var saved = await admin.PostAsync("/admin", TipForm("TIPMARKER", "draft", Guid.NewGuid().ToString()));

        foreach (var response in new[] { created, saved })
        {
            Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.Equal("api_error", await AdminErrors.CodeAsync(response));
            Assert.DoesNotContain("TIPMARKER", await response.Content.ReadAsStringAsync());
        }
        Assert.DoesNotContain("TIPMARKER", AllLogs(app));
        await conn.ExecuteAsync("ALTER TABLE tips_gone RENAME TO tips");
        Assert.Equal(0, await app.CountAsync("tips"));
    }

    // N15: the field names on the wire are the ones the admin app reads, one round trip per route.
    [Fact]
    public async Task N15_a_tip_with_markup_is_stored_raw_shown_encoded_and_every_field_survives_the_round_trip()
    {
        var (app, admin) = await StartSignedInAsync();
        await using var _ = app;
        var (deviceId, token) = await app.RegisterAsync();
        await app.ClientFor(token).PostEntryAsync(EntryRequests.Golden());

        await admin.PostAsync("/admin", TipForm("<b>x</b>", "draft"));
        var tips = await AdminRequests.BodyAsync(await admin.GetAsync("/admin"));
        var entries = await AdminRequests.BodyAsync(await admin.GetAsync("/admin/entries"));

        await using var conn = await PostgresFixture.OpenAsync(app.Settings);
        var row = await conn.QuerySingleAsync("SELECT id, text FROM tips");
        Assert.Equal("<b>x</b>", (string)row.text);
        Assert.Contains("&lt;b&gt;x&lt;/b&gt;", tips);
        Assert.DoesNotContain("<b>x</b>", tips);
        Assert.Contains($"/admin?edit={(Guid)row.id}", tips);
        Assert.Contains("<td>draft</td>", tips);
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} UTC", tips);
        Assert.Contains("Hello", entries);
        Assert.Contains(deviceId.ToString()[..8], entries);
        Assert.Contains("2026-10-24 12:00:00 UTC", entries);
    }

    // ---- the call to the server fails ----

    private static AdminAppHost HostWith(TestApp app, FakeHandler handler, TimeSpan? timeout = null)
    {
        return new AdminAppHost(app, handler: handler, timeout: timeout);
    }

    private static HttpResponseMessage ProblemAnswer(HttpStatusCode status, string code, string? requestId = null)
    {
        var body = new Dictionary<string, object?> { ["title"] = "x", ["status"] = (int)status, ["code"] = code };
        if (requestId is not null)
        {
            body["requestId"] = requestId;
        }
        return new HttpResponseMessage(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }

    // N8
    [Fact]
    public async Task N8_when_the_server_cannot_be_reached_a_page_is_503_api_unreachable_and_not_a_redirect()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();
        await using var host = HostWith(app, new FakeHandler((_, _) => throw new HttpRequestException("down")));

        var response = await host.AdminClient(session).GetAsync("/admin");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("api_unreachable", await AdminErrors.CodeAsync(response));
        Assert.Null(AdminRequests.SetCookie(response));
    }

    // N9
    [Fact]
    public async Task N9_when_the_server_is_slower_than_the_timeout_a_page_is_504_api_timeout()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();
        var slow = new FakeHandler(async (_, cancel) =>
        {
            await Task.Delay(Timeout.Infinite, cancel);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        await using var host = HostWith(app, slow, timeout: TimeSpan.FromMilliseconds(200));

        var response = await host.AdminClient(session).GetAsync("/admin");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("api_timeout", await AdminErrors.CodeAsync(response));
        Assert.Null(AdminRequests.SetCookie(response));
    }

    // N10
    [Fact]
    public async Task N10_when_the_server_crashes_on_a_request_the_page_is_502_api_error_with_the_servers_request_id()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();
        await using var host = HostWith(app, new FakeHandler((_, _) => Task.FromResult(ProblemAnswer(HttpStatusCode.InternalServerError, "server_error", "SERVER-REQ-42"))));

        var response = await host.AdminClient(session).GetAsync("/admin");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("api_error", json.GetProperty("code").GetString());
        Assert.Equal("SERVER-REQ-42", json.GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task A_redirect_or_an_unmapped_answer_from_the_server_is_502_api_error_and_is_not_followed()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();
        var seen = new List<string>();
        var redirect = new FakeHandler((request, _) =>
        {
            seen.Add(request.RequestUri!.ToString());
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("http://elsewhere.example/steal");
            return Task.FromResult(response);
        });
        await using var host = HostWith(app, redirect);

        var response = await host.AdminClient(session).GetAsync("/admin");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("api_error", await AdminErrors.CodeAsync(response));
        Assert.DoesNotContain(seen, uri => uri.Contains("elsewhere.example"));
    }

    // N11
    [Fact]
    public async Task N11_a_wrong_service_key_is_502_admin_key_refused_never_a_wrong_password_and_never_a_sign_out()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();
        await using var other = new AdminAppHost(app, key: OtherKey);

        var signIn = await other.AdminClient().PostAsync("/admin/login", AdminRequests.Form(("password", TestApp.AdminTestPassword)));
        var page = await other.AdminClient(session).GetAsync("/admin");

        Assert.Equal(HttpStatusCode.BadGateway, signIn.StatusCode);
        Assert.Equal("admin_key_refused", await AdminErrors.CodeAsync(signIn));
        Assert.DoesNotContain("The password is not right.", await AdminRequests.BodyAsync(signIn));
        Assert.Null(AdminRequests.SetCookie(signIn));
        Assert.Equal(HttpStatusCode.BadGateway, page.StatusCode);
        Assert.Equal("admin_key_refused", await AdminErrors.CodeAsync(page));
    }

    [Fact]
    public async Task A_server_without_a_key_is_503_admin_key_not_configured_for_the_admin_app()
    {
        await using var app = await TestApp.StartAdminAsync(_db, key: "");

        var response = await app.AdminClient().PostAsync("/admin/login", AdminRequests.Form(("password", TestApp.AdminTestPassword)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("admin_key_not_configured", await AdminErrors.CodeAsync(response));
        Assert.DoesNotContain("Admin login is not set up", await AdminRequests.BodyAsync(response));
    }

    // N12
    [Fact]
    public async Task N12_logout_while_the_server_is_down_sets_no_cookie_and_the_session_still_works_afterwards()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();
        var real = new HttpMessageInvoker(app.Server.CreateHandler());
        var down = true;
        var toggle = new FakeHandler((request, cancel) =>
        {
            if (down)
            {
                throw new HttpRequestException("down");
            }
            return real.SendAsync(request, cancel);
        });
        await using var host = HostWith(app, toggle);

        var logout = await host.AdminClient(session).PostAsync("/admin/logout", AdminRequests.Form());
        down = false;
        var after = await host.AdminClient(session).GetAsync("/admin");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, logout.StatusCode);
        Assert.Null(AdminRequests.SetCookie(logout));
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    // N13, the clean run: a sign-in, a save and a sign-out leave none of the secrets in the admin app's log.
    [Fact]
    public async Task N13_a_full_session_leaves_no_secret_in_the_admin_app_log()
    {
        await using var app = await TestApp.StartAdminAsync(_db);

        await app.AdminClient().PostAsync("/admin/login", AdminRequests.Form(("password", PasswordMarker)));
        var session = await app.LoginAsync();
        await app.AdminClient(session).PostAsync("/admin", TipContent("LOGMARKER"));
        await app.AdminClient(session).PostAsync("/admin/logout", AdminRequests.Form());

        var logs = app.AdminHost.Logs.All;
        Assert.DoesNotContain(PasswordMarker, logs);
        Assert.DoesNotContain(TestApp.AdminTestPassword, logs);
        Assert.DoesNotContain(session, logs);
        Assert.DoesNotContain(TestApp.AdminTestKey, logs);
        Assert.DoesNotContain("LOGMARKER", logs);
    }

    // N16
    [Fact]
    public async Task N16_with_the_database_down_sign_in_is_503_database_unavailable_never_a_redirect_and_never_not_set_up()
    {
        var settings = await _db.NewDatabaseAsync(migrate: false);
        settings["PGPORT"] = PostgresFixture.UnusedPort().ToString();
        settings["ADMIN_PASSWORD_HASH"] = TestApp.AdminTestHash;
        settings["ADMIN_API_KEY"] = TestApp.AdminTestKey;
        await using var app = new TestApp(settings);

        var response = await app.AdminClient().PostAsync("/admin/login", AdminRequests.Form(("password", TestApp.AdminTestPassword)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("database_unavailable", await AdminErrors.CodeAsync(response));
        Assert.Null(AdminRequests.SetCookie(response));
        Assert.DoesNotContain("not set up", await AdminRequests.BodyAsync(response));
    }
}
