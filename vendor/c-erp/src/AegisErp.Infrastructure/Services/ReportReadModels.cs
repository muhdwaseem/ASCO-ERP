namespace AegisErp.Infrastructure.Services;

/// <summary>One line inside a cash-flow section (positive = cash in, negative = cash out).</summary>
public record CashFlowLine(string Label, decimal Amount);

/// <summary>A cash-flow section (operating / investing / financing) with its own subtotal.</summary>
public record CashFlowSection(string Name, IReadOnlyList<CashFlowLine> Lines, decimal Total);

/// <summary>
/// Indirect-method cash flow statement. Every non-cash account is classified into exactly one
/// section, and each section's cash effect is the negative of that group's ledger movement — so
/// the three sections always sum to the real movement on the cash accounts.
/// </summary>
public record CashFlowStatement(
    string PeriodName,
    CashFlowSection Operating,
    CashFlowSection Investing,
    CashFlowSection Financing,
    decimal OpeningCash,
    decimal ClosingCash)
{
    public decimal NetChange => Operating.Total + Investing.Total + Financing.Total;

    /// <summary>The actual movement on the cash &amp; bank accounts; NetChange must equal this.</summary>
    public decimal ActualCashMovement => ClosingCash - OpeningCash;

    public bool IsReconciled =>
        Math.Round(NetChange, 2) == Math.Round(ActualCashMovement, 2);
}

/// <summary>Revenue/expense for one segment (cost centre) over a date range.</summary>
public record SegmentPnlRow(string Code, string Name, decimal Revenue, decimal Expense)
{
    public decimal Net => Revenue - Expense;

    /// <summary>Net margin as a fraction of revenue (0 when there is no revenue).</summary>
    public decimal Margin => Revenue == 0 ? 0 : Net / Revenue;
}

/// <summary>A customer's revenue or a vendor's spend over a date range (net of tax, net of credit/debit notes).</summary>
public record PartyAmountRow(string Code, string Name, decimal Amount, int DocumentCount);

/// <summary>
/// Revenue attributed to whichever salesperson owned a customer on each document's own date — see
/// <see cref="ReportsService.GetSalespersonRevenueAsync"/>. "Unassigned" groups documents whose
/// customer had no salesperson at the time.
/// </summary>
public record SalespersonRevenueRow(string Salesperson, decimal Amount, int DocumentCount);
