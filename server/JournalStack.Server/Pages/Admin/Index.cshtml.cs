using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace JournalStack.Server.Pages;

public class IndexModel : PageModel
{
    private const string NoSuchTip = "That tip no longer exists.";

    private readonly NpgsqlDataSource _db;

    public IndexModel(NpgsqlDataSource db)
    {
        _db = db;
    }

    public List<AdminTip> Tips { get; private set; } = new();

    // The app shows the first published row, because the list is newest first.
    public Guid? ShownInApp => Tips.FirstOrDefault(tip => tip.Status == "published")?.Id;

    // What the editor starts with. An empty id means a new tip.
    public string EditId { get; private set; } = "";
    public string EditText { get; private set; } = "";
    public string EditStatus { get; private set; } = "draft";
    public string? Message { get; private set; }

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
            Message = NoSuchTip;
            return;
        }

        EditId = tip.Id.ToString();
        EditText = tip.Text;
        EditStatus = tip.Status;
    }

    // No bound members: the form is read here, behind a content-type guard, and the id is parsed by hand.
    // A bound Guid? would turn "abc" into null and silently create a new tip.
    public async Task<IActionResult> OnPostAsync()
    {
        var form = Request.HasFormContentType ? await Request.ReadFormAsync() : FormCollection.Empty;
        var rawId = form["id"].FirstOrDefault() ?? "";
        var rawText = form["text"].FirstOrDefault();
        var rawStatus = form["status"].FirstOrDefault();

        Guid? id = null;
        var badId = false;
        if (rawId.Length > 0)
        {
            if (Guid.TryParse(rawId, out var parsed))
            {
                id = parsed;
            }
            else
            {
                badId = true;
            }
        }

        // What was typed goes back into the editor if the save is refused.
        EditId = id?.ToString() ?? "";
        EditText = rawText ?? "";
        EditStatus = rawStatus == "published" ? "published" : "draft";

        var (text, status, error) = AdminContent.ValidateTip(rawText, rawStatus);
        if (error is not null)
        {
            return await Refuse(422, error);
        }
        if (badId)
        {
            return await Refuse(404, NoSuchTip);
        }

        if (id is null)
        {
            await AdminContent.CreateTip(_db, text, status, HttpContext.RequestAborted);
        }
        else if (!await AdminContent.UpdateTip(_db, id.Value, text, status, HttpContext.RequestAborted))
        {
            return await Refuse(404, NoSuchTip);
        }

        return Redirect("/admin");
    }

    private async Task<IActionResult> Refuse(int statusCode, string message)
    {
        await LoadTipsAsync();
        Message = message;
        Response.StatusCode = statusCode;
        return Page();
    }

    private async Task LoadTipsAsync()
    {
        Tips = await AdminContent.ListTips(_db, HttpContext.RequestAborted);
    }
}
