namespace AegisErp.Domain.Entities;

/// <summary>
/// A cash advance/loan paid out to an employee, recovered gradually through payroll deductions.
/// Posting the advance itself (see <c>SalaryAdvanceService.IssueAsync</c>) is a real cash event —
/// Dr <see cref="WellKnownAccounts.EmployeeAdvancesReceivable"/> / Cr Bank — distinct from a
/// payroll run's flat, unstructured <see cref="PayrollRunLine.Deductions"/> field, which the app
/// has always allowed to represent an ad-hoc loan recovery too but never tracked as a balance.
/// Simplifying assumption: at most one Active advance per employee at a time (enforced by
/// <c>SalaryAdvanceService.IssueAsync</c>) — avoids any ambiguity about which of several advances a
/// given run's deduction repays.
/// </summary>
public class SalaryAdvance : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public decimal Amount { get; set; }
    public DateOnly IssueDate { get; set; }

    /// <summary>Suggested per-run deduction — pre-fills (but doesn't lock) each new payroll run
    /// line's <see cref="PayrollRunLine.SalaryAdvanceDeduction"/> for this employee while the
    /// advance is Active.</summary>
    public decimal MonthlyDeductionAmount { get; set; }

    /// <summary>Starts equal to <see cref="Amount"/>, decremented by
    /// <see cref="PayrollRunLine.SalaryAdvanceDeduction"/> each time a run repaying it is posted —
    /// see <c>PayrollService.PostRunAsync</c>.</summary>
    public decimal RemainingBalance { get; set; }

    public SalaryAdvanceStatus Status { get; set; } = SalaryAdvanceStatus.Active;
    public string? Reason { get; set; }

    /// <summary>The Dr Advances Receivable / Cr Bank voucher posted when the cash was actually
    /// handed out.</summary>
    public int JournalVoucherId { get; set; }
    public JournalVoucher JournalVoucher { get; set; } = null!;

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }
}

public enum SalaryAdvanceStatus
{
    Active = 1,
    Settled = 2,
}
