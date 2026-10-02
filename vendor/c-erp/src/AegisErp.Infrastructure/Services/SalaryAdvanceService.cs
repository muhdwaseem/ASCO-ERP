using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>
/// Issues and tracks employee salary advances/loans recovered through payroll deductions. Issuing
/// one is a real cash event (Dr <see cref="WellKnownAccounts.EmployeeAdvancesReceivable"/> / Cr
/// Bank), posted here; the recovery side is driven from <c>PayrollService</c> — see
/// <see cref="PayrollRunLine.SalaryAdvanceDeduction"/>.
/// </summary>
public class SalaryAdvanceService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;
    public SalaryAdvanceService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    public async Task<List<SalaryAdvance>> GetAllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.SalaryAdvances.AsNoTracking()
            .Include(a => a.Employee)
            .OrderByDescending(a => a.IssueDate).ThenByDescending(a => a.Id)
            .ToListAsync();
    }

    /// <summary>The employee's Active advance, if any — at most one can exist at a time (enforced
    /// by <see cref="IssueAsync"/>). Used both by the Employee detail panel and by
    /// <c>PayrollService.CreateDraftRunAsync</c> to pre-fill a new run's deduction.</summary>
    public async Task<SalaryAdvance?> GetActiveForEmployeeAsync(int employeeId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.SalaryAdvances.AsNoTracking()
            .Where(a => a.EmployeeId == employeeId && a.Status == SalaryAdvanceStatus.Active)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync();
    }

    public async Task<List<SalaryAdvance>> GetForEmployeeAsync(int employeeId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.SalaryAdvances.AsNoTracking()
            .Where(a => a.EmployeeId == employeeId)
            .OrderByDescending(a => a.IssueDate).ThenByDescending(a => a.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Pays out a new advance: Dr <see cref="WellKnownAccounts.EmployeeAdvancesReceivable"/> / Cr
    /// the chosen bank account, for the full amount, on the spot (this app doesn't model a
    /// separate "approved but not yet paid" stage — matches how Direct Expense Pay-Later and
    /// Gratuity both post the moment the underlying event is recorded).
    /// </summary>
    public async Task<SalaryAdvance> IssueAsync(
        int employeeId, decimal amount, decimal monthlyDeductionAmount, string? reason,
        int bankAccountId, DateOnly issueDate, string postedBy, DateTime nowUtc)
    {
        if (amount <= 0) throw new PostingException("Advance amount must be positive.");
        if (monthlyDeductionAmount <= 0) throw new PostingException("Monthly deduction amount must be positive.");
        if (monthlyDeductionAmount > amount) throw new PostingException("Monthly deduction cannot exceed the advance amount.");

        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var employee = await db.Employees.FirstOrDefaultAsync(m => m.Id == employeeId)
            ?? throw new PostingException("Employee not found.");
        if (employee.Status != EmployeeStatus.Active)
            throw new PostingException($"{employee.FullName} is not an active employee.");
        if (await db.SalaryAdvances.AnyAsync(a => a.EmployeeId == employeeId && a.Status == SalaryAdvanceStatus.Active))
            throw new PostingException($"{employee.FullName} already has an active advance — settle it before issuing another.");

        var period = await db.FiscalPeriods.FirstOrDefaultAsync(p => issueDate >= p.StartDate && issueDate <= p.EndDate)
            ?? throw new PostingException($"No fiscal period covers {issueDate:dd MMM yyyy}.");

        var advancesReceivable = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.EmployeeAdvancesReceivable);
        var who = $"{employee.FullName} ({employee.EmployeeCode})";
        var lines = new List<VoucherLineInput>
        {
            new(advancesReceivable.Id, null, $"Salary advance — {who}", amount, 0),
            new(bankAccountId, null, $"Salary advance — {who}", 0, amount),
        };

        var voucher = await JournalPoster.PostAsync(db, _current.CanPost, VoucherType.Journal, null, issueDate, period.Id,
            $"Salary advance — {who}", null, postedBy, lines, nowUtc);

        var advance = new SalaryAdvance
        {
            EmployeeId = employeeId,
            Amount = amount,
            IssueDate = issueDate,
            MonthlyDeductionAmount = monthlyDeductionAmount,
            RemainingBalance = amount,
            Status = SalaryAdvanceStatus.Active,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            JournalVoucher = voucher,
            CreatedBy = postedBy,
            CreatedAtUtc = nowUtc,
        };
        db.SalaryAdvances.Add(advance);

        await JournalPoster.SaveAndCommitAsync(db, tx);
        return advance;
    }
}
