namespace AegisErp.Domain.Entities;

/// <summary>
/// One employee's End of Service Benefits (gratuity) calculation and posting, per UAE Federal
/// Decree-Law No. 33 of 2021, Article 51 (mainland UAE — DIFC/ADGM use a different DEWS scheme,
/// out of scope). Calculated once at termination, not accrued monthly — see
/// <c>GratuityService.CalculateGratuity</c> for the formula and the rationale for this
/// simplification. Mirrors <see cref="PayrollRun"/>'s own Post-then-Paid lifecycle.
/// </summary>
public class GratuityPayment : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public DateOnly TerminationDate { get; set; }

    /// <summary>Snapshotted at calculation time — not recomputed later even if the employee's own
    /// JoiningDate is somehow edited afterward.</summary>
    public decimal YearsOfService { get; set; }
    public decimal BasicSalaryUsed { get; set; }
    public decimal CalculatedAmount { get; set; }

    public VoucherStatus Status { get; set; } = VoucherStatus.Draft;

    /// <summary>Snapshot of the Gratuity Expense (Dr) account chosen at posting time.</summary>
    public int? ExpenseAccountId { get; set; }
    public Account? ExpenseAccount { get; set; }

    public int? JournalVoucherId { get; set; }
    public JournalVoucher? JournalVoucher { get; set; }

    public bool IsPaid { get; set; }
    public DateOnly? PaidDate { get; set; }
    public int? PaidFromBankAccountId { get; set; }
    public Account? PaidFromBankAccount { get; set; }

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PostedAtUtc { get; set; }
}
