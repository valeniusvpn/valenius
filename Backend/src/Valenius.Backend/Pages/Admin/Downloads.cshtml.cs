using Microsoft.AspNetCore.Mvc.RazorPages;
using Valenius.Backend.Data;
using Valenius.Backend.Services;

namespace Valenius.Backend.Pages.Admin;

/// <summary>Admin-facing download listing: the published stable installer of each desktop OS
/// (Windows / macOS / Linux) with a direct download link. Behind the /Admin (AnyRole) auth.
/// The same data is served anonymously by the public <c>/downloads</c> page.</summary>
public class DownloadsModel(ApplicationDbContext db) : PageModel
{
    public IReadOnlyList<DownloadItem> Items { get; private set; } = [];
    public string PublicUrl { get; private set; } = "";

    public async Task OnGetAsync()
    {
        Items = await ReleaseQuery.PublishedStableDesktopAsync(db);
        PublicUrl = $"{Request.Scheme}://{Request.Host}/downloads";
    }
}
