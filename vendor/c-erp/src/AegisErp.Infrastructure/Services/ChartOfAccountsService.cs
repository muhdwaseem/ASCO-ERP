using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Everything the "New Account" form collects.</summary>
public record NewAccountInput(
    string Code, string Name, AccountType Type, bool IsPostable,
    string? Category, string Currency, int? ParentId, string? Description, decimal OpeningBalance,
    PnlSection? PnlSection = null);

/// <summary>One parsed row from an imported chart-of-accounts CSV, before its parent code has been
/// resolved to an id (see <see cref="ChartOfAccountsService.ImportAsync"/>).</summary>
public record ImportAccountRow(
    string Code, string Name, AccountType Type, bool IsPostable,
    string? ParentCode, string? Category, PnlSection? PnlSection, string? Currency);

/// <summary>Outcome of importing one CSV row — surfaced per-row so a bad row doesn't block the rest.</summary>
public record ImportRowResult(int RowNumber, string Code, bool Success, string Message);

/// <summary>Outcome of one account in a <see cref="ChartOfAccountsService.DeleteManyAsync"/> batch.</summary>
public record BulkDeleteResult(int Id, string Code, bool Success, string Message);

/// <summary>One row of the bulk Opening Balances screen — an account and whatever opening balance
/// it currently carries (both zero if it doesn't have one yet).</summary>
public record OpeningBalanceRow(int AccountId, string Code, string Name, AccountType Type, decimal Debit, decimal Credit);

/// <summary>One account's opening balance as entered on that screen.</summary>
public record OpeningBalanceEntry(int AccountId, decimal Debit, decimal Credit);

