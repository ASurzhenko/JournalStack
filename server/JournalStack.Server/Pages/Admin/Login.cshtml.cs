using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace JournalStack.Server.Pages;

public class LoginModel : PageModel
{
    private readonly NpgsqlDataSource _db;
    private readonly Limits _limits;
    private readonly TimeProvider _clock;

    public LoginModel(NpgsqlDataSource db, Limits limits, TimeProvider clock)
    {
        _db = db;
        _limits = limits;
        _clock = clock;
    }

    public string? Message { get; private set; }

    public void OnGet()
    {
    }

    // The order matters: the lockout counts the attempt before any body is read, so a huge body cannot dodge it.
    public async Task<IActionResult> OnPostAsync()
    {
        var hash = Admin.ConfiguredHash(HttpContext);
        if (hash.Length == 0)
        {
            return Refuse(503, "Admin login is not set up on this server.");
        }
        if (!_limits.AdminLogins.TryTake("all"))
        {
            return Refuse(429, "Too many login attempts. Try again in a minute.");
        }

        // A post that is not a form is treated as an empty form, so it ends as a wrong password.
        var password = Request.HasFormContentType ? (await Request.ReadFormAsync())["password"].FirstOrDefault() : null;
        if (password is null || !Admin.PasswordMatches(hash, password))
        {
            return Refuse(401, "The password is not right.");
        }

        await Admin.StartSessionAsync(HttpContext, _db, _clock, hash);
        return Redirect("/admin");
    }

    private IActionResult Refuse(int status, string message)
    {
        Message = message;
        Response.StatusCode = status;
        return Page();
    }
}
