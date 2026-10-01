using System.Security.Claims;
using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Asco.Api.Modules;

public record GratuityProvisionRequest(DateOnly AsOf, int ExpenseAccountId);
public record GratuitySettleRequest(int EmployeeId, DateOnly LeavingDate, int ExpenseAccountId, int? BankAccountId);
public record DisposeAssetRequest(DateOnly Date, decimal Proceeds, int? BankAccountId, int GainLossAccountId);
public record PrepaymentRequest(string Description, int? VendorId, DateOnly StartDate, int Months, decimal Amount,
    int PrepaidAccountId, int ExpenseAccountId, int? CostCenterId, int? PaidFromAccountId, DateOnly? PaidDate);
public record PrepaymentReleaseRequest(DateOnly? UpTo);

/// <summary>
/// Core accounting tools that sit on top of C-ERP's ledger:
///  • Cost-centre P&amp;L matrix (income/expense accounts × cost centres, from posted GL lines);
///  • Gratuity (UAE EOSB) — accrual per employee, month-end provision true-up, settlement of leavers
///    (C-ERP's GratuityService does the termination posting; ASCO utilises the provision against it);
///  • Fixed-asset depreciation schedule and disposal (C-ERP's FixedAssetService);
///  • Prepayments — ASCO's own asco_prepayments, released month by month through GlBridge.
/// </summary>
internal static class AccountingEndpoints
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
    private static DateOnly MonthStart(DateOnly d) => new(d.Year, d.Month, 1);
    private static DateOnly MonthEnd(DateOnly d) => MonthStart(d).AddMonths(1).AddDays(-1);

    private static IResult? NeedPost(HttpContext ctx) =>
        CompanyAccess.From(ctx).CanPost ? null : Results.Problem("Your role in this company is read-only.", statusCode: 403);
    private static IResult? NeedPayroll(HttpContext ctx) =>
        CompanyAccess.From(ctx).CanAccessPayroll ? null : Results.Problem("Payroll access is required.", statusCode: 403);

    // ── Gratuity maths ──────────────────────────────────────────────────────
    // UAE Decree-Law 33/2021 Art. 51: 21 days' basic per year for the first 5 years, 30 days after,
    // capped at 24 months' basic. The provision accrues from day one (IAS 19) — the 1-year minimum
    // only decides whether anything is *payable* on leaving, which C-ERP's CalculateGratuity applies.
    private static decimal DaysEarned(decimal years) => years <= 5m ? 21m * years : 105m + 30m * (years - 5m);
    private static decimal Accrued(decimal basic, decimal years) =>
        basic <= 0 ? 0m : Math.Round(Math.Min(basic / 30m * DaysEarned(years), basic * 24m), 2, MidpointRounding.AwayFromZero);

    internal sealed record GratuityRow(int Id, string EmployeeCode, string FullName, int? CostCenterId, string? CostCenter, DateOnly JoiningDate,
        decimal BasicSalary, decimal Years, decimal Days, decimal Accrued, decimal Payable, bool Eligible);

    private static async Task<(List<GratuityRow> Rows, decimal UnpaidLeavers, decimal Held, int? ProvisionAccountId)> GratuityPositionAsync(AegisDbContext db, DateOnly asOf)
    {
        var emps = await db.Employees.AsNoTracking().Include(e => e.CostCenter)
            .Where(e => e.Status == EmployeeStatus.Active && e.JoiningDate <= asOf).OrderBy(e => e.EmployeeCode).ToListAsync();
        var rows = emps.Select(e =>
        {
            var years = GratuityService.YearsOfService(e.JoiningDate, asOf);
            return new GratuityRow(e.Id, e.EmployeeCode, e.FullName, e.CostCenterId, e.CostCenter?.Code, e.JoiningDate, e.BasicSalary,
                Math.Round(years, 2), Math.Round(DaysEarned(years), 1),
                e.GratuityEligible ? Accrued(e.BasicSalary, years) : 0m,
                e.GratuityEligible ? GratuityService.CalculateGratuity(e.BasicSalary, e.JoiningDate, asOf) : 0m, e.GratuityEligible);
        }).ToList();
        // Leavers already settled by C-ERP but not yet paid still sit in the provision account.
        var unpaid = (await db.GratuityPayments.AsNoTracking().Where(g => !g.IsPaid && g.JournalVoucherId != null).Select(g => g.CalculatedAmount).ToListAsync()).Sum();
        var acct = await db.Accounts.AsNoTracking().Where(a => a.Code == WellKnownAccounts.GratuityPayable).Select(a => (int?)a.Id).FirstOrDefaultAsync();
        decimal held = 0;
        if (acct is int id)
            held = (await db.JournalLines.AsNoTracking()
                .Where(l => l.AccountId == id && l.JournalVoucher.Status == VoucherStatus.Posted && l.JournalVoucher.Date <= asOf)
                .Select(l => new { l.Debit, l.Credit }).ToListAsync()).Sum(l => l.Credit - l.Debit);
        return (rows, unpaid, held, acct);
    }

    /// <summary>C-ERP posts gratuity to the well-known 21040 liability; demo/new charts may not have it yet.</summary>
    private static async Task<int> EnsureProvisionAccountAsync(AegisDbContext db, ChartOfAccountsService coa, string actor)
    {
        var id = await db.Accounts.AsNoTracking().Where(a => a.Code == WellKnownAccounts.GratuityPayable).Select(a => (int?)a.Id).FirstOrDefaultAsync();
        if (id is int x) return x;
        var parent = await db.Accounts.AsNoTracking().Where(a => a.Code == WellKnownAccounts.AccountsPayable).Select(a => a.ParentId).FirstOrDefaultAsync();
        var a = await coa.CreateAsync(new NewAccountInput(WellKnownAccounts.GratuityPayable, "End of Service Benefits (Gratuity) Provision",
            AccountType.Liability, true, "Non-current liability", "AED", parent, "Created by ASCO for the gratuity provision", 0), actor);
        return a.Id;
    }

    private static async Task<Account> RequireAccount(AegisDbContext db, int id, AccountType type, string what)
    {
        var a = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id) ?? throw new PostingException($"{what} account not found.");
        if (a.Type != type) throw new PostingException($"{a.Code} {a.Name} is not {(type == AccountType.Expense ? "an expense" : type == AccountType.Asset ? "an asset" : $"a {type.ToString().ToLower()}")} account.");
        return a;
    }

    public static void MapAccountingEndpoints(this RouteGroupBuilder api)
    {
        // ── Cost-centre / project P&L matrix ────────────────────────────────
        api.MapGet("/reports/cost-centre-pnl", async (IDbContextFactory<AegisDbContext> dbf, DateOnly? from, DateOnly? to) =>
        {
            var f = from ?? new DateOnly(Today.Year, 1, 1);
            var t = to ?? Today;
            await using var db = await dbf.CreateDbContextAsync();
            var lines = await db.JournalLines.AsNoTracking()
                .Where(l => l.JournalVoucher.Status == VoucherStatus.Posted && l.JournalVoucher.Date >= f && l.JournalVoucher.Date <= t
                            && (l.Account.Type == AccountType.Income || l.Account.Type == AccountType.Expense))
                .Select(l => new { l.AccountId, l.Account.Code, l.Account.Name, l.Account.Type, Cc = l.CostCenter == null ? null : l.CostCenter.Code, CcName = l.CostCenter == null ? null : l.CostCenter.Name, l.Debit, l.Credit })
                .ToListAsync();
            const string none = "_none";
            var centres = lines.GroupBy(l => l.Cc ?? none)
                .Select(g => new { key = g.Key, code = g.Key == none ? "—" : g.Key, name = g.First().CcName ?? "Unassigned" })
                .OrderBy(c => c.key == none).ThenBy(c => c.code, StringComparer.Ordinal).ToList();
            object Rows(AccountType type) => lines.Where(l => l.Type == type).GroupBy(l => l.AccountId)
                .Select(g => new
                {
                    code = g.First().Code, name = g.First().Name,
                    amounts = g.GroupBy(x => x.Cc ?? none).ToDictionary(c => c.Key, c => c.Sum(x => type == AccountType.Income ? x.Credit - x.Debit : x.Debit - x.Credit)),
                })
                .OrderBy(r => r.code, StringComparer.Ordinal).ToList();
            return new { from = f, to = t, centres, income = Rows(AccountType.Income), expense = Rows(AccountType.Expense) };
        });

        // ── Gratuity ────────────────────────────────────────────────────────
        api.MapGet("/gratuity", async (IDbContextFactory<AegisDbContext> dbf, DateOnly? asOf, HttpContext ctx) =>
        {
            if (NeedPayroll(ctx) is { } no) return no;
            var d = asOf ?? Today;
            await using var db = await dbf.CreateDbContextAsync();
            var (rows, unpaid, held, acct) = await GratuityPositionAsync(db, d);
            var required = rows.Sum(r => r.Accrued) + unpaid;
            return Results.Ok(new { asOf = d, rows, unpaidLeavers = unpaid, required, held, shortfall = required - held, provisionAccount = acct is null ? null : WellKnownAccounts.GratuityPayable });
        });

        // Month-end true-up: Dr gratuity expense / Cr provision for the shortfall (or the reverse to
        // release an excess). The expense is split across employees' cost centres by their accrual.
        api.MapPost("/gratuity/provision", async (GratuityProvisionRequest r, IDbContextFactory<AegisDbContext> dbf, ChartOfAccountsService coa, GlBridge gl, HttpContext ctx, ClaimsPrincipal u) =>
        {
            if ((NeedPayroll(ctx) ?? NeedPost(ctx)) is { } no) return no;
            await using var db = await dbf.CreateDbContextAsync();
            var expense = await RequireAccount(db, r.ExpenseAccountId, AccountType.Expense, "Gratuity expense");
            var actor = WriteEndpoints.Actor(u);
            var (rows, unpaid, held, _) = await GratuityPositionAsync(db, r.AsOf);
            var required = rows.Sum(x => x.Accrued) + unpaid;
            var diff = Math.Round(required - held, 2);
            if (Math.Abs(diff) < 0.01m)
                return Results.Ok(new { voucher = (string?)null, required, held, message = "The gratuity provision is already up to date." });
            var provision = await EnsureProvisionAccountAsync(db, coa, actor);

            // Split |diff| by each cost centre's share of the accrual; the last share takes the rounding.
            var shares = rows.GroupBy(x => x.CostCenterId).Select(g => (Cc: g.Key, W: g.Sum(x => x.Accrued))).Where(s => s.W > 0).ToList();
            var totalW = shares.Sum(s => s.W);
            var amt = Math.Abs(diff);
            var lines = new List<VoucherLineInput>();
            decimal allocated = 0;
            for (var i = 0; i < shares.Count; i++)
            {
                var part = i == shares.Count - 1 ? amt - allocated : Math.Round(amt * shares[i].W / totalW, 2, MidpointRounding.AwayFromZero);
                allocated += part;
                lines.Add(diff > 0 ? new(expense.Id, shares[i].Cc, $"Gratuity provision {r.AsOf:MMM yyyy}", part, 0) : new(expense.Id, shares[i].Cc, $"Gratuity provision released {r.AsOf:MMM yyyy}", 0, part));
            }
            if (lines.Count == 0) lines.Add(diff > 0 ? new(expense.Id, null, "Gratuity provision", amt, 0) : new(expense.Id, null, "Gratuity provision released", 0, amt));
            lines.Add(diff > 0 ? new(provision, null, "End of service benefits provision", 0, amt) : new(provision, null, "End of service benefits provision", amt, 0));
            var v = await gl.PostAsync(r.AsOf, $"Gratuity (EOSB) provision true-up as of {r.AsOf:dd MMM yyyy} — {rows.Count} employees", "EOSB", actor, lines);
            return Results.Ok(new { voucher = v.VoucherNo, required, held, amount = diff, message = (string?)null });
        });

        // Leaver: C-ERP terminates the employee and posts Dr expense / Cr 21040 for the final amount;
        // ASCO then utilises the existing provision (Dr 21040 / Cr expense, up to what's held) so the
        // expense isn't charged twice, and optionally pays it out (C-ERP MarkPaid: Dr 21040 / Cr bank).
        api.MapPost("/gratuity/settle", async (GratuitySettleRequest r, IDbContextFactory<AegisDbContext> dbf, ChartOfAccountsService coa, GratuityService grat, GlBridge gl, HttpContext ctx, ClaimsPrincipal u) =>
        {
            if ((NeedPayroll(ctx) ?? NeedPost(ctx)) is { } no) return no;
            await using var db = await dbf.CreateDbContextAsync();
            var expense = await RequireAccount(db, r.ExpenseAccountId, AccountType.Expense, "Gratuity expense");
            var actor = WriteEndpoints.Actor(u);
            await EnsureProvisionAccountAsync(db, coa, actor);
            var (_, _, held, _) = await GratuityPositionAsync(db, r.LeavingDate);
            var pay = await grat.PostGratuityAsync(r.EmployeeId, r.LeavingDate, expense.Id, actor, DateTime.UtcNow);
            var emp = await db.Employees.AsNoTracking().FirstAsync(e => e.Id == r.EmployeeId);
            string? utilised = null, paid = null;
            var use = Math.Min(pay.CalculatedAmount, Math.Max(held, 0));
            if (use > 0)
            {
                var provision = await EnsureProvisionAccountAsync(db, coa, actor);
                utilised = (await gl.PostAsync(r.LeavingDate, $"Gratuity for {emp.FullName} ({emp.EmployeeCode}) met from the EOSB provision", "EOSB", actor,
                [
                    new(provision, null, $"Provision utilised — {emp.FullName}", use, 0),
                    new(expense.Id, emp.CostCenterId, $"Provision utilised — {emp.FullName}", 0, use),
                ])).VoucherNo;
            }
            if (r.BankAccountId is int bank && pay.CalculatedAmount > 0)
                paid = (await grat.MarkPaidAsync(pay.Id, bank, r.LeavingDate, actor, DateTime.UtcNow)).VoucherNo;
            return Results.Ok(new { employee = emp.FullName, amount = pay.CalculatedAmount, years = Math.Round(pay.YearsOfService, 2), utilised, paid });
        });

        // ── Fixed assets: schedule + disposal ───────────────────────────────
        api.MapGet("/fixed-assets/{id:int}/schedule", async (int id, IDbContextFactory<AegisDbContext> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var a = await db.FixedAssets.AsNoTracking().Include(x => x.DepreciationEntries).ThenInclude(e => e.JournalVoucher)
                .Include(x => x.AssetAccount).Include(x => x.DepreciationExpenseAccount).Include(x => x.CostCenter)
                .FirstOrDefaultAsync(x => x.Id == id);
            if (a is null) return Results.NotFound();
            var basis = a.PurchaseCost - a.SalvageValue;
            var monthly = a.UsefulLifeMonths > 0 ? Math.Round(basis / a.UsefulLifeMonths, 2, MidpointRounding.AwayFromZero) : 0m;
            var rows = new List<object>();
            decimal acc = 0;
            var no = 0;
            foreach (var e in a.DepreciationEntries.OrderBy(e => e.Date))
            {
                rows.Add(new { no = ++no, month = e.Date.ToString("yyyy-MM"), opening = a.PurchaseCost - acc, depreciation = e.Amount, accumulated = acc + e.Amount, closing = a.PurchaseCost - acc - e.Amount, status = $"Posted {e.JournalVoucher?.VoucherNo}" });
                acc += e.Amount;
            }
            if (a.Status == FixedAssetStatus.Active)
            {
                var month = a.DepreciationEntries.Count > 0 ? MonthStart(a.DepreciationEntries.Max(e => e.Date)).AddMonths(1) : MonthStart(a.PurchaseDate);
                while (acc < basis && monthly > 0 && no < 1200)
                {
                    var amt = Math.Min(monthly, basis - acc);
                    rows.Add(new { no = ++no, month = month.ToString("yyyy-MM"), opening = a.PurchaseCost - acc, depreciation = amt, accumulated = acc + amt, closing = a.PurchaseCost - acc - amt, status = "Planned" });
                    acc += amt;
                    month = month.AddMonths(1);
                }
            }
            return Results.Ok(new
            {
                asset = new
                {
                    a.Id, a.AssetCode, a.Name, a.Category, a.PurchaseDate, a.PurchaseCost, a.SalvageValue, a.UsefulLifeMonths, monthly,
                    assetAccount = $"{a.AssetAccount.Code} · {a.AssetAccount.Name}", expenseAccount = $"{a.DepreciationExpenseAccount.Code} · {a.DepreciationExpenseAccount.Name}",
                    costCenter = a.CostCenter?.Code, a.AccumulatedDepreciation, a.NetBookValue, status = a.Status.ToString(), a.DisposalDate, a.DisposalProceeds,
                },
                rows,
            });
        });

        api.MapPost("/fixed-assets/{id:int}/dispose", async (int id, DisposeAssetRequest r, FixedAssetService svc, LedgerService ledger, HttpContext ctx, ClaimsPrincipal u) =>
        {
            if (NeedPost(ctx) is { } no) return no;
            await WriteEndpoints.OpenPeriodFor(ledger, r.Date);
            var v = await svc.DisposeAsync(id, r.Date, r.Proceeds, r.BankAccountId, r.GainLossAccountId, WriteEndpoints.Actor(u), DateTime.UtcNow);
            return Results.Ok(new { voucher = v.VoucherNo });
        });

        // ── Prepayments ─────────────────────────────────────────────────────
        api.MapGet("/prepayments", async (ModulesDbContext db, IDbContextFactory<AegisDbContext> erp) =>
        {
            var list = await db.Prepayments.AsNoTracking().Include(p => p.Releases).OrderByDescending(p => p.StartDate).ThenByDescending(p => p.Id).ToListAsync();
            await using var e = await erp.CreateDbContextAsync();
            var accts = await e.Accounts.AsNoTracking().Select(a => new { a.Id, a.Code, a.Name }).ToDictionaryAsync(a => a.Id, a => $"{a.Code} · {a.Name}");
            var vendors = await e.Vendors.AsNoTracking().Select(v => new { v.Id, v.Name }).ToDictionaryAsync(v => v.Id, v => v.Name);
            var ccs = await e.CostCenters.AsNoTracking().Select(c => new { c.Id, c.Code }).ToDictionaryAsync(c => c.Id, c => c.Code);
            return list.Select(p =>
            {
                var released = p.Releases.Sum(x => x.Amount);
                return new
                {
                    p.Id, p.PrepaymentNo, p.Description, vendor = p.VendorId is int v ? vendors.GetValueOrDefault(v) : null,
                    p.StartDate, endDate = MonthEnd(p.StartDate.AddMonths(p.Months - 1)), p.Months, p.Amount, monthly = p.MonthAmount(1),
                    prepaidAccount = accts.GetValueOrDefault(p.PrepaidAccountId), expenseAccount = accts.GetValueOrDefault(p.ExpenseAccountId),
                    costCenter = p.CostCenterId is int c ? ccs.GetValueOrDefault(c) : null, p.PaymentVoucherNo,
                    monthsReleased = p.Releases.Count, released, remaining = p.Amount - released,
                    status = p.Cancelled ? "Cancelled" : p.Releases.Count >= p.Months ? "Fully released" : "Active",
                    schedule = Enumerable.Range(1, p.Months).Select(n =>
                    {
                        var rel = p.Releases.FirstOrDefault(x => x.MonthNo == n);
                        return new { no = n, month = p.StartDate.AddMonths(n - 1).ToString("yyyy-MM"), amount = rel?.Amount ?? p.MonthAmount(n), voucherNo = rel?.VoucherNo };
                    }),
                };
            });
        });

        api.MapPost("/prepayments", async (PrepaymentRequest r, ModulesDbContext db, IDbContextFactory<AegisDbContext> erp, GlBridge gl, HttpContext ctx, ClaimsPrincipal u) =>
        {
            if (NeedPost(ctx) is { } no) return no;
            if (string.IsNullOrWhiteSpace(r.Description)) throw new PostingException("Describe the prepayment (e.g. Office rent 2027).");
            if (r.Amount <= 0) throw new PostingException("Enter the amount paid in advance.");
            if (r.Months is < 1 or > 120) throw new PostingException("Months must be between 1 and 120.");
            await using (var e = await erp.CreateDbContextAsync())
            {
                await RequireAccount(e, r.PrepaidAccountId, AccountType.Asset, "Prepaid");
                await RequireAccount(e, r.ExpenseAccountId, AccountType.Expense, "Expense");
            }
            var start = MonthStart(r.StartDate);
            var actor = WriteEndpoints.Actor(u);
            var head = $"PRE-{start.Year}-";
            var p = db.Prepayments.Add(new Prepayment
            {
                PrepaymentNo = Numbering.Next(await db.Prepayments.Where(x => x.PrepaymentNo.StartsWith(head)).Select(x => x.PrepaymentNo).ToListAsync(), "PRE", start.Year),
                Description = r.Description.Trim(), VendorId = r.VendorId, StartDate = start, Months = r.Months, Amount = Math.Round(r.Amount, 2),
                PrepaidAccountId = r.PrepaidAccountId, ExpenseAccountId = r.ExpenseAccountId, CostCenterId = r.CostCenterId,
                CreatedBy = actor, CreatedAtUtc = DateTime.UtcNow,
            }).Entity;
            JournalVoucher? v = null;
            if (r.PaidFromAccountId is int bank)
            {
                v = await gl.PostAsync(r.PaidDate ?? r.StartDate, $"Prepayment {p.PrepaymentNo} — {p.Description}", p.PrepaymentNo, actor,
                [
                    new(r.PrepaidAccountId, r.CostCenterId, $"Paid in advance — {p.Description}", p.Amount, 0),
                    new(bank, null, $"Prepayment {p.PrepaymentNo}", 0, p.Amount),
                ]);
                p.PaymentVoucherNo = v.VoucherNo;
            }
            await gl.SaveOrReverseAsync(db, v, actor);
            return Results.Created($"/api/prepayments/{p.Id}", new { p.Id, number = p.PrepaymentNo, p.PaymentVoucherNo, monthly = p.MonthAmount(1) });
        });

        // Releases every month that is due up to (and including) the given month, one voucher per
        // month dated at its month-end, so catch-up runs still land in the right periods.
        api.MapPost("/prepayments/release", async (PrepaymentReleaseRequest r, ModulesDbContext db, GlBridge gl, HttpContext ctx, ClaimsPrincipal u) =>
        {
            if (NeedPost(ctx) is { } no) return no;
            var upTo = MonthStart(r.UpTo ?? Today);
            var actor = WriteEndpoints.Actor(u);
            var active = await db.Prepayments.Include(p => p.Releases).Where(p => !p.Cancelled).ToListAsync();
            var due = active.SelectMany(p => Enumerable.Range(1, p.Months)
                    .Where(n => p.StartDate.AddMonths(n - 1) <= upTo && p.Releases.All(x => x.MonthNo != n))
                    .Select(n => (P: p, No: n, Month: p.StartDate.AddMonths(n - 1))))
                .GroupBy(x => x.Month).OrderBy(g => g.Key).ToList();
            var vouchers = new List<string>();
            var count = 0;
            foreach (var month in due)
            {
                var date = MonthEnd(month.Key);
                var lines = new List<VoucherLineInput>();
                foreach (var (p, n, _) in month)
                {
                    var amt = p.MonthAmount(n);
                    lines.Add(new(p.ExpenseAccountId, p.CostCenterId, $"{p.PrepaymentNo} {p.Description} — month {n}/{p.Months}", amt, 0));
                    lines.Add(new(p.PrepaidAccountId, null, $"{p.PrepaymentNo} released — month {n}/{p.Months}", 0, amt));
                }
                var v = await gl.PostAsync(date, $"Prepayment release — {month.Key:MMM yyyy} ({month.Count()} item(s))", "PREPAY", actor, lines);
                foreach (var (p, n, _) in month)
                    db.PrepaymentReleases.Add(new PrepaymentRelease { PrepaymentId = p.Id, MonthNo = n, Date = date, Amount = p.MonthAmount(n), VoucherNo = v.VoucherNo });
                await gl.SaveOrReverseAsync(db, v, actor);
                vouchers.Add(v.VoucherNo);
                count += month.Count();
            }
            return Results.Ok(new { vouchers, released = count, message = count == 0 ? $"Nothing due up to {upTo:MMM yyyy}." : null });
        });
    }
}
