using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Models;
using Valenius.Backend.Services;

namespace Valenius.Backend.Pages.Admin.Monitoring;

/// <summary>
/// OSS-only, narrow-purpose token minting for the Zabbix/NMS monitoring endpoint
/// (docs/valenius-zabbix-monitoring-concept.md). Reuses the Management API's
/// <see cref="ApiToken"/> model/auth core, but — unlike the full <c>Admin/ApiTokens</c> page
/// (Pro-only, every scope) — this page can only ever mint a token scoped to exactly
/// <c>monitoring:read</c>, so it's safe to ship in OSS without exposing the wider Management
/// API surface (whose endpoints don't even exist in OSS).
/// </summary>
public class IndexModel(ApplicationDbContext db, IAuditService audit) : PageModel
{
    private const string MonitoringScope = "monitoring:read";

    public enum TokenState { Active, Expired, Revoked }

    public record TokenRow(
        int Id, string TokenId, string Name, string? Description,
        DateTime CreatedAt, DateTime? LastUsedAt, DateTime? ExpiresAt, TokenState State);

    public List<TokenRow> Tokens { get; private set; } = [];

    public string? JustMintedToken { get; private set; }
    public string? JustMintedTokenName { get; private set; }

    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public int? ExpiresInDays { get; set; }
    [BindProperty] public string? IpAllowlist { get; set; }

    public string? Error { get; private set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();

        if (TempData["MintedToken"] is string token && TempData["MintedTokenName"] is string name)
        {
            JustMintedToken = token;
            JustMintedTokenName = name;
        }
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        await LoadAsync();

        if (string.IsNullOrWhiteSpace(Name))
        {
            Error = "Name is required.";
            return Page();
        }

        if (ExpiresInDays is < 1)
        {
            Error = "Expiry must be at least 1 day, or left blank for no expiry.";
            return Page();
        }

        var minted = ApiTokenGenerator.Mint();
        var token = new ApiToken
        {
            TokenId = minted.TokenId,
            SecretHash = minted.SecretHash,
            Name = Name.Trim(),
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            Scopes = MonitoringScope,
            CustomerId = null,
            IpAllowlist = string.IsNullOrWhiteSpace(IpAllowlist) ? null : IpAllowlist.Trim(),
            CreatedAt = DateTime.UtcNow,
            CreatedBy = User.Identity?.Name ?? "Unknown",
            ExpiresAt = ExpiresInDays is { } days ? DateTime.UtcNow.AddDays(days) : null,
        };

        db.ApiTokens.Add(token);
        await db.SaveChangesAsync();

        _ = audit.LogAsync(Audit.Api, "Monitoring token created", $"Token: {token.Name} ({token.TokenId})");

        TempData["MintedToken"] = minted.FullToken;
        TempData["MintedTokenName"] = token.Name;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeAsync(int id)
    {
        var token = await db.ApiTokens.FirstOrDefaultAsync(t => t.Id == id && t.Scopes == MonitoringScope);
        if (token is null) return NotFound();

        token.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        _ = audit.LogAsync(Audit.Api, "Monitoring token revoked", $"Token: {token.Name} ({token.TokenId})");
        TempData["Success"] = $"Token \"{token.Name}\" revoked.";
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        var now = DateTime.UtcNow;

        // Scoped to exactly "monitoring:read" — a token with any other scope combination was
        // minted elsewhere (Pro's Admin/ApiTokens) and this page has no business managing it.
        var rows = await db.ApiTokens
            .Where(t => t.Scopes == MonitoringScope)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new
            {
                t.Id, t.TokenId, t.Name, t.Description,
                t.CreatedAt, t.LastUsedAt, t.ExpiresAt, t.RevokedAt,
            })
            .ToListAsync();

        Tokens = rows.Select(r => new TokenRow(
            r.Id, r.TokenId, r.Name, r.Description, r.CreatedAt, r.LastUsedAt, r.ExpiresAt,
            r.RevokedAt is not null ? TokenState.Revoked
                : r.ExpiresAt is { } exp && exp < now ? TokenState.Expired
                : TokenState.Active
        )).ToList();
    }
}
