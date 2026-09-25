using System.Security.Claims;
using AegisErp.Domain;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Asco.Api.Modules;

public record BomLineRequest(int ComponentItemId, decimal Quantity);
public record BomRequest(string Code, string Name, int OutputItemId, decimal OutputQuantity, decimal ConversionCost, List<BomLineRequest> Lines);
public record ProductionOrderRequest(int BomId, decimal Quantity, DateOnly PlannedDate, int SourceWarehouseId, int OutputWarehouseId);
public record CompleteRequest(DateOnly? Date);

internal static class ManufacturingEndpoints
{
    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    public static void MapManufacturingEndpoints(this RouteGroupBuilder api)
    {
        var mfg = api.MapGroup("/manufacturing").RequireModule(ModuleKeys.Manufacturing);
        static IResult? NeedPost(HttpContext ctx) => CompanyAccess.From(ctx).CanPost ? null : Results.Problem("Your role in this company is read-only.", statusCode: 403);

        mfg.MapGet("/boms", async (ModulesDbContext db, StockEngine stock) =>
        {
            var boms = await db.Boms.Include(b => b.Lines).OrderBy(b => b.Code).ToListAsync();
            var items = await stock.ItemsAsync(boms.Select(b => b.OutputItemId).Concat(boms.SelectMany(b => b.Lines).Select(l => l.ComponentItemId)));
            var result = new List<object>();
            foreach (var b in boms)
            {
                // Standard material cost per batch at today's average costs.
                var material = 0m;
                foreach (var l in b.Lines) material += R2(l.Quantity * await stock.AvgCostAsync(l.ComponentItemId));
                result.Add(new
                {
                    b.Id, b.Code, b.Name, b.OutputItemId, outputItem = items.GetValueOrDefault(b.OutputItemId)?.Name, b.OutputQuantity, b.ConversionCost, b.IsActive,
                    standardMaterialCost = material, standardUnitCost = b.OutputQuantity > 0 ? Math.Round((material + b.ConversionCost) / b.OutputQuantity, 4) : 0,
                    lines = b.Lines.Select(l => new { l.ComponentItemId, itemCode = items.GetValueOrDefault(l.ComponentItemId)?.Code, itemName = items.GetValueOrDefault(l.ComponentItemId)?.Name, l.Quantity }),
                });
            }
            return result;
        });

        mfg.MapPost("/boms", async (BomRequest r, ModulesDbContext db, StockEngine stock, HttpContext ctx) =>
        {
            if (NeedPost(ctx) is { } no) return no;
            if (string.IsNullOrWhiteSpace(r.Code) || string.IsNullOrWhiteSpace(r.Name)) throw new PostingException("Code and name are required.");
            if (r.OutputQuantity <= 0) throw new PostingException("Output quantity must be positive.");
            if (r.Lines.Count == 0 || r.Lines.Any(l => l.Quantity <= 0)) throw new PostingException("Add components with positive quantities.");
            if (r.Lines.Any(l => l.ComponentItemId == r.OutputItemId)) throw new PostingException("A product can't be a component of itself.");
            var items = await stock.ItemsAsync(r.Lines.Select(l => l.ComponentItemId).Append(r.OutputItemId));
            foreach (var id in r.Lines.Select(l => l.ComponentItemId).Append(r.OutputItemId))
                if (items.GetValueOrDefault(id) is not { IsGoods: true }) throw new PostingException($"Item #{id} must be an existing goods item.");
            if (await db.Boms.AnyAsync(b => b.Code == r.Code.Trim())) throw new PostingException($"BOM {r.Code} already exists.");
            var bom = new Bom { Code = r.Code.Trim().ToUpperInvariant(), Name = r.Name.Trim(), OutputItemId = r.OutputItemId, OutputQuantity = r.OutputQuantity, ConversionCost = r.ConversionCost };
            bom.Lines.AddRange(r.Lines.Select(l => new BomLine { ComponentItemId = l.ComponentItemId, Quantity = l.Quantity }));
            db.Boms.Add(bom);
            await db.SaveChangesAsync();
            return Results.Created($"/api/manufacturing/boms/{bom.Id}", new { bom.Id, bom.Code });
        });

        mfg.MapGet("/orders", async (ModulesDbContext db, StockEngine stock) =>
        {
            var orders = await db.ProductionOrders.Include(o => o.Bom).OrderByDescending(o => o.Id).ToListAsync();
            var items = await stock.ItemsAsync(orders.Select(o => o.Bom.OutputItemId));
            var whs = await db.Warehouses.ToDictionaryAsync(w => w.Id);
            return orders.Select(o => new
            {
                o.Id, o.OrderNo, bom = o.Bom.Code, product = items.GetValueOrDefault(o.Bom.OutputItemId)?.Name, o.Quantity, o.PlannedDate, o.Status,
                from = whs.GetValueOrDefault(o.SourceWarehouseId)?.Code, to = whs.GetValueOrDefault(o.OutputWarehouseId)?.Code,
                o.CompletedDate, o.MaterialCost, o.ConversionCost, totalCost = o.MaterialCost + o.ConversionCost, o.UnitCost, o.VoucherNo,
            });
        });

        mfg.MapPost("/orders", async (ProductionOrderRequest r, ModulesDbContext db, HttpContext ctx, ClaimsPrincipal u) =>
        {
            if (NeedPost(ctx) is { } no) return no;
            if (r.Quantity <= 0) throw new PostingException("Quantity must be positive.");
            var bom = await db.Boms.FirstOrDefaultAsync(b => b.Id == r.BomId && b.IsActive) ?? throw new PostingException("BOM not found.");
            if (!await db.Warehouses.AnyAsync(w => w.Id == r.SourceWarehouseId) || !await db.Warehouses.AnyAsync(w => w.Id == r.OutputWarehouseId))
                throw new PostingException("Choose valid source and output warehouses.");
            var head = $"MO-{r.PlannedDate.Year}-";
            var no2 = Numbering.Next(await db.ProductionOrders.Where(o => o.OrderNo.StartsWith(head)).Select(o => o.OrderNo).ToListAsync(), "MO", r.PlannedDate.Year);
            var o = db.ProductionOrders.Add(new ProductionOrder
            {
                OrderNo = no2, BomId = bom.Id, Quantity = r.Quantity, PlannedDate = r.PlannedDate, SourceWarehouseId = r.SourceWarehouseId,
                OutputWarehouseId = r.OutputWarehouseId, CreatedBy = WriteEndpoints.Actor(u), CreatedAtUtc = DateTime.UtcNow,
            }).Entity;
            await db.SaveChangesAsync();
            return Results.Created($"/api/manufacturing/orders/{o.Id}", new { o.Id, o.OrderNo, o.Status });
        });

        // Material requirements for released orders vs stock on hand in their source warehouse.
        mfg.MapGet("/requirements", async (ModulesDbContext db, StockEngine stock) =>
        {
            var orders = await db.ProductionOrders.Include(o => o.Bom).ThenInclude(b => b.Lines).Where(o => o.Status == ProductionStatus.Released).ToListAsync();
            var need = orders.SelectMany(o => o.Bom.Lines.Select(l => (l.ComponentItemId, o.SourceWarehouseId, qty: l.Quantity * o.Quantity / o.Bom.OutputQuantity)))
                .GroupBy(x => (x.ComponentItemId, x.SourceWarehouseId)).ToList();
            var items = await stock.ItemsAsync(need.Select(g => g.Key.ComponentItemId));
            var whs = await db.Warehouses.ToDictionaryAsync(w => w.Id);
            var rows = new List<object>();
            foreach (var g in need)
            {
                var required = Math.Round(g.Sum(x => x.qty), 4);
                var onHand = await stock.OnHandAsync(g.Key.ComponentItemId, g.Key.SourceWarehouseId);
                rows.Add(new { itemCode = items.GetValueOrDefault(g.Key.ComponentItemId)?.Code, itemName = items.GetValueOrDefault(g.Key.ComponentItemId)?.Name, warehouse = whs.GetValueOrDefault(g.Key.SourceWarehouseId)?.Code, required, onHand, shortage = Math.Max(0, required - onHand) });
            }
            return rows;
        });

        // Complete: issue components (avg cost) + receive finished goods at material + conversion cost.
        mfg.MapPost("/orders/{id:int}/complete", async (int id, CompleteRequest r, ModulesDbContext db, StockEngine stock, GlBridge gl, HttpContext ctx, ClaimsPrincipal u) =>
        {
            if (NeedPost(ctx) is { } no) return no;
            var o = await db.ProductionOrders.Include(x => x.Bom).ThenInclude(b => b.Lines).FirstOrDefaultAsync(x => x.Id == id) ?? throw new PostingException("Production order not found.");
            if (o.Status != ProductionStatus.Released) throw new PostingException($"{o.OrderNo} is {o.Status}.");
            var p = await db.ProfileAsync();
            var invAcc = p?.InventoryAccountId.Need("inventory") ?? throw new PostingException("Set up Settings → Industry & Modules first.");
            var batches = o.Quantity / o.Bom.OutputQuantity;
            var conversion = R2(o.Bom.ConversionCost * batches);
            int? convAcc = conversion > 0 ? p!.ConversionCostAccountId.Need("production overhead absorbed") : null;
            var date = r.Date ?? DateOnly.FromDateTime(DateTime.Today);
            var actor = WriteEndpoints.Actor(u);

            // One atomic move: components out (at average cost) + finished goods in at material + conversion.
            // Raw materials and finished goods share the inventory account, so the material transfer nets
            // to zero in the GL — only the absorbed conversion cost posts (Dr Inventory / Cr overhead absorbed).
            // The order's completion is saved in the same SaveChanges as the move (onComputed).
            var lines = o.Bom.Lines.Select(l => new StockLineInput(l.ComponentItemId, o.SourceWarehouseId, -Math.Round(l.Quantity * batches, 4)))
                .Append(new StockLineInput(o.Bom.OutputItemId, o.OutputWarehouseId, o.Quantity)).ToList();
            var move = await stock.PostAsync(StockMoveType.ProductionReceipt, date, o.OrderNo, $"Production {o.OrderNo}", actor, lines,
                glLines: conversion > 0 ? _ => [new(invAcc, null, $"Conversion cost {o.OrderNo}", conversion, 0), new(convAcc!.Value, null, $"Overhead absorbed {o.OrderNo}", 0, conversion)] : null,
                productionOrderId: o.Id,
                inboundValueFromOutbound: material => material + conversion,
                onComputed: mv =>
                {
                    var material = StockEngine.OutValue(mv);
                    o.Status = ProductionStatus.Completed;
                    o.CompletedDate = date;
                    o.MaterialCost = material;
                    o.ConversionCost = conversion;
                    o.UnitCost = Math.Round((material + conversion) / o.Quantity, 4);
                });
            o.VoucherNo = move.VoucherNo;
            if (move.VoucherNo is not null) await db.SaveChangesAsync(); // voucher no is only known after posting
            return Results.Ok(new { o.OrderNo, o.MaterialCost, o.ConversionCost, o.UnitCost, move = move.MoveNo, o.VoucherNo });
        });

        mfg.MapPost("/orders/{id:int}/cancel", async (int id, ModulesDbContext db, HttpContext ctx) =>
        {
            if (NeedPost(ctx) is { } no) return no;
            var o = await db.ProductionOrders.FirstOrDefaultAsync(x => x.Id == id) ?? throw new PostingException("Production order not found.");
            if (o.Status != ProductionStatus.Released) throw new PostingException($"{o.OrderNo} is {o.Status} and can't be cancelled.");
            o.Status = ProductionStatus.Cancelled;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