public class ChartOfAccountsService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;
    public ChartOfAccountsService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    /// <summary>
    /// Every posting flow (Sales/Purchase Invoice, Receipt, Vendor Payment, Credit/Debit Note) and
    /// opening-balance entry looks up a handful of control accounts by their exact well-known code
    /// (see <see cref="WellKnownAccounts"/> and the "31010" equity account <see cref="CreateAsync"/>
    /// uses) — a company with none of these can't post anything at all. The Receipt/Payment
    /// Voucher and Direct Expense "bank account" pickers have their own undocumented convention on
    /// top of that: they only offer Asset accounts whose code starts with "110" (see those dialogs'
    /// OnInitializedAsync) — with none, there is nothing to select and nothing to deposit/pay from.
    /// Item creation needs at least one postable Income and one postable Expense account to pick
    /// from, or it can't be saved at all.
    ///
    /// Rather than a flat list, every one of those control accounts is nested under the "Standard
    /// Chart of Accounts" header/group hierarchy the business adopted as its go-forward standard —
    /// new companies get that structure automatically instead of needing a manual CSV import.
    /// Existing companies' charts are untouched; this only runs once, at <see cref="CreateAsync"/>
    /// time, for a brand-new company.
    /// </summary>
    public static List<Account> BuildStarterAccounts()
    {
        var accounts = new List<Account>();

        Account Header(string code, string name, AccountType type, Account? parent = null)
        {
            var a = new Account { Code = code, Name = name, Type = type, IsPostable = false, Parent = parent };
            accounts.Add(a);
            return a;
        }

        Account Leaf(string code, string name, AccountType type, Account parent, string? category = null, PnlSection? pnl = null)
        {
            var a = new Account { Code = code, Name = name, Type = type, IsPostable = true, Parent = parent, Category = category, PnlSection = pnl };
            accounts.Add(a);
            return a;
        }

        // ── Header hierarchy — the Standard Chart of Accounts document's group structure ──
        var assets = Header("1000", "Assets", AccountType.Asset);
        var ppe = Header("1100", "Property Plant & Equipment", AccountType.Asset, assets);
        var accumDepHeader = Header("1150", "Accumulated Depreciation", AccountType.Asset, assets);
        var otherNonCurrent = Header("1180", "Other Non-Current Assets", AccountType.Asset, assets);
        var inventory = Header("1200", "Inventory", AccountType.Asset, assets);
        var tradeReceivables = Header("1210", "Trade Receivables", AccountType.Asset, assets);
        var prepayments = Header("1220", "Prepayments", AccountType.Asset, assets);
        var otherReceivables = Header("1230", "Other Receivables", AccountType.Asset, assets);
        var vatRecoverable = Header("1240", "VAT Recoverable", AccountType.Asset, assets);
        var dueFromRelated = Header("1250", "Due from Related Parties", AccountType.Asset, assets);
        var cashBank = Header("1260", "Cash & Bank", AccountType.Asset, assets);

        var liabilities = Header("2000", "Liabilities", AccountType.Liability);
        var longTermBorrowings = Header("2100", "Long-term Borrowings", AccountType.Liability, liabilities);
        var employeeProvisions = Header("2110", "Employee Provisions", AccountType.Liability, liabilities);
        var tradePayables = Header("2200", "Trade Payables", AccountType.Liability, liabilities);
        var accruedExpenses = Header("2210", "Accrued Expenses", AccountType.Liability, liabilities);
        var vatPayableHeader = Header("2220", "VAT Payable", AccountType.Liability, liabilities);
        var shortTermProvisions = Header("2230", "Short-term Provisions", AccountType.Liability, liabilities);
        var dueToRelated = Header("2240", "Due to Related Parties", AccountType.Liability, liabilities);
        var otherCurrentLiabilities = Header("2250", "Other Current Liabilities", AccountType.Liability, liabilities);

        var equity = Header("3000", "Equity", AccountType.Equity);
        var capital = Header("3100", "Capital", AccountType.Equity, equity);
        var reserves = Header("3200", "Reserves", AccountType.Equity, equity);
        var retainedEarnings = Header("3300", "Retained Earnings", AccountType.Equity, equity);
        var currentAccount = Header("3400", "Current Account", AccountType.Equity, equity);

        var income = Header("4000", "Income", AccountType.Income);
        var salesRevenue = Header("4100", "Sales / Service Revenue", AccountType.Income, income);
        var salesDeductions = Header("4150", "Sales Deductions", AccountType.Income, income);
        var otherIncome = Header("4200", "Other Income", AccountType.Income, income);

        var expenses = Header("5000", "Expenses", AccountType.Expense);
        var cogs = Header("5100", "Cost of Goods Sold", AccountType.Expense, expenses);
        var salariesBenefits = Header("5200", "Salaries & Benefits", AccountType.Expense, expenses);
        var adminExpenses = Header("5300", "Administrative Expenses", AccountType.Expense, expenses);
        var sellingDistribution = Header("5400", "Selling & Distribution", AccountType.Expense, expenses);
        var financeCosts = Header("5500", "Finance Costs", AccountType.Expense, expenses);
        var depreciationAmort = Header("5600", "Depreciation & Amortisation", AccountType.Expense, expenses);
        var otherExpenses = Header("5700", "Other Expenses", AccountType.Expense, expenses);

        // ── Postable control accounts, nested under the matching header ──
        Leaf("11020", "Bank Account", AccountType.Asset, cashBank, "Cash and cash equivalents");
        Leaf(WellKnownAccounts.AccountsReceivable, "Accounts Receivable", AccountType.Asset, tradeReceivables, "Accounts receivable");
        Leaf(WellKnownAccounts.VatInput, "VAT Input / Prepaid Expenses", AccountType.Asset, vatRecoverable, "Current asset");
        Leaf(WellKnownAccounts.ReverseChargeVatRecoverable, "Reverse Charge VAT Recoverable", AccountType.Asset, vatRecoverable, "Current asset");
        Leaf(WellKnownAccounts.AccumulatedDepreciation, "Accumulated Depreciation", AccountType.Asset, accumDepHeader, "Accumulated depreciation");
        Leaf(WellKnownAccounts.EmployeeAdvancesReceivable, "Employee Advances / Loans Receivable", AccountType.Asset, otherReceivables, "Other receivables");

        Leaf(WellKnownAccounts.AccountsPayable, "Accounts Payable", AccountType.Liability, tradePayables, "Accounts payable");
        Leaf(WellKnownAccounts.SalariesPayable, "Salaries Payable", AccountType.Liability, accruedExpenses, "Accrued expense");
        Leaf(WellKnownAccounts.GratuityPayable, "Gratuity Payable", AccountType.Liability, employeeProvisions, "Employee provisions");
        Leaf(WellKnownAccounts.ExpensesPayable, "Expenses Payable", AccountType.Liability, accruedExpenses, "Accrued expense");
        Leaf(WellKnownAccounts.VatPayable, "VAT Payable", AccountType.Liability, vatPayableHeader, "Current liability");
        Leaf(WellKnownAccounts.VatControl, "VAT Control", AccountType.Liability, vatPayableHeader, "Current liability");
        Leaf(WellKnownAccounts.ReverseChargeVatPayable, "Reverse Charge VAT Payable", AccountType.Liability, vatPayableHeader, "Current liability");
        Leaf(WellKnownAccounts.DeferredRevenue, "Deferred Revenue", AccountType.Liability, otherCurrentLiabilities, "Current liability");

        Leaf("31010", "Share Capital & Retained Earnings", AccountType.Equity, retainedEarnings, "Equity");

        // Generic starter Income/Expense accounts — without at least one of each, Item creation
        // can't be saved at all on a brand-new company.
        Leaf("41010", "Service Revenue", AccountType.Income, salesRevenue, "Revenue", PnlSection.OperatingIncome);
        Leaf("53010", "General & Administrative Expenses", AccountType.Expense, adminExpenses, "Admin expenses", PnlSection.OperatingExpense);

        // Classified as Cost of Goods Sold per the PRO-service Complete-dialog P&L review — these
        // two aren't posted to automatically (see WellKnownAccounts doc comments), but a new
        // company now has them ready and correctly classified instead of needing them added by hand.
        Leaf(WellKnownAccounts.GovtFeesExpense, "Government Fees Expense", AccountType.Expense, cogs, "Cost of sales", PnlSection.CostOfGoodsSold);
        Leaf(WellKnownAccounts.SubcontractedProServicesExpense, "Subcontracted PRO Services Expense", AccountType.Expense, cogs, "Cost of sales", PnlSection.CostOfGoodsSold);

        return accounts;
    }

    public async Task<List<Account>> GetAllAsync(bool postableOnly = false)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.Accounts.AsNoTracking().OrderBy(a => a.Code).AsQueryable();
        if (postableOnly) q = q.Where(a => a.IsPostable && a.IsActive);
        return await q.ToListAsync();
    }

    /// <summary>Header (non-postable) accounts, for the parent picker.</summary>
    public async Task<List<Account>> GetHeaderAccountsAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Accounts.AsNoTracking().Where(a => !a.IsPostable).OrderBy(a => a.Code).ToListAsync();
    }

    public async Task<List<CostCenter>> GetCostCentersAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CostCenters.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).ToListAsync();
    }

    /// <summary>Every cost centre including inactive ones — for the management page.</summary>
    public async Task<List<CostCenter>> GetAllCostCentersAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CostCenters.AsNoTracking().OrderBy(c => c.Code).ToListAsync();
    }

    public async Task<CostCenter> CreateCostCenterAsync(string code, string name)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        code = code.Trim();
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(code)) throw new PostingException("Cost centre code is required.");
        if (string.IsNullOrWhiteSpace(name)) throw new PostingException("Cost centre name is required.");

        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.CostCenters.AnyAsync(c => c.Code == code))
            throw new PostingException($"A cost centre with code {code} already exists.");

        var cc = new CostCenter { Code = code, Name = name, IsActive = true };
        db.CostCenters.Add(cc);
        await db.SaveChangesAsync();
        return cc;
    }

    public async Task UpdateCostCenterAsync(int id, string name)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new PostingException("Cost centre name is required.");

        await using var db = await _dbf.CreateDbContextAsync();
        var cc = await db.CostCenters.FirstOrDefaultAsync(c => c.Id == id)
            ?? throw new PostingException("Cost centre not found.");
        cc.Name = name;
        await db.SaveChangesAsync();
    }

    public async Task SetCostCenterActiveAsync(int id, bool isActive)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var cc = await db.CostCenters.FirstOrDefaultAsync(c => c.Id == id)
            ?? throw new PostingException("Cost centre not found.");
        cc.IsActive = isActive;
        await db.SaveChangesAsync();
    }

    public async Task DeleteCostCenterAsync(int id)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var cc = await db.CostCenters.FirstOrDefaultAsync(c => c.Id == id)
            ?? throw new PostingException("Cost centre not found.");
        if (await db.JournalLines.AnyAsync(l => l.CostCenterId == id))
            throw new PostingException("This cost centre has posted entries and cannot be deleted — deactivate it instead.");
        db.CostCenters.Remove(cc);
        await db.SaveChangesAsync();
    }

    public async Task<bool> CodeExistsAsync(string code)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Accounts.AnyAsync(a => a.Code == code);
    }

    /// <summary>Suggests the next free numeric code under a parent (max child + 10), or parent + 10 if it has none.</summary>
    /// <summary>
    /// Suggests the next posting-account code under a header, e.g. header "510" already has
    /// postable children "51001", "51002" — the next one offered is "51003". Children are numbered
    /// by appending a running suffix to the header's own code, so the suggestion is always the
    /// highest existing suffix plus one (not a jump — the very next number a user expects).
    /// </summary>
    public async Task<string> SuggestCodeAsync(int? parentId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        if (parentId is not int pid)
            return "";
        var parent = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == pid);
        if (parent is null) return "";

        var childCodes = await db.Accounts.AsNoTracking()
            .Where(a => a.ParentId == pid).Select(a => a.Code).ToListAsync();

        const int defaultSuffixWidth = 2;
        var suffixWidth = defaultSuffixWidth;
        var maxSuffix = 0;
        foreach (var c in childCodes)
        {
            if (c.Length > parent.Code.Length && c.StartsWith(parent.Code) &&
                int.TryParse(c[parent.Code.Length..], out var suf))
            {
                suffixWidth = Math.Max(suffixWidth, c.Length - parent.Code.Length);
                if (suf > maxSuffix) maxSuffix = suf;
            }
        }
        return parent.Code + (maxSuffix + 1).ToString(new string('0', suffixWidth));
    }

    /// <summary>
    /// Creates an account and, if an opening balance is supplied for a postable account,
    /// posts a balanced opening voucher against the equity account (31010) in one transaction.
    /// </summary>
    public async Task<Account> CreateAsync(NewAccountInput input, string createdBy)
    {
        // Chart of Accounts gates New/Edit/Delete behind Session.CanPost in the UI (not
        // CanAdminister — Accountant can maintain the chart, same as posting documents), but
        // nothing here enforced it server-side.
        if (!_current.CanPost)
            throw new PostingException("You don't have permission to do this.");

        var code = input.Code.Trim();
        var name = input.Name.Trim();
        if (string.IsNullOrWhiteSpace(code)) throw new PostingException("Account number is required.");
        if (string.IsNullOrWhiteSpace(name)) throw new PostingException("Account name is required.");
        if (input.OpeningBalance != 0 && !input.IsPostable)
            throw new PostingException("A header account cannot carry an opening balance.");

        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.Accounts.AnyAsync(a => a.Code == code))
            throw new PostingException($"An account with number {code} already exists.");

        await using var tx = await db.Database.BeginTransactionAsync();

        var account = new Account
        {
            Code = code,
            Name = name,
            Type = input.Type,
            IsPostable = input.IsPostable,
            ParentId = input.ParentId,
            Category = string.IsNullOrWhiteSpace(input.Category) ? null : input.Category.Trim(),
            Currency = string.IsNullOrWhiteSpace(input.Currency) ? "AED" : input.Currency.Trim(),
            Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
            IsActive = true,
            PnlSection = input.PnlSection ?? DefaultPnlSection(input.Type),
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(); // assigns account.Id

        if (input.OpeningBalance != 0)
        {
            var equity = await db.Accounts.FirstOrDefaultAsync(a => a.Code == "31010")
                ?? throw new PostingException("Opening-balance equity account 31010 is missing.");
            // Dated at the very start of the books (earliest period), not the latest one — an
            // opening balance has to predate every reporting range it should show up in, or the
            // Trial Balance's "Opening Balance" column treats it as an in-period transaction instead.
            var period = await db.FiscalPeriods.OrderBy(p => p.StartDate).FirstOrDefaultAsync()
                ?? throw new PostingException("No fiscal period is defined for the opening entry.");

            var now = DateTime.UtcNow;
            var amount = Math.Abs(input.OpeningBalance);
            var onDebitSide = input.Type.NormalBalance() == NormalBalance.Debit;

            // Positive opening balance sits on the account's normal side; equity is the contra.
            var lines = new List<VoucherLineInput>
            {
                onDebitSide
                    ? new VoucherLineInput(account.Id, null, "Opening balance", amount, 0)
                    : new VoucherLineInput(account.Id, null, "Opening balance", 0, amount),
                onDebitSide
                    ? new VoucherLineInput(equity.Id, null, $"Opening balance — {code}", 0, amount)
                    : new VoucherLineInput(equity.Id, null, $"Opening balance — {code}", amount, 0),
            };

            await JournalPoster.PostAsync(db, _current.CanPost, VoucherType.Opening, explicitNo: null,
                period.StartDate, period.Id, $"Opening balance — {name}", code, createdBy, lines, now);
        }

        await JournalPoster.SaveAndCommitAsync(db, tx);
        return account;
    }

    /// <summary>
    /// Every postable account (except the opening-balance equity account itself, which only ever
    /// absorbs whatever the others need to balance) alongside whatever opening balance it currently
    /// carries — for the bulk Opening Balances screen. A small firm switching from another system
    /// can key in each account's Debit or Credit balance here in one sitting, the way they'd read it
    /// off their old trial balance, instead of re-creating every account just to set one field.
    /// </summary>
    public async Task<List<OpeningBalanceRow>> GetOpeningBalancesAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.IsPostable && a.Code != "31010")
            .OrderBy(a => a.Code)
            .ToListAsync();

        // Grouped and summed client-side (not once the values reach SQL) rather than a straight
        // dictionary lookup, because the equity account (excluded above) carries one contra line
        // per account that has an opening balance — a plain ToDictionaryAsync on AccountId would
        // throw the moment a second one exists.
        var openingLines = await db.JournalLines.AsNoTracking()
            .Where(l => l.JournalVoucher.Type == VoucherType.Opening)
            .Select(l => new { l.AccountId, l.Debit, l.Credit })
            .ToListAsync();
        var openingByAccount = openingLines.GroupBy(l => l.AccountId)
            .ToDictionary(g => g.Key, g => (Debit: g.Sum(l => l.Debit), Credit: g.Sum(l => l.Credit)));

        return accounts.Select(a =>
        {
            openingByAccount.TryGetValue(a.Id, out var line);
            return new OpeningBalanceRow(a.Id, a.Code, a.Name, a.Type, line.Debit, line.Credit);
        }).ToList();
    }

    /// <summary>
    /// Saves opening balances for any number of accounts in one go. Each account's entry is
    /// upserted independently: a zero/zero entry clears a previously-set balance, a nonzero one
    /// creates or adjusts a two-line "Opening" voucher against the equity account (31010) — so
    /// re-saving after fixing a typo just corrects the existing entry rather than piling up a second
    /// one. Every entry is dated at the very start of the books (earliest fiscal period), so it
    /// shows as a true opening balance rather than an in-period transaction on every report.
    /// </summary>
    public async Task SetOpeningBalancesAsync(IEnumerable<OpeningBalanceEntry> entries, string updatedBy)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var equity = await db.Accounts.FirstOrDefaultAsync(a => a.Code == "31010")
            ?? throw new PostingException("Opening-balance equity account 31010 is missing.");
        var period = await db.FiscalPeriods.OrderBy(p => p.StartDate).FirstOrDefaultAsync()
            ?? throw new PostingException("No fiscal period is defined for the opening entry.");
        var now = DateTime.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync();

        foreach (var entry in entries)
        {
            if (entry.Debit < 0 || entry.Credit < 0)
                throw new PostingException("Opening balances cannot be negative.");
            if (entry.Debit != 0 && entry.Credit != 0)
                throw new PostingException("An account can't have both a debit and a credit opening balance.");
            if (entry.AccountId == equity.Id)
                continue; // the equity account absorbs the plug automatically; it has no row to edit

            var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == entry.AccountId)
                ?? throw new PostingException("Account not found.");

            var existing = await db.JournalVouchers.Include(v => v.Lines)
                .FirstOrDefaultAsync(v => v.Type == VoucherType.Opening && v.Reference == account.Code);

            if (entry.Debit == 0 && entry.Credit == 0)
            {
                if (existing is not null) db.JournalVouchers.Remove(existing);
                continue;
            }

            if (existing is not null)
            {
                existing.Lines.First(l => l.AccountId == account.Id).Debit = entry.Debit;
                existing.Lines.First(l => l.AccountId == account.Id).Credit = entry.Credit;
                existing.Lines.First(l => l.AccountId == equity.Id).Debit = entry.Credit;
                existing.Lines.First(l => l.AccountId == equity.Id).Credit = entry.Debit;
            }
            else
            {
                var lines = new List<VoucherLineInput>
                {
                    new(account.Id, null, "Opening balance", entry.Debit, entry.Credit),
                    new(equity.Id, null, $"Opening balance — {account.Code}", entry.Credit, entry.Debit),
                };
                await JournalPoster.PostAsync(db, _current.CanPost, VoucherType.Opening, explicitNo: null,
                    period.StartDate, period.Id, $"Opening balance — {account.Name}", account.Code, updatedBy, lines, now);
                // Voucher numbers are allocated by counting existing rows in the database, not the
                // change tracker — each one has to land before the next PostAsync call in this loop
                // computes its own number, or two accounts in the same batch collide on one number.
                await db.SaveChangesAsync();
            }
        }

        await JournalPoster.SaveAndCommitAsync(db, tx);
    }

    /// <summary>
    /// Updates the editable fields of an account. Code, type and posting-type are fixed once created.
    /// <paramref name="expectedRowVersion"/> must be the value the editor read the account with —
    /// if another user has saved a change since, this throws a recoverable <see cref="PostingException"/>
    /// instead of silently overwriting their edit.
    /// </summary>
    public async Task<Account> UpdateAsync(int id, string name, string? category, string currency,
        int? parentId, string? description, bool isActive, Guid expectedRowVersion, string updatedBy,
        PnlSection? pnlSection = null)
    {
        if (!_current.CanPost)
            throw new PostingException("You don't have permission to do this.");

        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new PostingException("Account name is required.");
        if (parentId == id) throw new PostingException("An account cannot be its own parent.");

        await using var db = await _dbf.CreateDbContextAsync();
        var acc = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id)
            ?? throw new PostingException("Account not found.");

        acc.Name = name;
        acc.Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        acc.Currency = string.IsNullOrWhiteSpace(currency) ? "AED" : currency.Trim();
        acc.ParentId = parentId;
        acc.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        acc.IsActive = isActive;
        acc.PnlSection = acc.Type is AccountType.Income or AccountType.Expense
            ? pnlSection ?? acc.PnlSection ?? DefaultPnlSection(acc.Type)
            : null;
        acc.UpdatedBy = updatedBy;
        acc.UpdatedAtUtc = DateTime.UtcNow;
        acc.RowVersion = Guid.NewGuid();
        // Check against the version the editor actually saw, not whatever's now freshly loaded above.
        db.Entry(acc).Property(a => a.RowVersion).OriginalValue = expectedRowVersion;
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return acc;
    }

    /// <summary>Quick activate/deactivate toggle that doesn't require re-submitting the whole edit
    /// form — mirrors the same pattern used for currencies and tags.</summary>
    public async Task SetActiveAsync(int id, bool isActive)
    {
        if (!_current.CanPost)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var acc = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id)
            ?? throw new PostingException("Account not found.");
        acc.IsActive = isActive;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Imports a batch of accounts (e.g. from a CSV) one at a time, so one bad row is reported
    /// without blocking the rest. A row's parent must already exist — either from before this
    /// import or from an earlier row in the same batch — so header/group rows should be listed
    /// before the accounts that live under them.
    /// </summary>
    public async Task<List<ImportRowResult>> ImportAsync(List<ImportAccountRow> rows, string createdBy)
    {
        // Checked upfront (CreateAsync below checks it too) so an unauthorized caller gets one
        // clean rejection instead of every row individually failing.
        if (!_current.CanPost)
            throw new PostingException("You don't have permission to do this.");

        var codeToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using (var db = await _dbf.CreateDbContextAsync())
            foreach (var a in await db.Accounts.AsNoTracking().ToListAsync())
                codeToId[a.Code] = a.Id;

        var results = new List<ImportRowResult>();
        var rowNo = 1; // row 1 is the header line in the source file
        foreach (var row in rows)
        {
            rowNo++;
            try
            {
                int? parentId = null;
                if (!string.IsNullOrWhiteSpace(row.ParentCode))
                {
                    if (!codeToId.TryGetValue(row.ParentCode, out var pid))
                        throw new PostingException(
                            $"Parent account '{row.ParentCode}' was not found — list it on an earlier row or create it first.");
                    parentId = pid;
                }

                var input = new NewAccountInput(row.Code, row.Name, row.Type, row.IsPostable,
                    row.Category, row.Currency ?? "AED", parentId, null, 0, row.PnlSection);
                var created = await CreateAsync(input, createdBy);
                codeToId[created.Code] = created.Id;
                results.Add(new ImportRowResult(rowNo, row.Code, true, "Created"));
            }
            catch (PostingException ex)
            {
                results.Add(new ImportRowResult(rowNo, row.Code, false, ex.Message));
            }
        }
        return results;
    }

    /// <summary>The sensible default P&amp;L section for a newly created account of this type — Cost
    /// of Goods Sold is never auto-assigned; the user picks it explicitly when it applies.</summary>
    private static PnlSection? DefaultPnlSection(AccountType type) => type switch
    {
        AccountType.Income => PnlSection.OperatingIncome,
        AccountType.Expense => PnlSection.OperatingExpense,
        _ => null,
    };

    /// <summary>
    /// Deletes an account, but only if nothing references it — sub-accounts, ledger entries,
    /// invoices or receipts. Otherwise it should be deactivated, not deleted.
    /// </summary>
    public async Task DeleteAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var acc = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id)
            ?? throw new PostingException("Account not found.");

        var reason = await DeleteBlockReasonAsync(db, id);
        if (reason is not null) throw new PostingException(reason);

        db.Accounts.Remove(acc);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Deletes as many of the given accounts as it safely can — e.g. clearing out an old chart of
    /// accounts before importing a new one. Each account gets the same checks as
    /// <see cref="DeleteAsync"/>, so anything still referenced is skipped (never force-deleted) and
    /// reported back with its reason rather than aborting the whole batch. Runs in passes so a
    /// header whose children are *also* selected this run is deleted right after they are, without
    /// the caller having to pre-sort the selection.
    /// </summary>
    public async Task<List<BulkDeleteResult>> DeleteManyAsync(IEnumerable<int> ids)
    {
        var idSet = ids.ToHashSet();
        await using var db = await _dbf.CreateDbContextAsync();
        var accounts = await db.Accounts.Where(a => idSet.Contains(a.Id)).ToListAsync();
        var pending = accounts.Select(a => a.Id).ToHashSet();
        var results = new Dictionary<int, BulkDeleteResult>();

        bool progressed;
        do
        {
            progressed = false;
            foreach (var id in pending.ToList())
            {
                var reason = await DeleteBlockReasonAsync(db, id, pending);
                if (reason is not null) continue;

                var acc = accounts.First(a => a.Id == id);
                db.Accounts.Remove(acc);
                await db.SaveChangesAsync();
                results[id] = new BulkDeleteResult(id, acc.Code, true, "Deleted.");
                pending.Remove(id);
                progressed = true;
            }
        } while (progressed && pending.Count > 0);

        foreach (var id in pending)
        {
            var acc = accounts.First(a => a.Id == id);
            var reason = await DeleteBlockReasonAsync(db, id, pending) ?? "Could not be deleted.";
            results[id] = new BulkDeleteResult(id, acc.Code, false, reason);
        }

        return accounts.Select(a => results[a.Id]).ToList();
    }

    /// <summary>Null if <paramref name="id"/> is safe to delete right now. <paramref name="alsoBeingDeleted"/>
    /// lets a batch delete ignore sub-accounts that are themselves queued for deletion this run,
    /// instead of treating them as a permanent blocker.</summary>
    private static async Task<string?> DeleteBlockReasonAsync(AegisDbContext db, int id, HashSet<int>? alsoBeingDeleted = null)
    {
        var hasOtherChildren = alsoBeingDeleted is null
            ? await db.Accounts.AnyAsync(a => a.ParentId == id)
            : await db.Accounts.AnyAsync(a => a.ParentId == id && !alsoBeingDeleted.Contains(a.Id));
        if (hasOtherChildren) return "This account has sub-accounts. Remove or reassign them first.";
        if (await db.JournalLines.AnyAsync(l => l.AccountId == id))
            return "This account has ledger entries and cannot be deleted — deactivate it instead.";

        // Every other place an account can be referenced before anything's ever posted against
        // it — a document line, a payment/receipt "paid from" account, a tax code's GL account, or
        // an item's default sales/purchase account. Checked explicitly (rather than relying on the
        // database's FK constraint to reject the delete) so the user gets a clear reason instead of
        // a raw "something went wrong" from an unhandled DbUpdateException.
        if (await db.SalesInvoiceLines.AnyAsync(l => l.RevenueAccountId == id))
            return "This account is used on sales invoices and cannot be deleted.";
        if (await db.CustomerReceipts.AnyAsync(r => r.BankAccountId == id))
            return "This account is used on receipts and cannot be deleted.";
        if (await db.RecurringInvoiceProfileLines.AnyAsync(l => l.RevenueAccountId == id))
            return "This account is used on a recurring invoice profile and cannot be deleted.";
        if (await db.CreditNotes.AnyAsync(n => n.BankAccountId == id))
            return "This account is used on a credit note and cannot be deleted.";
        if (await db.CreditNoteLines.AnyAsync(l => l.RevenueAccountId == id))
            return "This account is used on a credit note line and cannot be deleted.";
        if (await db.EstimateLines.AnyAsync(l => l.RevenueAccountId == id))
            return "This account is used on a quotation and cannot be deleted.";
        if (await db.PurchaseInvoiceLines.AnyAsync(l => l.ExpenseAccountId == id))
            return "This account is used on purchase invoices and cannot be deleted.";
        if (await db.VendorPayments.AnyAsync(p => p.BankAccountId == id))
            return "This account is used on vendor payments and cannot be deleted.";
        if (await db.DebitNoteLines.AnyAsync(l => l.ExpenseAccountId == id))
            return "This account is used on a debit note line and cannot be deleted.";
        if (await db.DirectExpenseLines.AnyAsync(l => l.ExpenseAccountId == id))
            return "This account is used on a direct expense and cannot be deleted.";
        if (await db.DirectExpensePayments.AnyAsync(p => p.BankAccountId == id))
            return "This account is used on a direct expense payment and cannot be deleted.";
        if (await db.TaxCodes.AnyAsync(t => t.GlAccountId == id))
            return "This account is used by a tax code and cannot be deleted.";
        if (await db.Items.AnyAsync(i => i.SalesAccountId == id || i.PurchaseAccountId == id))
            return "This account is used as an item's default sales or purchase account and cannot be deleted.";
        if (await db.ServiceKitLines.AnyAsync(l => l.RevenueAccountId == id))
            return "This account is used by a service kit and cannot be deleted.";
        if (await db.FixedAssets.AnyAsync(a => a.AssetAccountId == id || a.DepreciationExpenseAccountId == id))
            return "This account is used on a fixed asset and cannot be deleted.";
        if (await db.Employees.AnyAsync(m => m.EmployeeExpenseAccountId == id))
            return "This account is used as an employee's salary expense account and cannot be deleted.";
        if (await db.PayrollRuns.AnyAsync(r => r.PaidFromBankAccountId == id))
            return "This account is used on a payroll run and cannot be deleted.";
        if (await db.PayrollRunLines.AnyAsync(l => l.ExpenseAccountId == id))
            return "This account is used on a payroll run line and cannot be deleted.";
        if (await db.GratuityPayments.AnyAsync(g => g.ExpenseAccountId == id || g.PaidFromBankAccountId == id))
            return "This account is used on a gratuity payment and cannot be deleted.";
        return null;
    }
}
