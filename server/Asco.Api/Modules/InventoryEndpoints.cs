using System.Security.Claims;
using AegisErp.Domain;
using AegisErp.Infrastructure;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Asco.Api.Modules;

public record WarehouseRequest(string Code, string Name);
public record MoveLineRequest(int ItemId, decimal Quantity, decimal? UnitCost = null);
public record ReceiptRequest(DateOnly Date, int WarehouseId, int? CreditAccountId, string? Reference, string? Narration, List<MoveLineRequest> Lines);
public record IssueRequest(DateOnly Date, int WarehouseId, int? DebitAccountId, string? Reference, string? Narration, List<MoveLineRequest> Lines);
public record TransferRequest(DateOnly Date, int FromWarehouseId, int ToWarehouseId, string? Narration, List<MoveLineRequest> Lines);
public record AdjustmentRequest(DateOnly Date, int WarehouseId, string Reason, List<MoveLineRequest> Lines);
public record InvoiceIssueRequest(int WarehouseId, DateOnly? Date);
public record ReorderRequest(decimal ReorderLevel);

internal static class InventoryEndpoints
{
    private static object MoveDto(StockMove m, IReadOnlyDictionary<int, StockItemInfo> items, IReadOnlyDictionary<int, Warehouse> whs) => new
    {
        m.Id, m.MoveNo, m.Type, m.Date, m.Reference, m.Narration, m.VoucherNo, m.CreatedBy,
        Value = StockEngine.InValue(m) > 0 ? StockEngine.InValue(m) : StockEngine.OutValue(m),
        Lines = m.Lines.Select(l => new
        {
            l.ItemId, ItemCode = items.GetValueOrDefault(l.ItemId)?.Code, ItemName = items.GetValueOrDefault(l.ItemId)?.Name,
            Warehouse = whs.GetValueOrDefault(l.WarehouseId)?.Code, l.Quantity, l.UnitCost, l.Value,
        }),
    };

    public static void MapInventoryEndpoints(this RouteGroupBuilder api)
    {
        var inv = api.MapGroup("/inventory").RequireModule(ModuleKeys.Inventory);
        static IResult? NeedPost(HttpContext ctx) => CompanyAccess.From(ctx).CanPost ? null : Results.Problem("Your role in this company is read-only.", statusCode: 403);

        inv.MapGet("/warehouses", async (ModulesDbContext db) =>
            await db.Warehouses.OrderBy(w => w.Code).Select(w => new { w.Id, w.Code, w.Name, w.IsActive }).ToListAsync());

        inv.MapPost("/warehouses", async (WarehouseRequest r, ModulesDbContext db, HttpContext ctx) =>
        {
            if (NeedPost(ctx) is { } no) return no;
            if (string.IsNullOrWhiteSpace(r.Code) || string.IsNullOrWhiteSpace(r.Name)) throw new PostingException("Code and name are required.");
            if (await db.Warehouses.AnyAsync(w => w.Code == r.Code.Trim())) throw new PostingException($"Warehouse {r.Code} already exists.");
            var w = db.Warehouses.Add(new Warehouse { Code = r.Code.Trim().ToUpperInvariant(), Name = r.Name.Trim() }).Entity;
            await db.SaveChangesAsync();
            return Results.Created($"/api/inventory/warehouses/{w.Id}", new { w.Id, w.Code, w.Name });
        });

        inv.MapGet("/stockable-items", async (StockEngine stock) =>
            (await stock.ItemsAsync()).Values.Where(i => i.IsGoods && i.IsActive).OrderBy(i => i.Code));

        // On hand per item × warehouse, with company-wide weighted-average cost and reorder flags.
        inv.MapGet("/stock", async (ModulesDbContext db, StockEngine stock) =>
        {
            var lines = await db.StockMoveLines.Select(l => new { l.ItemId, l.WarehouseId, l.Quantity, l.Value }).ToListAsync();
            var items = await stock.ItemsAsync(lines.Select(l => l.ItemId));
            var whs = await db.Warehouses.ToDictionaryAsync(w => w.Id);
            var reorder = await db.StockItemSettings.ToDictionaryAsync(s => s.ItemId, s => s.ReorderLevel);
            var byItem = lines.GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => (qty: g.Sum(x => x.Quantity), value: g.Sum(x => x.Value)));
            return lines.GroupBy(l => (l.ItemId, l.WarehouseId)).Select(g =>
            {
                var it = items.GetValueOrDefault(g.Key.ItemId);
                var (tq, tv) = byItem[g.Key.ItemId];
                var avg = tq > 0 ? Math.Round(tv / tq, 4) : 0;
                var qty = g.Sum(x => x.Quantity);
                var level = reorder.GetValueOrDefault(g.Key.ItemId);
                return new
                {
                    itemId = g.Key.ItemId, itemCode = it?.Code, itemName = it?.Name, unit = it?.Unit,
                    warehouse = whs.GetValueOrDefault(g.Key.WarehouseId)?.Code, quantity = qty, avgCost = avg, value = Math.Round(qty * avg, 2),
                    itemTotal = tq, reorderLevel = level, belowReorder = level > 0 && tq < level,
                };
            }).Where(r => r.quantity != 0).OrderBy(r => r.itemCode).ThenBy(r => r.warehouse);
        });

