using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Valenius.Backend.Data;
using Valenius.Backend.Models;
using Valenius.Backend.Services;

namespace Valenius.Backend.Pages.Admin;

public class IndexModel(ApplicationDbContext db, ClientPresenceTracker presence, WgTunnelTracker tunnels, ILogger<IndexModel> logger, IServiceScopeFactory scopeFactory) : PageModel
{
    public record ClientRow(Client Client, string? LastUsername, bool IsOnline, bool IsConnected);
    public record CustomerGroup(string Name, int? CustomerId, List<ClientRow> Clients);
    public record CustomerOption(string Value, string Name, int Count);

    // Allowed page sizes. "all" disables paging.
    public static readonly string[] PageSizeOptions = ["10", "25", "50", "100", "all"];
    private const int DefaultPageSize = 25;

    public List<CustomerGroup> Groups { get; private set; } = [];
    public List<CustomerOption> CustomerOptions { get; private set; } = [];
    public string Filter { get; private set; } = "all";
    public string SelectedCustomer { get; private set; } = "all";
    public string Search { get; private set; } = "";
    public string PageSizeRaw { get; private set; } = DefaultPageSize.ToString();
    public bool ShowAll { get; private set; }
    public int CurrentPage { get; private set; } = 1;
    public int TotalPages { get; private set; } = 1;
    public int FilteredCount { get; private set; }
    public (int Total, int Pending, int Active, int Inactive, int Online, int Connected) Counts { get; private set; }

