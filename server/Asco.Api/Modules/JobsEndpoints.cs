using System.Security.Claims;
using AegisErp.Domain;
using AegisErp.Infrastructure;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Asco.Api.Modules;

public record JobRequest(string Title, JobType Type, int? CustomerId, decimal Budget, DateOnly? OpenedDate,
    TransportMode? Mode = null, string? Direction = null, string? Origin = null, string? Destination = null, string? Carrier = null,
    string? AwbBl = null, string? ContainerNo = null, int? Packages = null, decimal? WeightKg = null, DateOnly? Etd = null, DateOnly? Eta = null,
    string? SiteLocation = null, DateOnly? TargetDate = null);
public record JobStatusRequest(JobStatus Status);
public record VehicleRequest(string PlateNo, string Type, decimal? CapacityKg);
public record TripRequest(int VehicleId, int? JobId, string? Driver, DateOnly Date, string From, string To, decimal DistanceKm,
    decimal FuelLitres, decimal FuelCost, decimal Tolls, decimal OtherCost, int? PaidFromAccountId);

/// <summary>
/// Jobs = logistics shipments, construction projects or service engagements. Each job gets its own
/// C-ERP cost centre (code = job number), so every invoice, bill, expense or trip line tagged with it
/// rolls into the job's P&amp;L straight from the general ledger — no double bookkeeping.
/// </summary>
internal static class JobsEndpoints
{
    private sealed record Pnl(decimal Revenue, decimal Cost);

    /// <summary>Revenue/cost per cost centre from posted GL lines (C-ERP ledger is the source of truth).</summary>
    private static async Task<Dictionary<int, Pnl>> PnlByCostCenterAsync(IDbContextFactory<AegisDbContext> erp, IEnumerable<int> ccIds)
    {
        var ids = ccIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        await using var e = await erp.CreateDbContextAsync();
        var lines = await e.JournalLines.AsNoTracking()
            .Where(l => l.CostCenterId != null && ids.Contains(l.CostCenterId.Value) && l.JournalVoucher.Status == VoucherStatus.Posted)
            .Select(l => new { cc = l.CostCenterId!.Value, l.Account.Type, l.Debit, l.Credit }).ToListAsync();
        return lines.GroupBy(l => l.cc).ToDictionary(g => g.Key, g => new Pnl(
            g.Where(x => x.Type == AccountType.Income).Sum(x => x.Credit - x.Debit),
            g.Where(x => x.Type == AccountType.Expense).Sum(x => x.Debit - x.Credit)));
    }

