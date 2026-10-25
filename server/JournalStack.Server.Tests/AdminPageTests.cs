using System.Net;

namespace JournalStack.Server.Tests;

[Collection("database")]
public class AdminPageTests
{
    private readonly PostgresFixture _db;

    public AdminPageTests(PostgresFixture db)
    {
        _db = db;
    }

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
    public async Task The_stylesheet_is_served_and_the_old_script_the_old_page_and_the_old_api_are_gone()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();
        var client = app.AdminClient(session);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/admin.css")).StatusCode);
        foreach (var path in new[] { "/admin/admin.js", "/admin/index.html", "/admin/api/tips" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        }
    }
}
