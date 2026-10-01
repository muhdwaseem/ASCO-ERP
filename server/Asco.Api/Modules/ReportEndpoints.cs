using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Asco.Api.Modules;

/// <summary>
/// Extra reports for the Reports ▾ menu (Zoho-style catalogue) that C-ERP has no read model for.
/// Every figure comes from C-ERP's posted documents / ledger; nothing here writes.
/// Ranges default to the calendar year to date, like the other /reports endpoints.
/// </summary>
internal static class ReportEndpoints
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
    private static (DateOnly From, DateOnly To) Range(DateOnly? from, DateOnly? to) => (from ?? new DateOnly(Today.Year, 1, 1), to ?? Today);
    private static decimal Rate(decimal r) => r > 1 ? r / 100m : r;
    private static decimal R2(decimal x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);

    public static void MapReportEndpoints(this RouteGroupBuilder api)
    {
        var reports = api.MapGroup("/reports");

        // ── Sales by item: posted invoice lines less posted credit-note lines ──────────────
        reports.MapGet("/sales-by-item", async (IDbContextFactory<AegisDbContext> dbf, DateOnly? from, DateOnly? to) =>
        {
            var (f, t) = Range(from, to);
            await using var db = await dbf.CreateDbContextAsync();
            var invs = await db.SalesInvoices.AsNoTracking().Include(i => i.Lines).ThenInclude(l => l.Item)
                .Where(i => i.Status == VoucherStatus.Posted && i.Date >= f && i.Date <= t).ToListAsync();
            var rows = invs.SelectMany(i => i.Lines.Select(l => (Inv: i.Id, l)))
                .GroupBy(x => x.l.Item != null ? $"{x.l.Item.Code} · {x.l.Item.Name}" : x.l.Description.Trim())
                .Select(g => new { item = g.Key, invoices = g.Select(x => x.Inv).Distinct().Count(), quantity = g.Sum(x => x.l.Quantity), net = g.Sum(x => x.l.Net), vat = g.Sum(x => x.l.Vat), gross = g.Sum(x => x.l.Gross) })
                .OrderByDescending(r => r.net).ToList();
            return new { from = f, to = t, rows };
        });

        // ── Purchases by item / description: posted bill lines + direct expense lines ──────
        reports.MapGet("/purchases-by-item", async (IDbContextFactory<AegisDbContext> dbf, DateOnly? from, DateOnly? to) =>
        {
            var (f, t) = Range(from, to);
            await using var db = await dbf.CreateDbContextAsync();
            var bills = await db.PurchaseInvoices.AsNoTracking().Include(b => b.Lines).ThenInclude(l => l.ExpenseAccount)
                .Where(b => b.Status == VoucherStatus.Posted && b.Date >= f && b.Date <= t).ToListAsync();
            var exps = await db.DirectExpenses.AsNoTracking().Include(e => e.Lines).ThenInclude(l => l.ExpenseAccount)
                .Where(e => e.Status == VoucherStatus.Posted && e.Date >= f && e.Date <= t).ToListAsync();
            var lines = bills.SelectMany(b => b.Lines.Select(l => (Item: l.Description.Trim(), Acct: $"{l.ExpenseAccount.Code} · {l.ExpenseAccount.Name}", l.Quantity, l.Net, l.Vat, Src: "Bill")))
                .Concat(exps.SelectMany(e => e.Lines.Select(l => { var s = l.Split(e.AmountsIncludeVat); return (Item: (l.Description ?? "Expense").Trim(), Acct: $"{l.ExpenseAccount.Code} · {l.ExpenseAccount.Name}", Quantity: 1m, s.Net, s.Vat, Src: "Expense"); })));
            var rows = lines.GroupBy(x => (x.Item, x.Acct))
                .Select(g => new { item = g.Key.Item, account = g.Key.Acct, documents = g.Count(), quantity = g.Sum(x => x.Quantity), net = g.Sum(x => x.Net), vat = g.Sum(x => x.Vat), gross = g.Sum(x => x.Net + x.Vat) })
                .OrderByDescending(r => r.net).ToList();
            return new { from = f, to = t, rows };
        });

        // ── UAE VAT return summary (VAT 201 layout) from posted documents ─────────────────
        reports.MapGet("/vat-return", async (IDbContextFactory<AegisDbContext> dbf, DateOnly? from, DateOnly? to) =>
        {
            var (f, t) = Range(from, to);
            await using var db = await dbf.CreateDbContextAsync();
            var sales = (await db.SalesInvoices.AsNoTracking().Include(i => i.Lines).Where(i => i.Status == VoucherStatus.Posted && i.Date >= f && i.Date <= t).ToListAsync()).SelectMany(i => i.Lines).ToList();
            var cns = (await db.CreditNotes.AsNoTracking().Include(c => c.Lines).Where(c => c.Status == VoucherStatus.Posted && c.Date >= f && c.Date <= t).ToListAsync()).SelectMany(c => c.Lines).ToList();
            var bills = (await db.PurchaseInvoices.AsNoTracking().Include(b => b.Lines).Where(b => b.Status == VoucherStatus.Posted && b.Date >= f && b.Date <= t).ToListAsync()).SelectMany(b => b.Lines).ToList();
            var dns = (await db.DebitNotes.AsNoTracking().Include(d => d.Lines).Where(d => d.Status == VoucherStatus.Posted && d.Date >= f && d.Date <= t).ToListAsync()).SelectMany(d => d.Lines).ToList();
            var exps = (await db.DirectExpenses.AsNoTracking().Include(e => e.Lines).Where(e => e.Status == VoucherStatus.Posted && e.Date >= f && e.Date <= t).ToListAsync())
                .SelectMany(e => e.Lines.Select(l => (Rate: Rate(l.VatRate), Split: l.Split(e.AmountsIncludeVat)))).ToList();

            var stdSales = sales.Where(l => Rate(l.VatRate) > 0).Sum(l => l.TaxableNet) - cns.Where(l => Rate(l.VatRate) > 0).Sum(l => l.Net);
            var outputVat = sales.Sum(l => l.Vat) - cns.Sum(l => l.Vat);
            var zeroSales = sales.Where(l => Rate(l.VatRate) == 0).Sum(l => l.TaxableNet) - cns.Where(l => Rate(l.VatRate) == 0).Sum(l => l.Net);
            var outOfScope = sales.Sum(l => l.Net - l.TaxableNet); // govt fees / bank charges passed through
            var stdExp = bills.Where(l => Rate(l.VatRate) > 0).Sum(l => l.Net - l.NonTaxableTotal) + exps.Where(x => x.Rate > 0).Sum(x => x.Split.Net) - dns.Where(l => Rate(l.VatRate) > 0).Sum(l => l.Net);
            var inputVat = bills.Sum(l => l.Vat) + exps.Sum(x => x.Split.Vat) - dns.Sum(l => l.Vat);
            var otherExp = bills.Where(l => Rate(l.VatRate) == 0).Sum(l => l.Net - l.NonTaxableTotal) + bills.Sum(l => l.NonTaxableTotal) + exps.Where(x => x.Rate == 0).Sum(x => x.Split.Net);
            object Row(string box, string name, decimal amount, decimal? vat, string? style = null) => new { box, name, amount = R2(amount), vat = vat is decimal v ? R2(v) : (decimal?)null, style };
            return new
            {
                from = f, to = t,
                rows = new[]
                {
                    Row("", "VAT on sales and all other outputs", 0, null, "group"),
                    Row("1", "Standard rated supplies (5%), net of credit notes", stdSales, outputVat),
                    Row("4", "Zero-rated supplies", zeroSales, 0),
                    Row("—", "Out of scope (government fees, bank charges passed through)", outOfScope, null),
                    Row("8", "Totals — outputs", stdSales + zeroSales, outputVat, "total"),
                    Row("", "VAT on expenses and all other inputs", 0, null, "group"),
                    Row("9", "Standard rated expenses, net of debit notes", stdExp, inputVat),
                    Row("—", "Zero-rated / exempt / non-taxable expenses", otherExp, null),
                    Row("11", "Totals — inputs", stdExp, inputVat, "total"),
                    Row("", "Net VAT due", 0, null, "group"),
                    Row("12", "Total value of due tax for the period", 0, outputVat),
                    Row("13", "Total value of recoverable tax for the period", 0, inputVat),
                    Row("14", outputVat - inputVat >= 0 ? "Payable tax for the period" : "Refundable tax for the period", 0, outputVat - inputVat, "grand"),
                },
            };
        });

        // ── Business performance ratios ───────────────────────────────────────────────────
        reports.MapGet("/ratios", async (IDbContextFactory<AegisDbContext> dbf, LedgerService ledger, DateOnly? from, DateOnly? to) =>
        {
            var (f, t) = Range(from, to);
            var pl = await ledger.GetProfitAndLossAsync(f, t);
            var bs = await ledger.GetBalanceSheetAsync(t);
            await using var db = await dbf.CreateDbContextAsync();
            var cats = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => (a.Code, Category: a.Category ?? ""));
            bool Is(int id, string word) => cats.TryGetValue(id, out var c) && c.Category.Contains(word, StringComparison.OrdinalIgnoreCase);
            var currentAssets = bs.Assets.Where(a => Is(a.AccountId, "current") || Is(a.AccountId, "cash") || Is(a.AccountId, "receivable") || Is(a.AccountId, "inventor")).Sum(a => a.Amount);
            var inventory = bs.Assets.Where(a => Is(a.AccountId, "inventor")).Sum(a => a.Amount);
            var cash = bs.Assets.Where(a => Is(a.AccountId, "cash")).Sum(a => a.Amount);
            var receivables = bs.Assets.Where(a => cats[a.AccountId].Code == WellKnownAccounts.AccountsReceivable || Is(a.AccountId, "receivable")).Sum(a => a.Amount);
            var currentLiab = bs.Liabilities.Where(l => !Is(l.AccountId, "non-current")).Sum(l => l.Amount);
            var payables = bs.Liabilities.Where(l => cats[l.AccountId].Code == WellKnownAccounts.AccountsPayable || Is(l.AccountId, "payable")).Sum(l => l.Amount);
            var revenue = pl.OperatingIncomePeriod;
            var costs = pl.CostOfGoodsSoldPeriod + pl.OperatingExpensePeriod;
            var days = Math.Max(1, t.DayNumber - f.DayNumber + 1);
            decimal? Pct(decimal a, decimal b) => b == 0 ? null : R2(a / b * 100);
            decimal? X(decimal a, decimal b) => b == 0 ? null : Math.Round(a / b, 2);
            decimal? Days(decimal a, decimal b) => b == 0 ? null : Math.Round(a / b * days, 0);
            object Row(string group, string name, decimal? value, string unit, string note) => new { group, name, value, unit, note };
            return new
            {
                from = f, to = t,
                rows = new[]
                {
                    Row("Profitability", "Revenue", R2(revenue), "AED", "Operating income for the period"),
                    Row("Profitability", "Gross profit margin", Pct(pl.GrossProfitPeriod, revenue), "%", "Gross profit ÷ revenue"),
                    Row("Profitability", "Operating profit margin", Pct(pl.OperatingProfitPeriod, revenue), "%", "Operating profit ÷ revenue"),
                    Row("Profitability", "Net profit margin", Pct(pl.NetProfitPeriod, revenue), "%", "Net profit ÷ revenue"),
                    Row("Profitability", "Return on equity", Pct(pl.NetProfitPeriod, bs.TotalEquity), "%", "Net profit ÷ total equity"),
                    Row("Profitability", "Return on assets", Pct(pl.NetProfitPeriod, bs.TotalAssets), "%", "Net profit ÷ total assets"),
                    Row("Liquidity", "Current ratio", X(currentAssets, currentLiab), "x", "Current assets ÷ current liabilities (healthy ≥ 1.5)"),
                    Row("Liquidity", "Quick ratio", X(currentAssets - inventory, currentLiab), "x", "(Current assets − inventory) ÷ current liabilities"),
                    Row("Liquidity", "Cash ratio", X(cash, currentLiab), "x", "Cash & bank ÷ current liabilities"),
                    Row("Liquidity", "Working capital", R2(currentAssets - currentLiab), "AED", "Current assets − current liabilities"),
                    Row("Efficiency", "Debtor days", Days(receivables, revenue), "days", "How long customers take to pay"),
                    Row("Efficiency", "Creditor days", Days(payables, costs), "days", "How long you take to pay suppliers"),
                    Row("Efficiency", "Expense ratio", Pct(pl.OperatingExpensePeriod, revenue), "%", "Operating expenses ÷ revenue"),
                    Row("Solvency", "Debt to equity", X(bs.TotalLiabilities, bs.TotalEquity), "x", "Total liabilities ÷ total equity"),
                    Row("Solvency", "Equity ratio", Pct(bs.TotalEquity, bs.TotalAssets), "%", "Total equity ÷ total assets"),
                },
            };
        });

        // ── Movement of equity ─────────────────────────────────────────────────────────────
        reports.MapGet("/equity-movement", async (IDbContextFactory<AegisDbContext> dbf, DateOnly? from, DateOnly? to) =>
        {
            var (f, t) = Range(from, to);
            await using var db = await dbf.CreateDbContextAsync();
            var lines = await db.JournalLines.AsNoTracking()
                .Where(l => l.JournalVoucher.Status == VoucherStatus.Posted && l.JournalVoucher.Date <= t
                            && (l.Account.Type == AccountType.Equity || l.Account.Type == AccountType.Income || l.Account.Type == AccountType.Expense))
                .Select(l => new { l.AccountId, l.Account.Code, l.Account.Name, l.Account.Type, l.JournalVoucher.Date, l.Debit, l.Credit }).ToListAsync();
            var equity = lines.Where(l => l.Type == AccountType.Equity).GroupBy(l => l.AccountId).Select(g =>
            {
                var open = g.Where(x => x.Date < f).Sum(x => x.Credit - x.Debit);
                var inc = g.Where(x => x.Date >= f).Sum(x => x.Credit);
                var dec = g.Where(x => x.Date >= f).Sum(x => x.Debit);
                return new { code = g.First().Code, name = g.First().Name, opening = open, increase = inc, decrease = dec, closing = open + inc - dec };
            }).OrderBy(r => r.code, StringComparer.Ordinal).ToList();
            decimal Profit(bool before) => lines.Where(l => l.Type != AccountType.Equity && (before ? l.Date < f : l.Date >= f)).Sum(l => l.Credit - l.Debit);
            var earlier = Profit(true);
            var period = Profit(false);
            return new
            {
                from = f, to = t, equity,
                earnings = new { opening = earlier, profit = period, closing = earlier + period },
                totalOpening = equity.Sum(e => e.opening) + earlier,
                totalClosing = equity.Sum(e => e.closing) + earlier + period,
            };
        });

        // ── Horizontal (monthly) P&L for a year ────────────────────────────────────────────
        reports.MapGet("/monthly-pnl", async (IDbContextFactory<AegisDbContext> dbf, int? year) =>
        {
            var y = year ?? Today.Year;
            var f = new DateOnly(y, 1, 1);
            var t = new DateOnly(y, 12, 31);
            await using var db = await dbf.CreateDbContextAsync();
            var lines = await db.JournalLines.AsNoTracking()
                .Where(l => l.JournalVoucher.Status == VoucherStatus.Posted && l.JournalVoucher.Date >= f && l.JournalVoucher.Date <= t
                            && (l.Account.Type == AccountType.Income || l.Account.Type == AccountType.Expense))
                .Select(l => new { l.AccountId, l.Account.Code, l.Account.Name, l.Account.Type, l.JournalVoucher.Date, l.Debit, l.Credit }).ToListAsync();
            object Rows(AccountType type) => lines.Where(l => l.Type == type).GroupBy(l => l.AccountId)
                .Select(g => new
                {
                    code = g.First().Code, name = g.First().Name,
                    months = Enumerable.Range(1, 12).Select(m => g.Where(x => x.Date.Month == m).Sum(x => type == AccountType.Income ? x.Credit - x.Debit : x.Debit - x.Credit)).ToArray(),
                })
                .OrderBy(r => r.code, StringComparer.Ordinal).ToList();
            return new { year = y, income = Rows(AccountType.Income), expense = Rows(AccountType.Expense) };
        });

        // ── Bank book: every bank/cash account's ledger for the range ──────────────────────
        reports.MapGet("/bank-book", async (LedgerService ledger, DateOnly? from, DateOnly? to) =>
        {
            var (f, t) = Range(from, to);
            var banks = await ledger.GetCashBalancesAsync();
            var books = new List<object>();
            foreach (var b in banks)
            {
                var gl = await ledger.GetGeneralLedgerAsync(f, t, b.AccountId);
                books.Add(new { b.Code, b.Name, gl.Opening, gl.TotalDebit, gl.TotalCredit, gl.Closing, rows = gl.Rows.Select(r => new { r.Date, r.VoucherNo, r.Type, r.Narration, r.CostCenter, r.Debit, r.Credit, r.RunningBalance }) });
            }
            return new { from = f, to = t, books };
        });
    }
}