    public static void MapJobsEndpoints(this RouteGroupBuilder api)
    {
        var jobs = api.MapGroup("/jobs").RequireModule(ModuleKeys.Jobs);
        static IResult? NeedPost(HttpContext ctx) => CompanyAccess.From(ctx).CanPost ? null : Results.Problem("Your role in this company is read-only.", statusCode: 403);

        jobs.MapGet("", async (ModulesDbContext db, IDbContextFactory<AegisDbContext> erp) =>
        {
            var list = await db.Jobs.OrderByDescending(j => j.Id).ToListAsync();
            var pnl = await PnlByCostCenterAsync(erp, list.Where(j => j.CostCenterId != null).Select(j => j.CostCenterId!.Value));
            await using var e = await erp.CreateDbContextAsync();
            var custIds = list.Where(j => j.CustomerId != null).Select(j => j.CustomerId!.Value).Distinct().ToList();
            var customers = await e.Customers.AsNoTracking().Where(c => custIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name);
            return list.Select(j =>
            {
                var p = j.CostCenterId is int cc ? pnl.GetValueOrDefault(cc) ?? new Pnl(0, 0) : new Pnl(0, 0);
                var margin = p.Revenue - p.Cost;
                return new
                {
                    j.Id, j.JobNo, j.Title, j.Type, j.Status, customer = j.CustomerId is int c ? customers.GetValueOrDefault(c) : null,
                    j.OpenedDate, j.ClosedDate, j.Mode, j.Direction, j.Origin, j.Destination, j.Carrier, j.AwbBl, j.ContainerNo, j.Packages, j.WeightKg, j.Etd, j.Eta,
                    j.SiteLocation, j.TargetDate, j.Budget, revenue = p.Revenue, cost = p.Cost, margin,
                    marginPct = p.Revenue != 0 ? Math.Round(margin / p.Revenue * 100, 1) : (decimal?)null,
                    budgetUsedPct = j.Budget > 0 ? Math.Round(p.Cost / j.Budget * 100, 1) : (decimal?)null,
                    overBudget = j.Budget > 0 && p.Cost > j.Budget, j.CostCenterId,
                };
            });
        });

        jobs.MapGet("/{id:int}/ledger", async (int id, ModulesDbContext db, IDbContextFactory<AegisDbContext> erp) =>
        {
            var j = await db.Jobs.FirstOrDefaultAsync(x => x.Id == id) ?? throw new PostingException("Job not found.");
            if (j.CostCenterId is not int cc) return Results.Ok(Array.Empty<object>());
            await using var e = await erp.CreateDbContextAsync();
            var rows = await e.JournalLines.AsNoTracking().Where(l => l.CostCenterId == cc && l.JournalVoucher.Status == VoucherStatus.Posted)
                .Select(l => new { l.JournalVoucher.Date, l.JournalVoucher.VoucherNo, account = l.Account.Code + " " + l.Account.Name, l.Account.Type, l.Description, l.Debit, l.Credit })
                .ToListAsync();
            return Results.Ok(rows.OrderBy(r => r.Date).ThenBy(r => r.VoucherNo));
        });

        jobs.MapPost("", async (JobRequest r, ModulesDbContext db, ChartOfAccountsService coa, CurrentCompany current, HttpContext ctx, ClaimsPrincipal u) =>
        {
            if (NeedPost(ctx) is { } no) return no;
            if (string.IsNullOrWhiteSpace(r.Title)) throw new PostingException("Give the job a title.");
            var opened = r.OpenedDate ?? DateOnly.FromDateTime(DateTime.Today);
            var prefix = r.Type switch { JobType.Shipment => "SHP", JobType.Project => "PRJ", _ => "JOB" };
            var head = $"{prefix}-{opened.Year}-";
            var jobNo = Numbering.Next(await db.Jobs.Where(j => j.JobNo.StartsWith(head)).Select(j => j.JobNo).ToListAsync(), prefix, opened.Year);

            // The job's cost centre. C-ERP reserves CreateCostCenterAsync for admins; creating a job's own
            // tracking centre is part of creating the job (which needs CanPost), so it is allowed for this one call.
            var wasAdmin = current.CanAdminister;
            AegisErp.Domain.Entities.CostCenter cc;
            try { current.CanAdminister = true; cc = await coa.CreateCostCenterAsync(jobNo, r.Title.Length > 80 ? r.Title[..80] : r.Title); }
            finally { current.CanAdminister = wasAdmin; }

            var job = db.Jobs.Add(new Job
            {
                JobNo = jobNo, Title = r.Title.Trim(), Type = r.Type, CustomerId = r.CustomerId, CostCenterId = cc.Id, Budget = r.Budget, OpenedDate = opened,
                Mode = r.Mode, Direction = r.Direction, Origin = r.Origin, Destination = r.Destination, Carrier = r.Carrier, AwbBl = r.AwbBl,
                ContainerNo = r.ContainerNo, Packages = r.Packages, WeightKg = r.WeightKg, Etd = r.Etd, Eta = r.Eta,
                SiteLocation = r.SiteLocation, TargetDate = r.TargetDate, CreatedBy = WriteEndpoints.Actor(u), CreatedAtUtc = DateTime.UtcNow,
            }).Entity;
            await db.SaveChangesAsync();
            return Results.Created($"/api/jobs/{job.Id}", new { job.Id, job.JobNo, costCenter = cc.Code });
        });

        jobs.MapPost("/{id:int}/status", async (int id, JobStatusRequest r, ModulesDbContext db, HttpContext ctx) =>
        {
            if (NeedPost(ctx) is { } no) return no;
            var j = await db.Jobs.FirstOrDefaultAsync(x => x.Id == id) ?? throw new PostingException("Job not found.");
            j.Status = r.Status;
            j.ClosedDate = r.Status is JobStatus.Closed or JobStatus.Completed or JobStatus.Cancelled ? DateOnly.FromDateTime(DateTime.Today) : null;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ── Fleet ─────────────────────────────────────────────────────────────
        var fleet = api.MapGroup("/fleet").RequireModule(ModuleKeys.Fleet);

        fleet.MapGet("/vehicles", async (ModulesDbContext db) =>
        {
            var vehicles = await db.Vehicles.OrderBy(v => v.PlateNo).ToListAsync();
            var trips = await db.Trips.Select(t => new { t.VehicleId, t.DistanceKm, t.FuelLitres, t.FuelCost, t.Tolls, t.OtherCost }).ToListAsync();
            return vehicles.Select(v =>
            {
                var mine = trips.Where(t => t.VehicleId == v.Id).ToList();
                var km = mine.Sum(t => t.DistanceKm);
                var cost = mine.Sum(t => t.FuelCost + t.Tolls + t.OtherCost);
                return new
                {
                    v.Id, v.PlateNo, v.Type, v.CapacityKg, v.IsActive, trips = mine.Count, distanceKm = km, fuelLitres = mine.Sum(t => t.FuelLitres),
                    runningCost = cost, costPerKm = km > 0 ? Math.Round(cost / km, 2) : (decimal?)null,
                    kmPerLitre = mine.Sum(t => t.FuelLitres) > 0 ? Math.Round(km / mine.Sum(t => t.FuelLitres), 2) : (decimal?)null,
                };
            });
        });

        fleet.MapPost("/vehicles", async (VehicleRequest r, ModulesDbContext db, HttpContext ctx) =>
        {
            if (NeedPost(ctx) is { } no) return no;
            if (string.IsNullOrWhiteSpace(r.PlateNo)) throw new PostingException("Plate number is required.");
            if (await db.Vehicles.AnyAsync(v => v.PlateNo == r.PlateNo.Trim())) throw new PostingException($"Vehicle {r.PlateNo} already exists.");
            var v = db.Vehicles.Add(new Vehicle { PlateNo = r.PlateNo.Trim().ToUpperInvariant(), Type = r.Type, CapacityKg = r.CapacityKg }).Entity;
            await db.SaveChangesAsync();
            return Results.Created($"/api/fleet/vehicles/{v.Id}", new { v.Id, v.PlateNo });
        });

        fleet.MapGet("/trips", async (ModulesDbContext db) =>
        {
            var trips = await db.Trips.Include(t => t.Vehicle).OrderByDescending(t => t.Date).ThenByDescending(t => t.Id).Take(1000).ToListAsync();
            var jobIds = trips.Where(t => t.JobId != null).Select(t => t.JobId!.Value).Distinct().ToList();
            var jobs2 = await db.Jobs.Where(j => jobIds.Contains(j.Id)).ToDictionaryAsync(j => j.Id, j => j.JobNo);
            return trips.Select(t => new
            {
                t.Id, t.TripNo, t.Date, vehicle = t.Vehicle.PlateNo, job = t.JobId is int j ? jobs2.GetValueOrDefault(j) : null, t.Driver, t.From, t.To,
                t.DistanceKm, t.FuelLitres, t.FuelCost, t.Tolls, t.OtherCost, total = t.FuelCost + t.Tolls + t.OtherCost, t.VoucherNo,
            });
        });

        // Records a trip; if costs are entered, posts Dr fleet expense (tagged to the job's cost centre) / Cr paid-from account.
        fleet.MapPost("/trips", async (TripRequest r, ModulesDbContext db, GlBridge gl, HttpContext ctx, ClaimsPrincipal u) =>
        {
            if (NeedPost(ctx) is { } no) return no;
            var vehicle = await db.Vehicles.FirstOrDefaultAsync(v => v.Id == r.VehicleId && v.IsActive) ?? throw new PostingException("Vehicle not found.");
            Job? job = r.JobId is int jid ? await db.Jobs.FirstOrDefaultAsync(j => j.Id == jid) ?? throw new PostingException("Job not found.") : null;
            if (r.DistanceKm < 0 || r.FuelCost < 0 || r.Tolls < 0 || r.OtherCost < 0) throw new PostingException("Amounts can't be negative.");
            var total = r.FuelCost + r.Tolls + r.OtherCost;
            var head = $"TRP-{r.Date.Year}-";
            var trip = db.Trips.Add(new Trip
            {
                TripNo = Numbering.Next(await db.Trips.Where(t => t.TripNo.StartsWith(head)).Select(t => t.TripNo).ToListAsync(), "TRP", r.Date.Year),
                VehicleId = vehicle.Id, JobId = job?.Id, Driver = r.Driver, Date = r.Date, From = r.From, To = r.To, DistanceKm = r.DistanceKm,
                FuelLitres = r.FuelLitres, FuelCost = r.FuelCost, Tolls = r.Tolls, OtherCost = r.OtherCost, CreatedBy = WriteEndpoints.Actor(u), CreatedAtUtc = DateTime.UtcNow,
            }).Entity;

            AegisErp.Domain.Entities.JournalVoucher? v = null;
            if (total > 0)
            {
                var p = await db.ProfileAsync();
                var exp = p?.FleetExpenseAccountId.Need("fleet running cost") ?? throw new PostingException("Set up Settings → Industry & Modules first.");
                var paidFrom = r.PaidFromAccountId ?? throw new PostingException("Choose the bank/cash account the trip costs were paid from.");
                v = await gl.PostAsync(r.Date, $"Trip {trip.TripNo} {vehicle.PlateNo} {r.From} → {r.To}", trip.TripNo, WriteEndpoints.Actor(u),
                [
                    new(exp, job?.CostCenterId, $"Fuel/tolls/other — {vehicle.PlateNo}", total, 0),
                    new(paidFrom, null, $"Trip {trip.TripNo}", 0, total),
                ]);
                trip.VoucherNo = v.VoucherNo;
            }
            await gl.SaveOrReverseAsync(db, v, WriteEndpoints.Actor(u));
            return Results.Created($"/api/fleet/trips/{trip.Id}", new { trip.Id, trip.TripNo, trip.VoucherNo });
        });
    }
}
