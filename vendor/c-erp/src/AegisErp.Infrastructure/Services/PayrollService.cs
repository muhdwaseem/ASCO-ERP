using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

public class PayrollService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;
    public PayrollService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    public async Task<List<PayrollRun>> GetAllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.PayrollRuns.AsNoTracking()
            .Include(r => r.FiscalPeriod).Include(r => r.Lines).ThenInclude(l => l.Employee)
            .OrderByDescending(r => r.RunDate).ThenByDescending(r => r.Id)
            .ToListAsync();
    }

    public async Task<PayrollRun?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.PayrollRuns.AsNoTracking()
            .Include(r => r.FiscalPeriod).Include(r => r.PaidFromBankAccount)
            .Include(r => r.Lines).ThenInclude(l => l.Employee)
            .Include(r => r.Lines).ThenInclude(l => l.ExpenseAccount)
            .Include(r => r.Lines).ThenInclude(l => l.SalaryAdvance)
            .FirstOrDefaultAsync(r => r.Id == id);
    }

    /// <summary>One payroll line with its parent run and employee loaded, for the payslip page.
    /// Goes through PayrollRuns (company-scoped) rather than querying PayrollRunLines directly,
    /// since the line itself carries no CompanyId — same "scope through the parent" pattern used
    /// elsewhere in this app for line-level entities with no filter of their own.</summary>
    public async Task<PayrollRunLine?> GetRunLineAsync(int lineId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.PayrollRuns.AsNoTracking()
            .SelectMany(r => r.Lines)
            .Include(l => l.PayrollRun).ThenInclude(r => r.FiscalPeriod)
            .Include(l => l.Employee).ThenInclude(e => e.CostCenter)
            .FirstOrDefaultAsync(l => l.Id == lineId);
    }

    /// <summary>One employee's own payslip history, for Employee Self-Service — same "scope through
    /// the company-scoped parent" pattern as <see cref="GetRunLineAsync"/>, filtered to Posted runs
    /// only (a Draft run's figures aren't final, so there's nothing to hand an employee yet).</summary>
    public async Task<List<PayrollRunLine>> GetPayslipLinesForEmployeeAsync(int employeeId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.PayrollRuns.AsNoTracking()
            .Where(r => r.Status == VoucherStatus.Posted)
            .SelectMany(r => r.Lines)
            .Include(l => l.PayrollRun).ThenInclude(r => r.FiscalPeriod)
            .Include(l => l.Employee).ThenInclude(e => e.CostCenter)
            .Where(l => l.EmployeeId == employeeId)
            .OrderByDescending(l => l.PayrollRun.RunDate)
            .ToListAsync();
    }

    /// <summary>
    /// Creates a Draft payroll run for a fiscal period, with one line per Active employee —
    /// salary/allowance fields and the expense account are snapshotted from the employee record
    /// right now, so a later salary change never retroactively alters this run. At most one run
    /// (Draft or Posted) per fiscal period, enforced by a DB unique index.
    /// </summary>
    public async Task<PayrollRun> CreateDraftRunAsync(int fiscalPeriodId, DateOnly runDate, string createdBy, DateTime nowUtc)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        await using var db = await _dbf.CreateDbContextAsync();

        var period = await db.FiscalPeriods.FindAsync(fiscalPeriodId)
            ?? throw new PostingException("Fiscal period not found.");
        if (await db.PayrollRuns.AnyAsync(r => r.FiscalPeriodId == fiscalPeriodId))
            throw new PostingException($"A payroll run already exists for {period.Name}.");

        var employees = await db.Employees
            .Where(m => m.Status == EmployeeStatus.Active)
            .ToListAsync();
        if (employees.Count == 0)
            throw new PostingException("There are no active employees to run payroll for.");

        // Pre-fill this run's deduction for anyone with an outstanding advance — capped at
        // whatever's left so the last, smaller-than-usual installment doesn't overshoot.
        var activeAdvances = await db.SalaryAdvances
            .Where(a => a.Status == SalaryAdvanceStatus.Active)
            .ToDictionaryAsync(a => a.EmployeeId);

        var run = new PayrollRun
        {
            FiscalPeriodId = fiscalPeriodId,
            RunDate = runDate,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
        };
        foreach (var m in employees)
        {
            activeAdvances.TryGetValue(m.Id, out var advance);
            run.Lines.Add(new PayrollRunLine
            {
                EmployeeId = m.Id,
                BasicSalary = m.BasicSalary,
                HousingAllowance = m.HousingAllowance,
                TransportAllowance = m.TransportAllowance,
                OtherAllowance = m.OtherAllowance,
                ExpenseAccountId = m.EmployeeExpenseAccountId,
                SalaryAdvanceId = advance?.Id,
                SalaryAdvanceDeduction = advance is null ? 0 : Math.Min(advance.MonthlyDeductionAmount, advance.RemainingBalance),
            });
        }

        db.PayrollRuns.Add(run);
        // Translated save: the DB's unique (CompanyId, FiscalPeriodId) index is the real race
        // guard if two requests both pass the AnyAsync check above at the same instant — this
        // turns that into the same friendly "conflicts with one just posted" message every other
        // document type already gives, instead of a raw DbUpdateException.
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return run;
    }

    /// <summary>Adjusts one employee's deduction(s) on a run that hasn't been posted yet.
    /// <paramref name="salaryAdvanceDeduction"/> is only meaningful when the line already carries
    /// a <see cref="PayrollRunLine.SalaryAdvanceId"/> (pre-filled by <see cref="CreateDraftRunAsync"/>)
    /// — capped at that advance's current remaining balance so it can never overpay it.</summary>
    public async Task UpdateRunLineAsync(int runLineId, decimal deductions, decimal salaryAdvanceDeduction = 0)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        if (deductions < 0) throw new PostingException("Deductions cannot be negative.");
        if (salaryAdvanceDeduction < 0) throw new PostingException("Salary advance deduction cannot be negative.");

        await using var db = await _dbf.CreateDbContextAsync();
        // Route through the company-scoped PayrollRuns (not db.PayrollRunLines directly) so a
        // runLineId can never reach another company's line — PayrollRunLine has no CompanyId/
        // filter of its own, same reasoning as every other line-level entity in this codebase.
        var line = await db.PayrollRuns.SelectMany(r => r.Lines).Include(l => l.SalaryAdvance)
            .FirstOrDefaultAsync(l => l.Id == runLineId)
            ?? throw new PostingException("Payroll run line not found.");
        var run = await db.PayrollRuns.FirstAsync(r => r.Id == line.PayrollRunId);
        if (run.Status != VoucherStatus.Draft)
            throw new PostingException("Only a Draft run's lines can be adjusted.");
        if (salaryAdvanceDeduction > 0 && line.SalaryAdvance is null)
            throw new PostingException("This employee has no active salary advance to deduct.");
        if (line.SalaryAdvance is not null && salaryAdvanceDeduction > line.SalaryAdvance.RemainingBalance)
            throw new PostingException($"Salary advance deduction cannot exceed the remaining balance of {line.SalaryAdvance.RemainingBalance:0.00}.");
        if (deductions + salaryAdvanceDeduction > line.GrossPay)
            throw new PostingException("Deductions cannot exceed gross pay.");

        line.Deductions = deductions;
        line.SalaryAdvanceDeduction = salaryAdvanceDeduction;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Posts a Draft run: one voucher with Dr each employee's own salary expense account for their
    /// Gross pay, Cr <see cref="WellKnownAccounts.SalariesPayable"/> for the total Net pay, and (if
    /// any line has a deduction) Cr <paramref name="deductionsAccountId"/> for the total deducted —
    /// a three-way split mirroring the existing Dr-gross/Cr-net/Cr-tax idiom used for AR. Reviewed
    /// with the Bookkeeper &amp; Controller agent: deductions can represent genuinely different
    /// things (a loan recovery, unpaid leave, a statutory withholding) that a single flat field
    /// can't distinguish, so — unlike Salaries Payable — this is deliberately a user-picked account
    /// per run, not a fixed control-account code; a Liability "clearing" account is the recommended
    /// default since it stays visible and reconcilable rather than silently reducing expense.
    /// </summary>
    public async Task<JournalVoucher> PostRunAsync(int runId, int? deductionsAccountId, string postedBy, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var run = await db.PayrollRuns.Include(r => r.FiscalPeriod).Include(r => r.Lines).ThenInclude(l => l.Employee)
            .Include(r => r.Lines).ThenInclude(l => l.SalaryAdvance)
            .FirstOrDefaultAsync(r => r.Id == runId)
            ?? throw new PostingException("Payroll run not found.");
        if (run.Status != VoucherStatus.Draft)
            throw new PostingException("This payroll run has already been posted.");
        if (run.Lines.Count == 0)
            throw new PostingException("Payroll run has no employees.");
        if (run.TotalGross <= 0)
            throw new PostingException("Payroll run total must be positive.");
        if (run.TotalDeductions > 0 && deductionsAccountId is null)
            throw new PostingException("Select an account for the payroll deductions.");

        var period = run.FiscalPeriod;

        var lines = run.Lines.Select(l => new VoucherLineInput(
            l.ExpenseAccountId, null, $"Salary — {l.Employee.FullName} — {period.Name}", l.GrossPay, 0)).ToList();

        // Credit totals are sums of the already-persisted per-line Net/Deductions amounts — never
        // independently recomputed (e.g. never TotalGross minus a separately-summed deduction
        // total) — so the voucher can never be a cent off balanced.
        var salariesPayable = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.SalariesPayable);
        lines.Add(new VoucherLineInput(salariesPayable.Id, null, $"Salaries payable — {period.Name}", 0, run.TotalNet));
        if (run.TotalDeductions > 0)
            lines.Add(new VoucherLineInput(deductionsAccountId!.Value, null, $"Payroll deductions — {period.Name}", 0, run.TotalDeductions));
        if (run.TotalSalaryAdvanceDeductions > 0)
        {
            var advancesReceivable = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.EmployeeAdvancesReceivable);
            lines.Add(new VoucherLineInput(advancesReceivable.Id, null, $"Salary advance recovery — {period.Name}", 0, run.TotalSalaryAdvanceDeductions));
        }

        var voucher = await JournalPoster.PostAsync(db, _current.CanPost, VoucherType.Journal, null, run.RunDate, run.FiscalPeriodId,
            $"Payroll run — {period.Name}", null, postedBy, lines, nowUtc);

        run.JournalVoucher = voucher;
        run.Status = VoucherStatus.Posted;
        run.PostedAtUtc = nowUtc;

        foreach (var l in run.Lines.Where(l => l.SalaryAdvanceDeduction > 0 && l.SalaryAdvance is not null))
        {
            var advance = l.SalaryAdvance!;
            advance.RemainingBalance -= l.SalaryAdvanceDeduction;
            if (advance.RemainingBalance <= 0)
            {
                advance.RemainingBalance = 0;
                advance.Status = SalaryAdvanceStatus.Settled;
            }
        }

        await JournalPoster.SaveAndCommitAsync(db, tx);
        return voucher;
    }

    /// <summary>
    /// Records that a posted run's salaries were actually transferred: Dr
    /// <see cref="WellKnownAccounts.SalariesPayable"/> / Cr the bank account, one combined entry
    /// for the run's total net pay — not per-employee payment tracking, which is intentionally
    /// deferred (that's <c>VendorPaymentAllocation</c>-level complexity). This is a separate step
    /// from <see cref="PostRunAsync"/> because posting recognises the expense/liability the moment
    /// payroll is run, while the cash might not actually move until later (or via a batch WPS
    /// transfer) — the two events are rarely simultaneous in practice.
    /// </summary>
    public async Task<JournalVoucher> MarkPaidAsync(int runId, int bankAccountId, DateOnly paidDate, string postedBy, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var run = await db.PayrollRuns.Include(r => r.FiscalPeriod).Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == runId)
            ?? throw new PostingException("Payroll run not found.");
        if (run.Status != VoucherStatus.Posted)
            throw new PostingException("Only a posted payroll run can be marked as paid.");
        if (run.IsPaid)
            throw new PostingException("This payroll run has already been marked as paid.");

        // The payment can land in a later fiscal period than the run itself (e.g. run for May,
        // paid in June) — resolve the period from paidDate, not the run's own FiscalPeriodId,
        // the same way FixedAssetService.DisposeAsync resolves its own posting date's period.
        var paymentPeriod = await db.FiscalPeriods.FirstOrDefaultAsync(p => paidDate >= p.StartDate && paidDate <= p.EndDate)
            ?? throw new PostingException("No fiscal period covers the payment date.");

        var salariesPayable = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.SalariesPayable);
        var lines = new List<VoucherLineInput>
        {
            new(salariesPayable.Id, null, $"Salaries paid — {run.FiscalPeriod.Name}", run.TotalNet, 0),
            new(bankAccountId, null, $"Salaries paid — {run.FiscalPeriod.Name}", 0, run.TotalNet),
        };

        var voucher = await JournalPoster.PostAsync(db, _current.CanPost, VoucherType.Journal, null, paidDate, paymentPeriod.Id,
            $"Payroll payment — {run.FiscalPeriod.Name}", null, postedBy, lines, nowUtc);

        run.IsPaid = true;
        run.PaidDate = paidDate;
        run.PaidFromBankAccountId = bankAccountId;

        await JournalPoster.SaveAndCommitAsync(db, tx);
        return voucher;
    }
}