        // Item-level valuation reconciled to the GL inventory control account.
        inv.MapGet("/valuation", async (ModulesDbContext db, StockEngine stock, LedgerService ledger) =>
        {
            var lines = await db.StockMoveLines.Select(l => new { l.ItemId, l.Quantity, l.Value }).ToListAsync();
            var items = await stock.ItemsAsync(lines.Select(l => l.ItemId));
            var rows = lines.GroupBy(l => l.ItemId).Select(g => new
            {
                itemId = g.Key, itemCode = items.GetValueOrDefault(g.Key)?.Code, itemName = items.GetValueOrDefault(g.Key)?.Name,
                quantity = g.Sum(x => x.Quantity), value = g.Sum(x => x.Value),
                avgCost = g.Sum(x => x.Quantity) > 0 ? Math.Round(g.Sum(x => x.Value) / g.Sum(x => x.Quantity), 4) : 0,
            }).OrderBy(r => r.itemCode).ToList();
            var total = rows.Sum(r => r.value);
            var p = await db.ProfileAsync();
            decimal? gl = null;
            if (p?.InventoryAccountId is int acc)
            {
                var tb = await ledger.GetTrialBalanceAsync(DateOnly.MinValue, DateOnly.FromDateTime(DateTime.Today.AddYears(50)));
                var row = tb.Rows.FirstOrDefault(r => r.AccountId == acc);
                gl = row is null ? 0 : row.ClosingDebit - row.ClosingCredit;
            }
            return new { rows, totalValue = total, glInventoryBalance = gl, difference = gl is null ? (decimal?)null : gl - total };
        });

        inv.MapGet("/reorder", async (ModulesDbContext db, StockEngine stock) =>
        {
            var settings = await db.StockItemSettings.Where(s => s.ReorderLevel > 0).ToListAsync();
            var qtys = (await db.StockMoveLines.Select(l => new { l.ItemId, l.Quantity }).ToListAsync()).GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
            var items = await stock.ItemsAsync(settings.Select(s => s.ItemId));
            return settings.Select(s => new { s.ItemId, itemCode = items.GetValueOrDefault(s.ItemId)?.Code, itemName = items.GetValueOrDefault(s.ItemId)?.Name, onHand = qtys.GetValueOrDefault(s.ItemId), s.ReorderLevel })
                .Where(r => r.onHand < r.ReorderLevel).OrderBy(r => r.itemCode);
        });

