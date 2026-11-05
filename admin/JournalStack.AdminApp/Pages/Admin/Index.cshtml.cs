using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace JournalStack.AdminApp.Pages;

public class IndexModel : PageModel
{
    private readonly ApiClient _api;

    public IndexModel(ApiClient api)
    {
        _api = api;
    }

    public List<AdminTip> Tips { get; private set; } = new();

    // The app shows the first published row, because the list is newest first.
    public Guid? ShownInApp => Tips.FirstOrDefault(tip => tip.Status == "published")?.Id;

    // What the editor starts with. An empty id means a new tip.
    public string EditId { get; private set; } = "";
    public string EditText { get; private set; } = "";
    public string EditStatus { get; private set; } = "draft";
    public string? Message { get; private set; }

    private string Session => Request.Cookies[Gate.CookieName] ?? "";

    // GET only reads. ?edit={id} fills the editor from the list that was just loaded.
    public async Task OnGetAsync()
    {
        await LoadTipsAsync();
        var edit = Request.Query["edit"].FirstOrDefault();
        if (string.IsNullOrEmpty(edit))
        {
            return;
        }

        var tip = Guid.TryParse(edit, out var id) ? Tips.FirstOrDefault(t => t.Id == id) : null;
        if (tip is null)
        {
            Message = "That tip no longer exists.";
            return;
        }

        EditId = tip.Id.ToString();
        EditText = tip.Text;
        EditStatus = tip.Status;
    }

    // No bound members: the form is read here, behind a content-type guard, and the three values go to the server as typed.
    // The server checks them. A refused save shows the server's message and puts what was typed back into the editor.
    public async Task<IActionResult> OnPostAsync()
    {
        var form = Request.HasFormContentType ? await Request.ReadFormAsync() : FormCollection.Empty;
        var rawId = form["id"].FirstOrDefault() ?? "";
        var rawText = form["text"].FirstOrDefault();
        var rawStatus = form["status"].FirstOrDefault();

        try
        {
            await _api.SaveTipAsync(Session, rawId, rawText, rawStatus, HttpContext.RequestAborted);
            return Redirect("/admin");
        }
        catch (ApiException ex) when (ex.Code is "invalid_tip" or "tip_not_found")
        {
            EditId = Guid.TryParse(rawId, out var id) ? id.ToString() : "";
            EditText = rawText ?? "";
            EditStatus = rawStatus == "published" ? "published" : "draft";
            await LoadTipsAsync();
            Message = ex.Title;
            Response.StatusCode = ex.Status;
            return Page();
        }
    }

    private async Task LoadTipsAsync()
    {
        Tips = await _api.ListTipsAsync(Session, HttpContext.RequestAborted);
    }
}
