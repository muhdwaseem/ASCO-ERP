using AegisErp.Domain;
using AegisErp.Infrastructure;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Asco.Api.Modules;

public record StockLineInput(int ItemId, int WarehouseId, decimal Quantity, decimal? UnitCost = null);
public record StockItemInfo(int Id, string Code, string Name, string Unit, bool IsGoods, bool IsActive);

/// <summary>
/// Inventory engine. Weighted-average cost per item (company-wide), stock tracked per warehouse,
/// negative stock refused, and each move's valuation posted to C-ERP's GL through GlBridge.
/// Moves are append-only: corrections are made with an adjustment, never by editing history.
/// </summary>
public sealed class StockEngine(ModulesDbContext db, IDbContextFactory<AegisDbContext> erp, GlBridge gl, ICurrentCompany current)
{
    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    private static decimal R4(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

    public async Task<Dictionary<int, StockItemInfo>> ItemsAsync(IEnumerable<int>? ids = null)
    {
        await using var e = await erp.CreateDbContextAsync();
        var q = e.Items.AsNoTracking();
        if (ids is not null) { var set = ids.Distinct().ToList(); q = q.Where(i => set.Contains(i.Id)); }
        return (await q.Select(i => new { i.Id, i.Code, i.Name, i.Unit, i.Kind, i.IsActive }).ToListAsync())
            .ToDictionary(i => i.Id, i => new StockItemInfo(i.Id, i.Code, i.Name, i.Unit, i.Kind == ItemKind.Goods, i.IsActive));
    }

    public async Task<decimal> OnHandAsync(int itemId, int warehouseId) =>
        (await db.StockMoveLines.Where(l => l.ItemId == itemId && l.WarehouseId == warehouseId).Select(l => l.Quantity).ToListAsync()).Sum();

    /// <summary>Weighted-average unit cost across all warehouses (value on hand ÷ quantity on hand).</summary>
    public async Task<decimal> AvgCostAsync(int itemId)
    {
        var rows = await db.StockMoveLines.Where(l => l.ItemId == itemId).Select(l => new { l.Quantity, l.Value, l.UnitCost, l.Id }).ToListAsync();
        var qty = rows.Sum(r => r.Quantity);
        if (qty > 0) return R4(rows.Sum(r => r.Value) / qty);
        return rows.Where(r => r.Quantity > 0).OrderByDescending(r => r.Id).Select(r => r.UnitCost).FirstOrDefault();
    }

    /// <summary>
    /// Posts one stock move. Out-lines are valued at current average cost; in-lines need a unit cost
    /// unless <paramref name="inboundCostFromOutbound"/> (transfers / production). <paramref name="glLines"/>
    /// receives the computed move and returns the voucher lines (or none).
    /// </summary>
    public async Task<StockMove> PostAsync(StockMoveType type, DateOnly date, string? reference, string? narration, string actor,
        IReadOnlyList<StockLineInput> lines, Func<StockMove, IEnumerable<VoucherLineInput>>? glLines,
        bool inboundCostFromOutbound = false, int? productionOrderId = null,
        Func<decimal, decimal>? inboundValueFromOutbound = null, Action<StockMove>? onComputed = null)
    {
        if (lines.Count == 0 || lines.Any(l => l.Quantity == 0)) throw new PostingException("Add at least one line with a non-zero quantity.");
        var companyId = current.CompanyId ?? throw new PostingException("No active company.");

        var items = await ItemsAsync(lines.Select(l => l.ItemId));
        foreach (var l in lines)
        {
            if (!items.TryGetValue(l.ItemId, out var it)) throw new PostingException($"Item #{l.ItemId} not found.");
            if (!it.IsGoods) throw new PostingException($"{it.Code} {it.Name} is a service item — only goods are stocked.");
        }
        var whIds = lines.Select(l => l.WarehouseId).Distinct().ToList();
        var whs = await db.Warehouses.Where(w => whIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id);
        foreach (var id in whIds)
            if (!whs.TryGetValue(id, out var w) || !w.IsActive) throw new PostingException($"Warehouse #{id} not found or inactive.");

        // Postgres: one transaction + advisory lock so two users can't both issue the last unit.
        // Sqlite (dev): single writer already serialises; a long read transaction would conflict with
        // C-ERP's own GL write in WAL mode, so none is opened there.
        await using var tx = db.Database.IsNpgsql() ? await db.Database.BeginTransactionAsync() : null;
        if (tx is not null) await Locks.TakeAsync(db, companyId, "stock");

        var move = new StockMove
        {
            Type = type, Date = date, Reference = reference, Narration = narration, CreatedBy = actor,
            CreatedAtUtc = DateTime.UtcNow, ProductionOrderId = productionOrderId,
        };

        var running = new Dictionary<(int, int), decimal>();
        var outValueTotal = 0m;
        foreach (var l in lines.Where(l => l.Quantity < 0))
        {
            var key = (l.ItemId, l.WarehouseId);
            if (!running.ContainsKey(key)) running[key] = await OnHandAsync(l.ItemId, l.WarehouseId);
            if (running[key] + l.Quantity < 0)
                throw new PostingException($"Insufficient stock: {items[l.ItemId].Code} {items[l.ItemId].Name} at {whs[l.WarehouseId].Code} has {running[key]:0.####}, needs {-l.Quantity:0.####}.");
            running[key] += l.Quantity;
            var cost = await AvgCostAsync(l.ItemId);
            var value = R2(l.Quantity * cost);
            outValueTotal += -value;
            move.Lines.Add(new StockMoveLine { ItemId = l.ItemId, WarehouseId = l.WarehouseId, Quantity = l.Quantity, UnitCost = cost, Value = value });
        }

        var inLines = lines.Where(l => l.Quantity > 0).ToList();
        if (inboundValueFromOutbound is not null && inLines.Count > 0)
        {
            // Production: finished goods absorb the issued material value + conversion cost.
            var total = inboundValueFromOutbound(outValueTotal);
            var qty = inLines.Sum(l => l.Quantity);
            foreach (var l in inLines)
            {
                var value = R2(total * l.Quantity / qty);
                move.Lines.Add(new StockMoveLine { ItemId = l.ItemId, WarehouseId = l.WarehouseId, Quantity = l.Quantity, UnitCost = R4(value / l.Quantity), Value = value });
            }
        }
        else
        {
            foreach (var l in inLines)
            {
                decimal cost;
                if (inboundCostFromOutbound)
                    cost = move.Lines.FirstOrDefault(o => o.ItemId == l.ItemId && o.Quantity < 0)?.UnitCost ?? await AvgCostAsync(l.ItemId);
                else if (l.UnitCost is decimal c && c >= 0) cost = c;
                else throw new PostingException($"Enter a unit cost for {items[l.ItemId].Code} {items[l.ItemId].Name}.");
                move.Lines.Add(new StockMoveLine { ItemId = l.ItemId, WarehouseId = l.WarehouseId, Quantity = l.Quantity, UnitCost = R4(cost), Value = R2(l.Quantity * cost) });
            }
        }

        var head = $"STK-{date.Year}-";
        var existing = await db.StockMoves.Where(m => m.MoveNo.StartsWith(head)).Select(m => m.MoveNo).ToListAsync();
        move.MoveNo = Numbering.Next(existing, "STK", date.Year);
        db.StockMoves.Add(move);

        AegisErp.Domain.Entities.JournalVoucher? v = null;
        onComputed?.Invoke(move); // lets callers update related rows so they save atomically with the move
        var vlines = glLines?.Invoke(move).ToList();
        if (vlines is { Count: > 0 })
        {
            v = await gl.PostAsync(date, $"{type} {move.MoveNo}{(narration is null ? "" : " — " + narration)}", move.MoveNo, actor, vlines);
            move.VoucherNo = v.VoucherNo;
        }
        await gl.SaveOrReverseAsync(db, v, actor);
        if (tx is not null) await tx.CommitAsync();
        return move;
    }

    public static decimal InValue(StockMove m) => m.Lines.Where(l => l.Quantity > 0).Sum(l => l.Value);
    public static decimal OutValue(StockMove m) => -m.Lines.Where(l => l.Quantity < 0).Sum(l => l.Value);
}
