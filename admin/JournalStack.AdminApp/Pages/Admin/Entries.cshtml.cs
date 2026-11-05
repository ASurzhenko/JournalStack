using Microsoft.AspNetCore.Mvc.RazorPages;

namespace JournalStack.AdminApp.Pages;

public class EntriesModel : PageModel
{
    private readonly ApiClient _api;

    public EntriesModel(ApiClient api)
    {
        _api = api;
    }

    public List<AdminEntry> Entries { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Entries = await _api.ListEntriesAsync(Request.Cookies[Gate.CookieName] ?? "", HttpContext.RequestAborted);
    }
}
