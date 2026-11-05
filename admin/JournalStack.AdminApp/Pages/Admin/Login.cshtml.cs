using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace JournalStack.AdminApp.Pages;

public class LoginModel : PageModel
{
    private readonly ApiClient _api;

    public LoginModel(ApiClient api)
    {
        _api = api;
    }

    public string? Message { get; private set; }

    public void OnGet()
    {
    }

    // The password check, the lockout and the session all live on the server. This page forwards the password
    // and decides by the `code` of the answer, never by the status. Any code not listed here goes to the catch-all,
    // so a wrong service key or a database outage can never read as "wrong password" or "not set up".
    public async Task<IActionResult> OnPostAsync()
    {
        // A post that is not a form, or a form with no password field, forwards null. The server counts it and answers invalid_credentials.
        var password = Request.HasFormContentType ? (await Request.ReadFormAsync())["password"].FirstOrDefault() : null;

        try
        {
            var result = await _api.LoginAsync(password, HttpContext.RequestAborted);
            var expires = new DateTimeOffset(DateTime.SpecifyKind(result.ExpiresAt, DateTimeKind.Utc));
            Response.Cookies.Append(Gate.CookieName, result.Token, Gate.CookieFor(HttpContext, expires));
            return Redirect("/admin");
        }
        catch (ApiException ex) when (ex.Code == "invalid_credentials")
        {
            return Refuse(401, "The password is not right.");
        }
        catch (ApiException ex) when (ex.Code == "login_locked")
        {
            return Refuse(429, "Too many login attempts. Try again in a minute.");
        }
        catch (ApiException ex) when (ex.Code == "admin_not_configured")
        {
            return Refuse(503, "Admin login is not set up on this server.");
        }
    }

    private IActionResult Refuse(int status, string message)
    {
        Message = message;
        Response.StatusCode = status;
        return Page();
    }
}
