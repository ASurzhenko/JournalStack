using System.Net;
using System.Text.RegularExpressions;

namespace JournalStack.Server.Tests;

[Collection("database")]
public class AdminPageTests
{
    private readonly PostgresFixture _db;

    public AdminPageTests(PostgresFixture db)
    {
        _db = db;
    }

    [Fact]
    public async Task The_admin_page_is_served_with_a_content_policy_and_no_caching_and_the_folder_redirects()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var client = app.AdminClient();

        var page = await client.GetAsync("/admin/");
        var redirect = await client.GetAsync("/admin");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        var policy = string.Join(";", page.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'self'", policy);
        Assert.Contains("frame-ancestors 'none'", policy);
        Assert.Equal("no-cache", string.Join(",", page.Headers.GetValues("Cache-Control")));
        Assert.Equal(HttpStatusCode.MovedPermanently, redirect.StatusCode);
        Assert.Equal("/admin/", redirect.Headers.Location!.AbsolutePath);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/admin.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin/missing.js")).StatusCode);
    }

    // A text check on the served files. It cannot prove the page is safe, only that none of the usual ways
    // to write markup from text were added. The browser check with a script-like entry is the proof.
    [Fact]
    public async Task The_served_files_use_none_of_the_ways_to_write_markup_from_text()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var client = app.AdminClient();
        var script = await client.GetStringAsync("/admin/admin.js");
        var html = await client.GetStringAsync("/admin/");

        foreach (var banned in new[] { "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "eval(", "new Function" })
        {
            Assert.DoesNotContain(banned, script);
            Assert.DoesNotContain(banned, html);
        }
        Assert.DoesNotMatch(@"<script(?![^>]*\ssrc=)", html);
        Assert.DoesNotMatch(@"\son\w+\s*=", html);
        Assert.DoesNotMatch(@"\sstyle\s*=", html);
        Assert.Matches(new Regex(@"<script src=""admin\.js"">"), html);
    }

    [Fact]
    public async Task Every_admin_api_answer_is_marked_as_not_to_be_cached()
    {
        await using var app = await TestApp.StartAdminAsync(_db);
        var session = await app.LoginAsync();

        var unauthorized = await app.AdminClient().GetAsync("/admin/api/tips");
        var noHeader = await app.AdminClient(session, header: false).PostAsync("/admin/api/tips", AdminRequests.Json(new { }));
        var list = await app.AdminClient(session).GetAsync("/admin/api/tips");

        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, noHeader.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        foreach (var response in new[] { unauthorized, noHeader, list })
        {
            Assert.Equal("no-store", string.Join(",", response.Headers.GetValues("Cache-Control")));
        }
    }
}
