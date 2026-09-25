using System.Security.Claims;
using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Asco.Api.Modules;

public static class ModuleKeys
{
    public const string Inventory = "inventory";
    public const string Manufacturing = "manufacturing";
    public const string Jobs = "jobs";
    public const string Fleet = "fleet";
    public static readonly string[] All = [Inventory, Manufacturing, Jobs, Fleet];
    /// <summary>Modules that need another module switched on too.</summary>
    public static readonly Dictionary<string, string[]> Requires = new() { [Manufacturing] = [Inventory], [Fleet] = [Jobs] };
}

public record IndustryInfo(Industry Industry, string Name, string Description, string[] DefaultModules, string JobLabel, string[] SuggestedAccounts);

/// <summary>Industry packs: what each industry switches on by default and how it talks about "jobs".
/// A company can still toggle modules individually — industry only sets sensible defaults.</summary>
public static class IndustryCatalog
{
    public static readonly IndustryInfo[] All =
    [
        new(Industry.General, "General / Services firm", "Core accounting, AR/AP, payroll, reports — everything in C-ERP.", [], "Job",
            []),
        new(Industry.Trading, "Trading & Distribution", "Stock in multiple warehouses, weighted-average costing, COGS on invoicing, reorder alerts.", [ModuleKeys.Inventory], "Order",
            ["Inventory / Stock in trade (asset)", "Cost of goods sold (expense)", "Goods received not invoiced (liability)", "Stock adjustments (expense)"]),
        new(Industry.Retail, "Retail", "Store stock, sell-through to COGS, stock counts and adjustments.", [ModuleKeys.Inventory], "Order",
            ["Inventory — store stock (asset)", "Cost of goods sold (expense)", "Stock shrinkage (expense)"]),
        new(Industry.Manufacturing, "Manufacturing", "Bills of materials, production orders, material issue and finished-goods costing on top of inventory.", [ModuleKeys.Inventory, ModuleKeys.Manufacturing], "Production order",
            ["Inventory — raw materials & finished goods (asset)", "Cost of goods sold (expense)", "Production overhead absorbed (expense contra)", "Stock adjustments (expense)"]),
        new(Industry.Logistics, "Logistics & Freight", "Shipment job files (air/sea/road, AWB/BL, containers), per-job profitability, fleet and trips.", [ModuleKeys.Jobs, ModuleKeys.Fleet], "Shipment",
            ["Freight revenue (income)", "Freight & carrier costs (expense)", "Fuel & vehicle running costs (expense)"]),
        new(Industry.Construction, "Construction & Projects", "Project jobs with budgets vs actual cost, site materials from inventory.", [ModuleKeys.Jobs, ModuleKeys.Inventory], "Project",
            ["Contract revenue (income)", "Project direct costs (expense)", "Materials inventory (asset)"]),
        new(Industry.Services, "Professional & PRO services", "Service jobs per client engagement with profitability; PRO service kits from C-ERP.", [ModuleKeys.Jobs], "Engagement",
            ["Service revenue (income)", "Direct service costs (expense)"]),
    ];

    public static IndustryInfo For(Industry i) => All.First(x => x.Industry == i);
}

public record ProfileRequest(Industry Industry, string[]? Modules, int? InventoryAccountId, int? CogsAccountId, int? StockClearingAccountId,
    int? StockAdjustmentAccountId, int? ConversionCostAccountId, int? FleetExpenseAccountId);

public static class ProfileExtensions
{
    public static HashSet<string> ModuleSet(this CompanyProfile? p) =>
        p is null ? [] : p.Modules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

    public static async Task<CompanyProfile?> ProfileAsync(this ModulesDbContext db) => await db.CompanyProfiles.FirstOrDefaultAsync();

    /// <summary>The account a posting needs, or a clear instruction to map it.</summary>
    public static int Need(this int? accountId, string what) =>
        accountId ?? throw new PostingException($"Map the {what} account under Settings → Industry & Modules before posting.");
}

/// <summary>Posts module documents to C-ERP's general ledger through its own JournalService, so every
/// module voucher gets C-ERP numbering, period locks, CanPost enforcement and balance validation.</summary>
public sealed class GlBridge(JournalService journals, LedgerService ledger)
{
    public async Task<JournalVoucher> PostAsync(DateOnly date, string narration, string? reference, string actor, IEnumerable<VoucherLineInput> lines)
    {
        var merged = lines.Where(l => l.Debit != 0 || l.Credit != 0).ToList();
        return await journals.CreateAndPostAsync(VoucherType.Journal, date, await WriteEndpoints.OpenPeriodFor(ledger, date), narration, reference, actor, merged, DateTime.UtcNow);
    }

    /// <summary>Compensation when the module row can't be saved after its voucher posted: a mirror
    /// voucher, so the ledger never holds a posting without its source document (and the trail shows why).</summary>
    public async Task ReverseAsync(JournalVoucher v, string actor, string why)
    {
        var lines = v.Lines.Select(l => new VoucherLineInput(l.AccountId, l.CostCenterId, $"Reversal: {l.Description}", l.Credit, l.Debit));
        await journals.CreateAndPostAsync(VoucherType.Journal, v.Date, v.FiscalPeriodId, $"Reversal of {v.VoucherNo} — {why}", v.VoucherNo, actor, lines, DateTime.UtcNow);
    }