    // NOTE: the pagination route value is named "pageNum", never "page" — "page" is a reserved
    // route token in Razor Pages (it identifies the page itself). Binding a route/query param
    // named "page" collides with that and corrupts link/form URL generation (e.g. the Toggle
    // POST loses its ?handler=Toggle and silently renders the empty page instead of activating).
    public async Task OnGetAsync(string? filter, string? customer, int? pageNum, string? pageSize, string? search)
    {
        Filter = filter ?? "all";
        SelectedCustomer = string.IsNullOrEmpty(customer) ? "all" : customer;
        Search = (search ?? "").Trim();
        PageSizeRaw = PageSizeOptions.Contains(pageSize) ? pageSize! : DefaultPageSize.ToString();
        ShowAll = PageSizeRaw == "all";

        var loaded = await LoadScopedAsync();
        if (loaded is null)
            return;

        var (clients, scoped, lastUsernames) = loaded.Value;

        // Customer dropdown options (admins only) — built before narrowing so every
        // customer with clients is selectable, each showing its client count.
        if (User.IsInRole("Admin"))
        {
            CustomerOptions = clients
                .GroupBy(c => c.Customer?.Name)
                .Select(g => new CustomerOption(
                    g.Key is null ? "unassigned" : g.First().CustomerId!.Value.ToString(),
                    g.Key ?? "Unassigned",
                    g.Count()))
                .OrderBy(o => o.Value == "unassigned" ? 1 : 0)
                .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Status counts reflect the selected customer scope (and search) so the pills match the list.
        Counts = (
            Total:     scoped.Count,
            Pending:   scoped.Count(c => !c.IsActive),
            Active:    scoped.Count(c => c.IsActive),
            Inactive:  0,
            Online:    scoped.Count(c => presence.IsOnline(c.ClientKey)),
            Connected: scoped.Count(c => tunnels.IsDisplayConnected(c.ClientKey))
        );

        var filtered = (Filter switch
        {
            "pending"   => scoped.Where(c => !c.IsActive),
            "active"    => scoped.Where(c => c.IsActive),
            "online"    => scoped.Where(c => presence.IsOnline(c.ClientKey)),
            "connected" => scoped.Where(c => tunnels.IsDisplayConnected(c.ClientKey)),
            _           => scoped.AsEnumerable()
        }).ToList();

        FilteredCount = filtered.Count;

        // Paging — applied to the flat ordered list, then the page is re-grouped by customer.
        var pageClients = filtered;
        if (!ShowAll)
        {
            var size = int.Parse(PageSizeRaw);
            TotalPages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)size));
            CurrentPage = Math.Clamp(pageNum ?? 1, 1, TotalPages);
            pageClients = filtered.Skip((CurrentPage - 1) * size).Take(size).ToList();
        }

        Groups = pageClients
            .GroupBy(c => c.Customer?.Name)
            .OrderBy(g => g.Key == null ? 1 : 0)
            .ThenBy(g => g.Key)
            .Select(g => new CustomerGroup(
                g.Key ?? "Unassigned",
                g.First().CustomerId,
                g.Select(c => new ClientRow(c, lastUsernames.GetValueOrDefault(c.Id), presence.IsOnline(c.ClientKey), tunnels.IsDisplayConnected(c.ClientKey))).ToList()))
            .ToList();
    }

    // Lightweight status feed for the live-refresh poller: recomputes online / VPN-connected /
    // active state for the current customer + search scope without re-rendering the page.
    public async Task<IActionResult> OnGetLiveStatusAsync(string? customer, string? search)
    {
        SelectedCustomer = string.IsNullOrEmpty(customer) ? "all" : customer;
        Search = (search ?? "").Trim();

        var loaded = await LoadScopedAsync();
        if (loaded is null)
            return new JsonResult(new { counts = new { }, clients = Array.Empty<object>() });

        var scoped = loaded.Value.Scoped;

        var counts = new
        {
            total     = scoped.Count,
            pending   = scoped.Count(c => !c.IsActive),
            active    = scoped.Count(c => c.IsActive),
            online    = scoped.Count(c => presence.IsOnline(c.ClientKey)),
            connected = scoped.Count(c => tunnels.IsDisplayConnected(c.ClientKey)),
        };

        var clients = scoped.Select(c => new
        {
            id        = c.Id,
            online    = presence.IsOnline(c.ClientKey),
            connected = tunnels.IsDisplayConnected(c.ClientKey),
            active    = c.IsActive,
        });

        return new JsonResult(new { counts, clients });
    }

    // Loads clients visible to the current user (role-scoped), narrows to the selected customer,
    // resolves the last Connect username per client, and applies the search filter. Returns null
    // when a non-admin user has no customer assignment. Reads SelectedCustomer / Search fields.
    private async Task<(List<Client> All, List<Client> Scoped, Dictionary<int, string> LastUsernames)?> LoadScopedAsync()
    {
        var customerId = GetCustomerId();
        if (!User.IsInRole("Admin") && !customerId.HasValue)
            return null;

        IQueryable<Client> query = db.Clients
            .Include(c => c.Customer)
            .Where(c => !c.IsDeleted);

        if (!User.IsInRole("Admin"))
            query = query.Where(c => c.CustomerId == customerId);

        var clients = await query.ToListAsync();
        clients = clients
            .OrderBy(c => c.Customer?.Name == null ? 1 : 0)
            .ThenBy(c => c.Customer?.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.EffectiveName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Narrow to the selected customer (independent of the status filter).
        var scoped = SelectedCustomer switch
        {
            "all"        => clients,
            "unassigned" => clients.Where(c => c.CustomerId is null).ToList(),
            _            => int.TryParse(SelectedCustomer, out var cid)
                                ? clients.Where(c => c.CustomerId == cid).ToList()
                                : clients
        };

        // Most recent Connect log username per client, determined in memory to avoid GroupBy translation issues
        var clientIds = scoped.ConvertAll(c => c.Id);
        var lastUsernames = new Dictionary<int, string>();
        if (clientIds.Count > 0)
        {
            var logs = await db.ConnectionLogs
                .Where(l => clientIds.Contains(l.ClientId) && l.EventType == "Connect")
                .OrderByDescending(l => l.OccurredAt)
                .Select(l => new { l.ClientId, l.Username })
                .ToListAsync();
            foreach (var log in logs)
                lastUsernames.TryAdd(log.ClientId, log.Username);
        }

        // Server-side search across name, last user and version (mirrors the old
        // client-side box, but now spanning every page, not just the visible rows).
        if (Search.Length > 0)
        {
            var q = Search.ToLowerInvariant();
            scoped = scoped.Where(c =>
                $"{c.EffectiveName} {lastUsernames.GetValueOrDefault(c.Id)} {c.ClientVersion}"
                    .ToLowerInvariant().Contains(q)).ToList();
        }

        return (clients, scoped, lastUsernames);
    }

    public async Task<IActionResult> OnPostToggleAsync(int id, string? filter, string? customer, int? pageNum, string? pageSize, string? search)
    {
        var client = await db.Clients.Include(c => c.Customer).FirstOrDefaultAsync(c => c.Id == id);
        if (client is null) return NotFound();
        if (!CanAccessClient(client)) return Forbid();

        var activating = !client.IsActive;
        client.IsActive = activating;

        // Persist the activation state FIRST, before any sidecar work, so an admin's
        // Activate/Deactivate always takes effect immediately and the click returns fast.
        // Previously provisioning ran before SaveChanges: if the sidecar was unreachable the
        // HTTP call blocked until the reverse proxy aborted the request (~60 s) — SaveChanges
        // never ran, so the client stayed "Activate" and the page came back empty.
        await db.SaveChangesAsync();

        // Provision/deprovision on the sidecar in the background (own DI scope + fresh
        // DbContext, since it outlives the request). A slow or unreachable sidecar can no
        // longer hang or fail the toggle; the staged config is delivered on the next
        // heartbeat, and provisioning can be retried from the client's Details page.
        if (client.Customer?.ServerMode == "Valenius")
        {
            var clientId = client.Id;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var scopedDb   = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var provisioner = scope.ServiceProvider.GetRequiredService<IPeerProvisioningService>();
                    var c = await scopedDb.Clients.Include(x => x.Customer)
                        .FirstOrDefaultAsync(x => x.Id == clientId);
                    if (c?.Customer is null) return;

                    if (activating)
                        await provisioner.ProvisionAsync(c, c.Customer);
                    else
                        await provisioner.DeprovisionAsync(c, c.Customer);
                    await scopedDb.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Background provisioning failed for client {ClientId} (activating={Activating}).",
                        clientId, activating);
                }
            });
        }

        // Preserve the current filter / customer / search / page view.
        return RedirectToPage(new { filter, customer, pageNum, pageSize, search });
    }

    private int? GetCustomerId()
    {
        var val = User.FindFirstValue("CustomerId");
        return int.TryParse(val, out var id) ? id : null;
    }

    private bool CanAccessClient(Client client)
    {
        if (User.IsInRole("Admin")) return true;
        var customerId = GetCustomerId();
        return customerId.HasValue && client.CustomerId == customerId;
    }
}
