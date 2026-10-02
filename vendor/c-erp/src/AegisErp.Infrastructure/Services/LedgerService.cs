using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>
/// Read-side queries over posted vouchers: account ledger, trial balance and dashboard KPIs.
/// Decimal aggregates are done in memory after materialising, which keeps the same code
/// working on SQLite (no native decimal) and PostgreSQL alike.
/// </summary>
public class LedgerService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public LedgerService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    public async Task<List<FiscalPeriod>> GetPeriodsAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.FiscalPeriods.AsNoTracking()
            .OrderBy(p => p.StartDate).ToListAsync();
    }

    /// <summary>The period containing the given date, falling back to the latest period.</summary>
    public async Task<FiscalPeriod?> GetDefaultPeriodAsync(DateOnly date)
    {
        var periods = await GetPeriodsAsync();
        return periods.FirstOrDefault(p => date >= p.StartDate && date <= p.EndDate)
               ?? periods.LastOrDefault();
    }

    private sealed record PostedLine(DateOnly Date, string VoucherNo, string Type, string? Narration,
        string? CostCenter, int? CostCenterId, string PostedBy, int AccountId, decimal Debit, decimal Credit);

    private static async Task<List<PostedLine>> PostedLinesAsync(AegisDbContext db) =>
        await db.JournalLines.AsNoTracking()
            .Where(l => l.JournalVoucher.Status == VoucherStatus.Posted)
            .Select(l => new PostedLine(
                l.JournalVoucher.Date, l.JournalVoucher.VoucherNo, l.JournalVoucher.Type.ToString(),
                l.JournalVoucher.Narration, l.CostCenter != null ? l.CostCenter.Code : null, l.CostCenterId,
                l.JournalVoucher.CreatedBy,
                l.AccountId, l.Debit, l.Credit))
            .ToListAsync();

    /// <summary>
    /// All posted entries across every account for a period (the "General Ledger" list view),
    /// optionally narrowed to one account, one cost center and/or one voucher type. Each row's
    /// running balance is that row's own account's net position after the entry — mixing rows
    /// from different accounts does not make the balance column meaningless, since it is always
    /// scoped to the account the row belongs to.
    /// </summary>
    public async Task<GeneralLedgerView> GetGeneralLedgerAsync(
        DateOnly fromDate, DateOnly toDate, int? accountId = null, int? costCenterId = null, VoucherType? type = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var accounts = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id);

        var lines = await PostedLinesAsync(db);
        if (accountId is int accId) lines = lines.Where(l => l.AccountId == accId).ToList();
        if (costCenterId is int ccId) lines = lines.Where(l => l.CostCenterId == ccId).ToList();
        if (type is VoucherType t) lines = lines.Where(l => l.Type == t.ToString()).ToList();

        // Opening = each account's net position (debit-positive) before the range starts.
        var running = lines
            .Where(l => l.Date < fromDate)
            .GroupBy(l => l.AccountId)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));
        var opening = running.Values.Sum();

        var inRange = lines
            .Where(l => l.Date >= fromDate && l.Date <= toDate)
            .OrderBy(l => l.Date).ThenBy(l => l.VoucherNo)
            .ToList();

        var rows = new List<GeneralLedgerRow>();
        decimal totDr = 0, totCr = 0;
        foreach (var l in inRange)
        {
            running.TryGetValue(l.AccountId, out var bal);
            bal += l.Debit - l.Credit;
            running[l.AccountId] = bal;
            totDr += l.Debit;
            totCr += l.Credit;
            var acc = accounts[l.AccountId];
            rows.Add(new GeneralLedgerRow(l.Date, l.VoucherNo, l.Type, l.Narration ?? "", acc.Id,
                acc.Code, acc.Name, l.CostCenter ?? "", l.PostedBy, l.Debit, l.Credit, bal));
        }

        var rangeLabel = $"{fromDate:dd MMM yyyy} – {toDate:dd MMM yyyy}";
        return new GeneralLedgerView(rangeLabel, opening, totDr, totCr, opening + totDr - totCr, rows);
    }

    public async Task<AccountLedger> GetAccountLedgerAsync(int accountId, int periodId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var account = await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == accountId);
        var period = await db.FiscalPeriods.AsNoTracking().FirstAsync(p => p.Id == periodId);
        var sign = account.NormalBalance == NormalBalance.Debit ? 1m : -1m;

        var lines = await PostedLinesAsync(db);
        var forAccount = lines.Where(l => l.AccountId == accountId).ToList();

        // Opening = net position (in the account's normal direction) before the period starts.
        var opening = sign * forAccount.Where(l => l.Date < period.StartDate).Sum(l => l.Debit - l.Credit);

        var inPeriod = forAccount
            .Where(l => l.Date >= period.StartDate && l.Date <= period.EndDate)
            .OrderBy(l => l.Date).ThenBy(l => l.VoucherNo)
            .ToList();

        var rows = new List<LedgerRow>();
        var running = opening;
        decimal totDr = 0, totCr = 0;
        foreach (var l in inPeriod)
        {
            running += sign * (l.Debit - l.Credit);
            totDr += l.Debit;
            totCr += l.Credit;
            rows.Add(new LedgerRow(l.Date, l.VoucherNo, l.Type, l.Narration ?? "", l.CostCenter ?? "",
                l.Debit, l.Credit, running));
        }

        return new AccountLedger(account.Code, account.Name, period.Name, account.NormalBalance,
            opening, totDr, totCr, running, rows);
    }

    /// <summary>
    /// Net balance of a single account (in its own normal-balance direction) as of a date, found by
    /// exact code match first, then exact name, then a name substring — case-insensitive throughout.
    /// Returns null when nothing matches. Backs the "Ask AI" account_balance intent; nothing else in
    /// the app currently needs a single account's balance without a full trial balance.
    /// </summary>
    public async Task<(string Code, string Name, decimal Balance)?> GetAccountBalanceAsync(string codeOrName, DateOnly asOfDate)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.IsPostable).ToListAsync();

        // Checked both directions: "Office Rent" for a question phrased as "the office rent
        // account" needs the shorter account name found inside the longer search phrase, while
        // "bank" matching "Mashreq Bank Account" needs the reverse. Whichever direction it takes,
        // this is still plain string matching in C# — never something the model resolves itself.
        var account = accounts.FirstOrDefault(a => a.Code.Equals(codeOrName, StringComparison.OrdinalIgnoreCase))
            ?? accounts.FirstOrDefault(a => a.Name.Equals(codeOrName, StringComparison.OrdinalIgnoreCase))
            ?? accounts.FirstOrDefault(a => a.Name.Contains(codeOrName, StringComparison.OrdinalIgnoreCase))
            ?? accounts.FirstOrDefault(a => codeOrName.Contains(a.Name, StringComparison.OrdinalIgnoreCase));
        if (account is null) return null;

        var lines = (await PostedLinesAsync(db)).Where(l => l.AccountId == account.Id && l.Date <= asOfDate);
        var sign = account.NormalBalance == NormalBalance.Debit ? 1m : -1m;
        var balance = sign * lines.Sum(l => l.Debit - l.Credit);
        return (account.Code, account.Name, balance);
    }

    public async Task<TrialBalance> GetTrialBalanceAsync(int periodId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var period = await db.FiscalPeriods.AsNoTracking().FirstAsync(p => p.Id == periodId);
        return await BuildTrialBalanceAsync(db, period.EndDate, period.Name);
    }

    /// <summary>
    /// Trial balance with movement: each account's Opening balance (net position before
    /// <paramref name="fromDate"/>), its total Debit/Credit activity within [fromDate, toDate], and
    /// the resulting Closing balance — the standard "From/To" trial balance layout.
    /// </summary>
    public async Task<TrialBalanceMovement> GetTrialBalanceAsync(DateOnly fromDate, DateOnly toDate)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var accounts = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id);
        var lines = await PostedLinesAsync(db);

        var opening = lines.Where(l => l.Date < fromDate)
            .GroupBy(l => l.AccountId)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit)); // signed, debit-positive

        var period = lines.Where(l => l.Date >= fromDate && l.Date <= toDate)
            .GroupBy(l => l.AccountId)
            .ToDictionary(g => g.Key, g => (Debit: g.Sum(l => l.Debit), Credit: g.Sum(l => l.Credit)));

        var rows = opening.Keys.Union(period.Keys)
            .Select(id =>
            {
                var acc = accounts[id];
                opening.TryGetValue(id, out var openNet);
                period.TryGetValue(id, out var p);
                var closeNet = openNet + p.Debit - p.Credit;
                return new TrialBalanceMovementRow(acc.Id, acc.Code, acc.Name, acc.Type,
                    openNet > 0 ? openNet : 0m, openNet < 0 ? -openNet : 0m,
                    p.Debit, p.Credit,
                    closeNet > 0 ? closeNet : 0m, closeNet < 0 ? -closeNet : 0m);
            })
            .Where(r => r.OpeningDebit != 0 || r.OpeningCredit != 0 || r.PeriodDebit != 0 || r.PeriodCredit != 0
                        || r.ClosingDebit != 0 || r.ClosingCredit != 0)
            .OrderBy(r => r.Code)
            .ToList();

        var label = $"{fromDate:dd MMM yyyy} – {toDate:dd MMM yyyy}";
        return new TrialBalanceMovement(label, rows,
            rows.Sum(r => r.OpeningDebit), rows.Sum(r => r.OpeningCredit),
            rows.Sum(r => r.PeriodDebit), rows.Sum(r => r.PeriodCredit),
            rows.Sum(r => r.ClosingDebit), rows.Sum(r => r.ClosingCredit));
    }

    private static async Task<TrialBalance> BuildTrialBalanceAsync(AegisDbContext db, DateOnly asOfDate, string label)
    {
        var accounts = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id);
        var lines = (await PostedLinesAsync(db)).Where(l => l.Date <= asOfDate).ToList();

        var rows = lines
            .GroupBy(l => l.AccountId)
            .Select(g =>
            {
                var acc = accounts[g.Key];
                var net = g.Sum(l => l.Debit - l.Credit); // signed, debit-positive
                return new TrialBalanceRow(acc.Id, acc.Code, acc.Name,
                    net > 0 ? net : 0m, net < 0 ? -net : 0m);
            })
            .Where(r => r.Debit != 0 || r.Credit != 0)
            .OrderBy(r => r.Code)
            .ToList();

        return new TrialBalance(label, rows, rows.Sum(r => r.Debit), rows.Sum(r => r.Credit));
    }

    public async Task<DashboardKpis> GetDashboardAsync(int periodId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var period = await db.FiscalPeriods.AsNoTracking().FirstAsync(p => p.Id == periodId);
        var accounts = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id);
        var lines = await PostedLinesAsync(db);

        decimal SignedByType(AccountType t, bool inPeriodOnly)
        {
            var q = lines.Where(l => accounts[l.AccountId].Type == t);
            if (inPeriodOnly) q = q.Where(l => l.Date >= period.StartDate && l.Date <= period.EndDate);
            return q.Sum(l => l.Debit - l.Credit);
        }

        decimal BalanceOfCodePrefix(string prefix)
        {
            var ids = accounts.Values.Where(a => a.Code.StartsWith(prefix)).Select(a => a.Id).ToHashSet();
            return lines.Where(l => ids.Contains(l.AccountId) && l.Date <= period.EndDate)
                        .Sum(l => l.Debit - l.Credit);
        }

        var income = -SignedByType(AccountType.Income, true);   // income is credit-normal
        var expense = SignedByType(AccountType.Expense, true);
        var cash = BalanceOfCodePrefix("110");                  // cash & bank
        var receivables = BalanceOfCodePrefix("12010");
        var payables = -BalanceOfCodePrefix("21010");           // payable is credit-normal
        var drafts = await db.JournalVouchers.CountAsync(v => v.Status == VoucherStatus.Draft);

        return new DashboardKpis(period.Name, income, expense, income - expense, cash, receivables, payables, drafts);
    }

    /// <summary>Revenue and expense per fiscal period, for the dashboard chart.</summary>
    public async Task<PeriodSeries> GetRevenueExpenseSeriesAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var periods = await db.FiscalPeriods.AsNoTracking().OrderBy(p => p.StartDate).ToListAsync();
        var accounts = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id);
        var lines = await PostedLinesAsync(db);

        var labels = new List<string>();
        var revenue = new List<double>();
        var expense = new List<double>();
        foreach (var p in periods)
        {
            var inP = lines.Where(l => l.Date >= p.StartDate && l.Date <= p.EndDate).ToList();
            var rev = -inP.Where(l => accounts[l.AccountId].Type == AccountType.Income).Sum(l => l.Debit - l.Credit);
            var exp = inP.Where(l => accounts[l.AccountId].Type == AccountType.Expense).Sum(l => l.Debit - l.Credit);
            labels.Add(p.Name.Replace(" 2026", ""));
            revenue.Add((double)rev);
            expense.Add((double)exp);
        }
        return new PeriodSeries(labels.ToArray(), revenue.ToArray(), expense.ToArray());
    }

    /// <summary>Balances of the cash &amp; bank accounts (codes starting 110) as at the latest period end.</summary>
    public async Task<List<CashBalance>> GetCashBalancesAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var latest = await db.FiscalPeriods.AsNoTracking().OrderByDescending(p => p.StartDate).FirstOrDefaultAsync();
        var end = latest?.EndDate ?? DateOnly.MaxValue;
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.IsPostable && a.Code.StartsWith("110")).ToListAsync();
        var lines = (await PostedLinesAsync(db)).Where(l => l.Date <= end).ToList();

        return accounts
            .Select(a => new CashBalance(a.Id, a.Code, a.Name, lines.Where(l => l.AccountId == a.Id).Sum(l => l.Debit - l.Credit)))
            .OrderByDescending(c => c.Balance)
            .ToList();
    }

    /// <summary>Sectioned profit &amp; loss for an arbitrary date range (Operating/Non-Operating
    /// Income, Cost of Goods Sold, Operating Expense), with cumulative to-date (to the range's end
    /// date) alongside — the Date Range filter on the P&amp;L page.</summary>
    public async Task<ProfitAndLoss> GetProfitAndLossAsync(DateOnly fromDate, DateOnly toDate)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var accounts = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id);
        var lines = await PostedLinesAsync(db);
        var label = $"{fromDate:dd MMM yyyy} – {toDate:dd MMM yyyy}";

        // sign: income is credit-normal (flip), expense is debit-normal.
        List<PnlLine> Build(PnlSection section, decimal sign) =>
            accounts.Values.Where(a => EffectiveSection(a) == section)
                .Select(a =>
                {
                    var forAcct = lines.Where(l => l.AccountId == a.Id);
                    var period_ = sign * forAcct.Where(l => l.Date >= fromDate && l.Date <= toDate).Sum(l => l.Debit - l.Credit);
                    var ytd = sign * forAcct.Where(l => l.Date <= toDate).Sum(l => l.Debit - l.Credit);
                    return new PnlLine(a.Id, a.Code, a.Name, period_, ytd);
                })
                .Where(l => l.Period != 0 || l.Ytd != 0)
                .OrderBy(l => l.Code)
                .ToList();

        var operatingIncome = Build(PnlSection.OperatingIncome, -1m);
        var cogs = Build(PnlSection.CostOfGoodsSold, 1m);
        var operatingExpense = Build(PnlSection.OperatingExpense, 1m);
        var nonOperatingIncome = Build(PnlSection.NonOperatingIncome, -1m);
        var nonOperatingExpense = Build(PnlSection.NonOperatingExpense, 1m);

        return new ProfitAndLoss(label, operatingIncome, cogs, operatingExpense, nonOperatingIncome, nonOperatingExpense,
            operatingIncome.Sum(l => l.Period), operatingIncome.Sum(l => l.Ytd),
            cogs.Sum(l => l.Period), cogs.Sum(l => l.Ytd),
            operatingExpense.Sum(l => l.Period), operatingExpense.Sum(l => l.Ytd),
            nonOperatingIncome.Sum(l => l.Period), nonOperatingIncome.Sum(l => l.Ytd),
            nonOperatingExpense.Sum(l => l.Period), nonOperatingExpense.Sum(l => l.Ytd));
    }

    /// <summary>An account's P&amp;L section, falling back to the type-level default (Operating
    /// Income/Expense) for the rare Income/Expense account that predates this classification and
    /// hasn't been reclassified yet.</summary>
    private static PnlSection? EffectiveSection(Account a) => a.PnlSection ?? a.Type switch
    {
        AccountType.Income => PnlSection.OperatingIncome,
        AccountType.Expense => PnlSection.OperatingExpense,
        _ => null,
    };

    /// <summary>The balance sheet as of a fiscal period's end — a thin wrapper over the date-based
    /// overload below, kept for callers that only have a period in hand.</summary>
    public async Task<BalanceSheet> GetBalanceSheetAsync(int periodId, bool excludeZeroBalances = true)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var period = await db.FiscalPeriods.AsNoTracking().FirstAsync(p => p.Id == periodId);
        return await BuildBalanceSheetAsync(db, period.EndDate, period.Name, excludeZeroBalances);
    }

    /// <summary>
    /// The balance sheet as of an arbitrary date — the Date filter on the Balance Sheet page.
    /// Current-year earnings roll into equity so it balances. Zero-balance accounts are omitted by
    /// default (<paramref name="excludeZeroBalances"/> = true) — set it to false to list every
    /// postable account of each type regardless of balance, mirroring Tally's "Exclude Accounts
    /// with zero Closing Balance" report option.
    /// </summary>
    public async Task<BalanceSheet> GetBalanceSheetAsync(DateOnly asOfDate, bool excludeZeroBalances = true)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await BuildBalanceSheetAsync(db, asOfDate, $"As of {asOfDate:dd MMM yyyy}", excludeZeroBalances);
    }

    private async Task<BalanceSheet> BuildBalanceSheetAsync(AegisDbContext db, DateOnly asOfDate, string label, bool excludeZeroBalances)
    {
        var accounts = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id);
        var lines = (await PostedLinesAsync(db)).Where(l => l.Date <= asOfDate).ToList();

        List<BsLine> Build(AccountType type, decimal sign)
        {
            var q = accounts.Values.Where(a => a.Type == type)
                .Select(a => new BsLine(a.Id, a.Code, a.Name,
                    sign * lines.Where(l => l.AccountId == a.Id).Sum(l => l.Debit - l.Credit)));
            if (excludeZeroBalances) q = q.Where(l => l.Amount != 0);
            return q.OrderBy(l => l.Code).ToList();
        }

        var assets = Build(AccountType.Asset, 1m);           // debit-normal
        var liabilities = Build(AccountType.Liability, -1m); // credit-normal
        var equity = Build(AccountType.Equity, -1m);         // credit-normal

        // Current-year earnings = income − expenses to date; keeps assets = liabilities + equity.
        var income = -lines.Where(l => accounts[l.AccountId].Type == AccountType.Income).Sum(l => l.Debit - l.Credit);
        var expense = lines.Where(l => accounts[l.AccountId].Type == AccountType.Expense).Sum(l => l.Debit - l.Credit);

        return new BalanceSheet(label, assets, liabilities, equity, income - expense);
    }
}
