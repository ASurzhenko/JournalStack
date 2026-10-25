using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace JournalStack.Server.Pages;

public class EntriesModel : PageModel
{
    private readonly NpgsqlDataSource _db;

    public EntriesModel(NpgsqlDataSource db)
    {
        _db = db;
    }

    public List<AdminEntry> Entries { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Entries = await AdminContent.ListEntries(_db, HttpContext.RequestAborted);
    }
}
