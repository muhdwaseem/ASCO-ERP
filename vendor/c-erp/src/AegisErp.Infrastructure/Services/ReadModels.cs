using AegisErp.Domain;

namespace AegisErp.Infrastructure.Services;

/// <summary>One row in a general-ledger account view, with the running balance after the entry.</summary>
public record LedgerRow(
    DateOnly Date,
    string VoucherNo,
    string Type,
    string Narration,
    string CostCenter,
    decimal Debit,
    decimal Credit,
    decimal RunningBalance);

public record AccountLedger(
    string AccountCode,
    string AccountName,
    string Period,
    NormalBalance NormalBalance,
    decimal Opening,
    decimal TotalDebit,
    decimal TotalCredit,
    decimal Closing,
    IReadOnlyList<LedgerRow> Rows);

/// <summary>
/// One row in the all-accounts general ledger view. Unlike <see cref="LedgerRow"/>, this can mix
/// entries from different accounts, so it carries its own account identity and running balance
/// (that account's own net position after this entry — not a combined total across accounts).
/// </summary>
public record GeneralLedgerRow(
    DateOnly Date,
    string VoucherNo,
    string Type,
    string Narration,
    int AccountId,
    string AccountCode,
    string AccountName,
    string CostCenter,
    string PostedBy,
    decimal Debit,
    decimal Credit,
    decimal RunningBalance);

public record GeneralLedgerView(
    string Period,
    decimal Opening,
    decimal TotalDebit,
    decimal TotalCredit,
    decimal Closing,
    IReadOnlyList<GeneralLedgerRow> Rows);

/// <summary>One line of a trial balance: an account with its net debit/credit position.</summary>
public record TrialBalanceRow(int AccountId, string Code, string Name, decimal Debit, decimal Credit);

public record TrialBalance(
    string Period,
    IReadOnlyList<TrialBalanceRow> Rows,
    decimal TotalDebit,
    decimal TotalCredit)
{
    public bool IsBalanced => TotalDebit == TotalCredit;
}

/// <summary>One line of a From/To trial balance: an account's Opening balance, its Debit/Credit
/// activity within the range, and the resulting Closing balance. <see cref="Type"/> is carried
/// along so the report's Summary format can subtotal by account type without a second query.</summary>
public record TrialBalanceMovementRow(
    int AccountId, string Code, string Name, AccountType Type,
    decimal OpeningDebit, decimal OpeningCredit,
    decimal PeriodDebit, decimal PeriodCredit,
    decimal ClosingDebit, decimal ClosingCredit);

public record TrialBalanceMovement(
    string RangeLabel,
    IReadOnlyList<TrialBalanceMovementRow> Rows,
    decimal TotalOpeningDebit, decimal TotalOpeningCredit,
    decimal TotalPeriodDebit, decimal TotalPeriodCredit,
    decimal TotalClosingDebit, decimal TotalClosingCredit)
{
    public bool IsBalanced => TotalClosingDebit == TotalClosingCredit;
}

/// <summary>One revenue or expense line on the P&amp;L, with period and year-to-date figures.</summary>
public record PnlLine(int AccountId, string Code, string Name, decimal Period, decimal Ytd);

/// <summary>
/// A Zoho-style sectioned P&amp;L: Operating Income and Cost of Goods Sold combine into Gross
/// Profit; Operating Expense on top of that gives Operating Profit; Non-Operating Income/Expense
/// on top of that gives Net Profit. Every figure exists in both a "Period" flavor (the requested
/// date range) and a "Ytd" flavor (cumulative to the range's end date).
/// </summary>
public record ProfitAndLoss(
    string PeriodName,
    IReadOnlyList<PnlLine> OperatingIncome,
    IReadOnlyList<PnlLine> CostOfGoodsSold,
    IReadOnlyList<PnlLine> OperatingExpense,
    IReadOnlyList<PnlLine> NonOperatingIncome,
    IReadOnlyList<PnlLine> NonOperatingExpense,
    decimal OperatingIncomePeriod, decimal OperatingIncomeYtd,
    decimal CostOfGoodsSoldPeriod, decimal CostOfGoodsSoldYtd,
    decimal OperatingExpensePeriod, decimal OperatingExpenseYtd,
    decimal NonOperatingIncomePeriod, decimal NonOperatingIncomeYtd,
    decimal NonOperatingExpensePeriod, decimal NonOperatingExpenseYtd)
{
    public decimal GrossProfitPeriod => OperatingIncomePeriod - CostOfGoodsSoldPeriod;
    public decimal GrossProfitYtd => OperatingIncomeYtd - CostOfGoodsSoldYtd;
    public decimal OperatingProfitPeriod => GrossProfitPeriod - OperatingExpensePeriod;
    public decimal OperatingProfitYtd => GrossProfitYtd - OperatingExpenseYtd;
    public decimal NetProfitPeriod => OperatingProfitPeriod + NonOperatingIncomePeriod - NonOperatingExpensePeriod;
    public decimal NetProfitYtd => OperatingProfitYtd + NonOperatingIncomeYtd - NonOperatingExpenseYtd;
}

/// <summary>One line on the balance sheet (account balance in its natural positive sense).</summary>
public record BsLine(int AccountId, string Code, string Name, decimal Amount);

public record BalanceSheet(
    string AsOf,
    IReadOnlyList<BsLine> Assets,
    IReadOnlyList<BsLine> Liabilities,
    IReadOnlyList<BsLine> Equity,
    decimal CurrentYearEarnings)
{
    public decimal TotalAssets => Assets.Sum(l => l.Amount);
    public decimal TotalLiabilities => Liabilities.Sum(l => l.Amount);
    public decimal TotalEquity => Equity.Sum(l => l.Amount) + CurrentYearEarnings;
    public decimal TotalLiabilitiesAndEquity => TotalLiabilities + TotalEquity;
    public bool IsBalanced => TotalAssets == TotalLiabilitiesAndEquity;
}

public record DashboardKpis(
    string Period,
    decimal Income,
    decimal Expense,
    decimal NetProfit,
    decimal CashAndBank,
    decimal Receivables,
    decimal Payables,
    int DraftVouchers);

/// <summary>Per-period revenue and expense series for the dashboard bar chart.</summary>
public record PeriodSeries(string[] Labels, double[] Revenue, double[] Expense);

/// <summary>A cash/bank account with its current balance, for the dashboard.</summary>
public record CashBalance(int AccountId, string Code, string Name, decimal Balance);

/// <summary>Input line used when creating/posting a voucher from the UI.</summary>
public record VoucherLineInput(int AccountId, int? CostCenterId, string? Description, decimal Debit, decimal Credit);
