namespace AegisErp.Domain.Entities;

/// <summary>
/// One payroll cycle for one fiscal period. A Draft run can have its lines adjusted (e.g. a
/// deduction for unpaid leave) before <c>PayrollService.PostRunAsync</c> generates the GL voucher
/// and locks it — at most one Draft or Posted run per fiscal period (enforced by a unique index).
/// </summary>
public class PayrollRun : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    public int FiscalPeriodId { get; set; }
    public FiscalPeriod FiscalPeriod { get; set; } = null!;

    public DateOnly RunDate { get; set; }
    public VoucherStatus Status { get; set; } = VoucherStatus.Draft;

    public int? JournalVoucherId { get; set; }
    public JournalVoucher? JournalVoucher { get; set; }

    /// <summary>Set once the run has actually been paid out (a later step than posting to the
    /// GL — posting recognizes the expense/liability, this records the cash actually moving).</summary>
    public bool IsPaid { get; set; }
    public DateOnly? PaidDate { get; set; }
    public int? PaidFromBankAccountId { get; set; }
    public Account? PaidFromBankAccount { get; set; }

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PostedAtUtc { get; set; }

    public List<PayrollRunLine> Lines { get; set; } = new();

    public decimal TotalGross => Lines.Sum(l => l.GrossPay);
    public decimal TotalDeductions => Lines.Sum(l => l.Deductions);
    public decimal TotalSalaryAdvanceDeductions => Lines.Sum(l => l.SalaryAdvanceDeduction);
    public decimal TotalNet => Lines.Sum(l => l.NetPay);
}

/// <summary>
/// One employee's pay for one <see cref="PayrollRun"/> — every salary/allowance field is
/// snapshotted at run-creation time from the <see cref="Employee"/> record, so a later change to
/// the employee's salary (a raise, a correction) never retroactively alters an already-created or
/// already-posted run. Mirrors the frozen-snapshot pattern used elsewhere in this app (e.g. a sales
/// invoice's contact details snapshotted off the customer record).
/// </summary>
public class PayrollRunLine
{
    public int Id { get; set; }

    public int PayrollRunId { get; set; }
    public PayrollRun PayrollRun { get; set; } = null!;

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public decimal BasicSalary { get; set; }
    public decimal HousingAllowance { get; set; }
    public decimal TransportAllowance { get; set; }
    public decimal OtherAllowance { get; set; }

    /// <summary>A single flat adjustment for this run only (e.g. unpaid leave) — not itemized in
    /// v1. Editable while the run is Draft; frozen once posted. A tracked loan/advance repayment
    /// goes through <see cref="SalaryAdvanceId"/>/<see cref="SalaryAdvanceDeduction"/> below
    /// instead, now that those exist — this field remains for every other kind of ad-hoc
    /// deduction.</summary>
    public decimal Deductions { get; set; }

    /// <summary>Which <see cref="Domain.Entities.SalaryAdvance"/> this line repays, if the employee
    /// had an Active one when the run was created — see <c>PayrollService.CreateDraftRunAsync</c>.
    /// Null for an employee with no outstanding advance.</summary>
    public int? SalaryAdvanceId { get; set; }
    public SalaryAdvance? SalaryAdvance { get; set; }

    /// <summary>Amount of this line's pay withheld to repay <see cref="SalaryAdvance"/> — pre-filled
    /// from <see cref="Domain.Entities.SalaryAdvance.MonthlyDeductionAmount"/> but editable while
    /// Draft (e.g. to pay off a small remaining balance in full). Posted to
    /// <see cref="WellKnownAccounts.EmployeeAdvancesReceivable"/> instead of the run's user-picked
    /// deductions account, and decrements the advance's balance — see
    /// <c>PayrollService.PostRunAsync</c>.</summary>
    public decimal SalaryAdvanceDeduction { get; set; }

    /// <summary>Snapshot of the employee's expense account at run-creation time.</summary>
    public int ExpenseAccountId { get; set; }
    public Account ExpenseAccount { get; set; } = null!;

    public decimal GrossPay => BasicSalary + HousingAllowance + TransportAllowance + OtherAllowance;
    public decimal NetPay => GrossPay - Deductions - SalaryAdvanceDeduction;
}