        inv.MapPut("/items/{itemId:int}/reorder", async (int itemId, ReorderRequest r, ModulesDbContext db, HttpContext ctx) =>
        {
            if (NeedPost(ctx) is { } no) return no;
            var s = await db.StockItemSettings.FirstOrDefaultAsync(x => x.ItemId == itemId) ?? db.StockItemSettings.Add(new StockItemSetting { ItemId = itemId }).Entity;
            s.ReorderLevel = r.ReorderLevel;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        inv.MapGet("/moves", async (ModulesDbContext db, StockEngine stock, int? take) =>
        {
            var moves = await db.StockMoves.Include(m => m.Lines).OrderByDescending(m => m.Id).Take(take ?? 500).ToListAsync();
            var items = await stock.ItemsAsync(moves.SelectMany(m => m.Lines).Select(l => l.ItemId));
            var whs = await db.Warehouses.ToDictionaryAsync(w => w.Id);
            return moves.Select(m => MoveDto(m, items, whs));
        });

        // ── Postings ──────────────────────────────────────────────────────────
        async Task<IResult> Done(StockMove m, ModulesDbContext db, StockEngine stock)
        {
            var items = await stock.ItemsAsync(m.Lines.Select(l => l.ItemId));
            var whs = await db.Warehouses.ToDictionaryAsync(w => w.Id);
            return Results.Created($"/api/inventory/moves/{m.Id}", MoveDto(m, items, whs));
        }

        inv.MapPost("/receipts", async (ReceiptRequest r, StockEngine stock, ModulesDbContext db, HttpContext ctx, ClaimsPrincipal u) =>
        {
            if (NeedPost(ctx) is { } no) return no;
            var p = await db.ProfileAsync();
            var invAcc = p?.InventoryAccountId.Need("inventory") ?? throw new PostingException("Set up Settings → Industry & Modules first.");
            var credit = r.CreditAccountId ?? p!.StockClearingAccountId.Need("goods-received clearing");
            var m = await stock.PostAsync(StockMoveType.Receipt, r.Date, r.Reference, r.Narration, WriteEndpoints.Actor(u),
                r.Lines.Select(l => new StockLineInput(l.ItemId, r.WarehouseId, Math.Abs(l.Quantity), l.UnitCost)).ToList(),
                mv => [new(invAcc, null, "Stock received", StockEngine.InValue(mv), 0), new(credit, null, "Stock received", 0, StockEngine.InValue(mv))]);
            return await Done(m, db, stock);
        });

        inv.MapPost("/issues", async (IssueRequest r, StockEngine stock, ModulesDbContext db, HttpContext ctx, ClaimsPrincipal u) =>
        {
            if (NeedPost(ctx) is { } no) return no;
            var p = await db.ProfileAsync();
            var invAcc = p?.InventoryAccountId.Need("inventory") ?? throw new PostingException("Set up Settings → Industry & Modules first.");
            var debit = r.DebitAccountId ?? p!.CogsAccountId.Need("cost of goods sold");
            var m = await stock.PostAsync(StockMoveType.Issue, r.Date, r.Reference, r.Narration, WriteEndpoints.Actor(u),
                r.Lines.Select(l => new StockLineInput(l.ItemId, r.WarehouseId, -Math.Abs(l.Quantity))).ToList(),
                mv => [new(debit, null, "Stock issued", StockEngine.OutValue(mv), 0), new(invAcc, null, "Stock issued", 0, StockEngine.OutValue(mv))]);
            return await Done(m, db, stock);
        });

        inv.MapPost("/transfers", async (TransferRequest r, StockEngine stock, ModulesDbContext db, HttpContext ctx, ClaimsPrincipal u) =>
        {
            if (NeedPost(ctx) is { } no) return no;
            if (r.FromWarehouseId == r.ToWarehouseId) throw new PostingException("Choose two different warehouses.");
            var lines = r.Lines.SelectMany(l => new[] { new StockLineInput(l.ItemId, r.FromWarehouseId, -Math.Abs(l.Quantity)), new StockLineInput(l.ItemId, r.ToWarehouseId, Math.Abs(l.Quantity)) }).ToList();
            var m = await stock.PostAsync(StockMoveType.Transfer, r.Date, null, r.Narration, WriteEndpoints.Actor(u), lines, glLines: null, inboundCostFromOutbound: true);
            return await Done(m, db, stock);
        });

        inv.MapPost("/adjustments", async (AdjustmentRequest r, StockEngine stock, ModulesDbContext db, HttpContext ctx, ClaimsPrincipal u) =>
        {
            if (NeedPost(ctx) is { } no) return no;
            var p = await db.ProfileAsync();
            var invAcc = p?.InventoryAccountId.Need("inventory") ?? throw new PostingException("Set up Settings → Industry & Modules first.");
            var adj = p!.StockAdjustmentAccountId.Need("stock adjustment");
            // Increases without a cost come in at current average cost.
            var lines = new List<StockLineInput>();
            foreach (var l in r.Lines)
                lines.Add(new StockLineInput(l.ItemId, r.WarehouseId, l.Quantity, l.Quantity > 0 ? l.UnitCost ?? await stock.AvgCostAsync(l.ItemId) : null));
            var m = await stock.PostAsync(StockMoveType.Adjustment, r.Date, null, r.Reason, WriteEndpoints.Actor(u), lines, mv =>
            {
                var net = StockEngine.InValue(mv) - StockEngine.OutValue(mv);
                return net >= 0
                    ? [new(invAcc, null, $"Stock adjustment: {r.Reason}", net, 0), new(adj, null, $"Stock adjustment: {r.Reason}", 0, net)]
                    : [new(adj, null, $"Stock adjustment: {r.Reason}", -net, 0), new(invAcc, null, $"Stock adjustment: {r.Reason}", 0, -net)];
            });
            return await Done(m, db, stock);
        });

        // Relieve stock + book COGS for the goods lines of a posted C-ERP sales invoice (once).
        inv.MapPost("/issue-for-invoice/{invoiceId:int}", async (int invoiceId, InvoiceIssueRequest r, StockEngine stock, ModulesDbContext db,
            IDbContextFactory<AegisDbContext> erp, HttpContext ctx, ClaimsPrincipal u) =>
        {
            if (NeedPost(ctx) is { } no) return no;
            await using var e = await erp.CreateDbContextAsync();
            var invoice = await e.SalesInvoices.AsNoTracking().Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == invoiceId)
                ?? throw new PostingException("Invoice not found.");
            if (invoice.Status != VoucherStatus.Posted) throw new PostingException("Only a posted invoice can be issued from stock.");
            if (await db.StockMoves.AnyAsync(m => m.Type == StockMoveType.SaleIssue && m.Reference == invoice.InvoiceNo))
                throw new PostingException($"Stock for {invoice.InvoiceNo} was already issued.");
            var goodsIds = invoice.Lines.Where(l => l.ItemId != null).Select(l => l.ItemId!.Value).ToList();
            var items = await stock.ItemsAsync(goodsIds);
            var lines = invoice.Lines.Where(l => l.ItemId is int id && items.GetValueOrDefault(id)?.IsGoods == true)
                .Select(l => new StockLineInput(l.ItemId!.Value, r.WarehouseId, -l.Quantity)).ToList();
            if (lines.Count == 0) throw new PostingException($"{invoice.InvoiceNo} has no stocked goods lines.");
            var p = await db.ProfileAsync();
            var invAcc = p?.InventoryAccountId.Need("inventory") ?? throw new PostingException("Set up Settings → Industry & Modules first.");
            var cogs = p!.CogsAccountId.Need("cost of goods sold");
            var m = await stock.PostAsync(StockMoveType.SaleIssue, r.Date ?? invoice.Date, invoice.InvoiceNo, $"Goods sold on {invoice.InvoiceNo}", WriteEndpoints.Actor(u), lines,
                mv => [new(cogs, null, $"COGS {invoice.InvoiceNo}", StockEngine.OutValue(mv), 0), new(invAcc, null, $"COGS {invoice.InvoiceNo}", 0, StockEngine.OutValue(mv))]);
            return await Done(m, db, stock);
        });
    }
}
