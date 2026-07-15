using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Valenius.Backend.Data;
using Valenius.Backend.Services;

namespace Valenius.Backend.Pages;

/// <summary>Public, unauthenticated self-serve download page. Lists the published <b>stable</b>
/// installer of each desktop OS (Windows / macOS / Linux) with a direct link. Exposes nothing
/// internal — only what is already reachable via the anonymous <c>/api/download/{fileName}</c>.</summary>
[AllowAnonymous]
public class DownloadsModel(ApplicationDbContext db) : PageModel
{
    public IReadOnlyList<DownloadItem> Items { get; private set; } = [];

    public async Task OnGetAsync() =>
        Items = await ReleaseQuery.PublishedStableDesktopAsync(db);
}