    /// <summary>Save module rows after the GL posting; reverse the voucher if that save fails.</summary>
    public async Task SaveOrReverseAsync(ModulesDbContext db, JournalVoucher? v, string actor)
    {
        try { await db.SaveChangesAsync(); }
        catch
        {
            if (v is not null) await ReverseAsync(v, actor, "ASCO module save failed");
            throw;
        }
    }
}

public static class Numbering
{
    /// <summary>PREFIX-YYYY-0001 per company, continuing from the highest existing number.
    /// Callers run inside a transaction holding the per-company lock (see Locks).</summary>
    public static string Next(IEnumerable<string> existing, string prefix, int year)
    {
        var head = $"{prefix}-{year}-";
        var max = existing.Where(n => n.StartsWith(head)).Select(n => int.TryParse(n[head.Length..], out var x) ? x : 0).DefaultIfEmpty(0).Max();
        return $"{head}{max + 1:0000}";
    }
}

public static class Locks
{
    /// <summary>Transaction-scoped Postgres advisory lock (same technique C-ERP uses for numbering and
    /// allocation). Serialises concurrent stock/numbering writes per company+topic. No-op on Sqlite,
    /// which already has a single writer.</summary>
    public static async Task TakeAsync(ModulesDbContext db, int companyId, string topic)
    {
        if (!db.Database.IsNpgsql()) return;
        // Stable FNV-1a hash: string.GetHashCode is randomised per process, and every API instance
        // behind the load balancer must compute the same key for the lock to mean anything.
        uint h = 2166136261;
        foreach (var ch in topic) { h ^= ch; h *= 16777619; }
        var key = ((long)companyId << 32) | h;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})");
    }
}

internal static class IndustryEndpoints
{
    public static void MapIndustryEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/modules/catalog", () => new
        {
            industries = IndustryCatalog.All,
            modules = new[]
            {
                new { key = ModuleKeys.Inventory, name = "Inventory", description = "Warehouses, stock receipts/issues/transfers/adjustments, weighted-average cost, COGS" },
                new { key = ModuleKeys.Manufacturing, name = "Manufacturing", description = "Bills of materials and production orders (needs Inventory)" },
                new { key = ModuleKeys.Jobs, name = "Jobs & Projects", description = "Shipments / projects / engagements with profitability via their own cost centre" },
                new { key = ModuleKeys.Fleet, name = "Fleet & Trips", description = "Vehicles, trips, fuel and running costs posted to jobs (needs Jobs)" },
            },
        });

        api.MapGet("/modules/profile", async (ModulesDbContext db) =>
        {
            var p = await db.ProfileAsync();
            var info = IndustryCatalog.For(p?.Industry ?? Industry.General);
            return new { industry = info.Industry, industryName = info.Name, jobLabel = info.JobLabel, modules = p.ModuleSet(), profile = p };
        });

        api.MapPut("/modules/profile", async (ProfileRequest r, ModulesDbContext db, HttpContext ctx, ClaimsPrincipal u) =>
        {
            if (!CompanyAccess.From(ctx).CanAdminister) return Results.Problem("Company administrator access is required.", statusCode: 403);
            var modules = (r.Modules ?? IndustryCatalog.For(r.Industry).DefaultModules).Where(m => ModuleKeys.All.Contains(m)).ToHashSet();
            foreach (var m in modules.ToList())
                if (ModuleKeys.Requires.TryGetValue(m, out var deps)) modules.UnionWith(deps);
            var p = await db.ProfileAsync() ?? db.CompanyProfiles.Add(new CompanyProfile()).Entity;
            p.Industry = r.Industry;
            p.Modules = string.Join(',', ModuleKeys.All.Where(modules.Contains));
            p.InventoryAccountId = r.InventoryAccountId;
            p.CogsAccountId = r.CogsAccountId;
            p.StockClearingAccountId = r.StockClearingAccountId;
            p.StockAdjustmentAccountId = r.StockAdjustmentAccountId;
            p.ConversionCostAccountId = r.ConversionCostAccountId;
            p.FleetExpenseAccountId = r.FleetExpenseAccountId;
            p.UpdatedBy = WriteEndpoints.Actor(u);
            p.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { p.Industry, modules = p.ModuleSet() });
        });
    }

    /// <summary>Endpoint filter: the module must be switched on for the active company.</summary>
    public static TBuilder RequireModule<TBuilder>(this TBuilder b, string module) where TBuilder : IEndpointConventionBuilder =>
        b.AddEndpointFilter(async (ctx, next) =>
        {
            var db = ctx.HttpContext.RequestServices.GetRequiredService<ModulesDbContext>();
            return (await db.ProfileAsync()).ModuleSet().Contains(module) ? await next(ctx)
                : Results.Problem($"The {module} module is not enabled for this company — turn it on under Settings → Industry & Modules.", statusCode: StatusCodes.Status403Forbidden);
        });
}
