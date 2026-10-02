using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Read-only preview of what an employee's gratuity would be if terminated on a given
/// date — nothing is persisted until <c>GratuityService.PostGratuityAsync</c> is called.</summary>
public record GratuityPreview(
    int EmployeeId, string EmployeeName, bool Eligible, decimal YearsOfService,
    decimal BasicSalaryUsed, decimal CalculatedAmount, bool CapApplied);

public class GratuityService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;
    public GratuityService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    /// <summary>Most recent gratuity record for an employee, if any — for the detail panel's
    /// Gratuity section. An employee can only be re-terminated (and get a second record) after
    /// being reactivated, so "most recent" is always the one relevant to their current state.</summary>
    public async Task<GratuityPayment?> GetLatestForEmployeeAsync(int employeeId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.GratuityPayments.AsNoTracking()
            .Include(g => g.ExpenseAccount).Include(g => g.PaidFromBankAccount)
            .Where(g => g.EmployeeId == employeeId)
            .OrderByDescending(g => g.Id)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Years of continuous service as a decimal (e.g. 5.5), from the calendar day difference over
    /// 365 — an approximation, not exact Gregorian year/month/day reckoning a court would use; see
    /// the plan's documented caveat that this needs verification against a UAE labour consultant.
    /// </summary>
    public static decimal YearsOfService(DateOnly joiningDate, DateOnly terminationDate)
    {
        var totalDays = terminationDate.DayNumber - joiningDate.DayNumber;
        return totalDays <= 0 ? 0m : totalDays / 365m;
    }

    /// <summary>
    /// UAE Federal Decree-Law No. 33 of 2021, Article 51 (mainland UAE only — DIFC/ADGM use a
    /// different DEWS scheme, out of scope): minimum 1 year continuous service to qualify; 21
    /// days' basic salary per year for the first 5 years, 30 days' basic salary per year beyond
    /// that; capped at 2 years' total basic wage. Based on basic salary only — allowances are
    /// deliberately not part of <paramref name="basicMonthlySalary"/>.
    /// </summary>
    public static decimal CalculateGratuity(decimal basicMonthlySalary, DateOnly joiningDate, DateOnly terminationDate)
    {
        if (basicMonthlySalary <= 0) return 0m;
        var years = YearsOfService(joiningDate, terminationDate);
        if (years < 1m) return 0m;

        var dailyRate = basicMonthlySalary / 30m;
        var gratuity = years <= 5m
            ? dailyRate * 21m * years
            : dailyRate * 21m * 5m + dailyRate * 30m * (years - 5m);

        var cap = basicMonthlySalary * 24m;
        return Math.Round(Math.Min(gratuity, cap), 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Preview using the employee's *current* BasicSalary and JoiningDate — for the
    /// termination dialog, before anything is posted.</summary>
    public async Task<GratuityPreview> PreviewAsync(int employeeId, DateOnly terminationDate)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var employee = await db.Employees.FirstOrDefaultAsync(m => m.Id == employeeId)
            ?? throw new PostingException("Employee not found.");

        var years = YearsOfService(employee.JoiningDate, terminationDate);
        var amount = CalculateGratuity(employee.BasicSalary, employee.JoiningDate, terminationDate);

        var dailyRate = employee.BasicSalary / 30m;
        var uncapped = years < 1m ? 0m : years <= 5m
            ? dailyRate * 21m * years
            : dailyRate * 21m * 5m + dailyRate * 30m * (years - 5m);
        var capApplied = Math.Round(uncapped, 2, MidpointRounding.AwayFromZero) > amount;

        return new GratuityPreview(
            employee.Id, employee.FullName, employee.GratuityEligible && years >= 1m,
            years, employee.BasicSalary, employee.GratuityEligible ? amount : 0m, capApplied);
    }

    /// <summary>
    /// Processes an employee's termination: calculates gratuity from their current salary and
    /// joining date, posts <b>Dr <paramref name="expenseAccountId"/> (Gratuity Expense) / Cr
    /// <see cref="WellKnownAccounts.GratuityPayable"/></b> for the calculated amount, and flips the
    /// employee to <see cref="EmployeeStatus.Terminated"/> — all in one transaction, since gratuity
    /// posting and processing the termination are the same action (reviewed by the Bookkeeper &amp;
    /// Controller agent before this was written).
    ///
    /// If the employee is not <see cref="Employee.GratuityEligible"/> or has under a year of
    /// service, <see cref="GratuityPayment.CalculatedAmount"/> is 0 and no voucher is posted
    /// (<see cref="GratuityPayment.JournalVoucherId"/> stays null) — the row itself is still created
    /// and marked Posted, as the audit record that this employee was evaluated and found not owed
    /// anything. This is the one case where a Posted <see cref="GratuityPayment"/> has no voucher.
    /// </summary>
    public async Task<GratuityPayment> PostGratuityAsync(int employeeId, DateOnly terminationDate, int expenseAccountId, string postedBy, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var employee = await db.Employees.FirstOrDefaultAsync(m => m.Id == employeeId)
            ?? throw new PostingException("Employee not found.");
        if (employee.Status != EmployeeStatus.Active)
            throw new PostingException($"{employee.FullName} is not an active employee.");

        var years = YearsOfService(employee.JoiningDate, terminationDate);
        var amount = employee.GratuityEligible ? CalculateGratuity(employee.BasicSalary, employee.JoiningDate, terminationDate) : 0m;

        var payment = new GratuityPayment
        {
            EmployeeId = employee.Id,
            TerminationDate = terminationDate,
            YearsOfService = years,
            BasicSalaryUsed = employee.BasicSalary,
            CalculatedAmount = amount,
            ExpenseAccountId = expenseAccountId,
            Status = VoucherStatus.Posted,
            CreatedBy = postedBy,
            CreatedAtUtc = nowUtc,
            PostedAtUtc = nowUtc,
        };

        if (amount > 0)
        {
            var period = await db.FiscalPeriods.FirstOrDefaultAsync(p => terminationDate >= p.StartDate && terminationDate <= p.EndDate)
                ?? throw new PostingException($"No fiscal period covers {terminationDate:dd MMM yyyy}.");

            var gratuityPayable = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.GratuityPayable);
            var who = $"{employee.FullName} ({employee.EmployeeCode})";
            var lines = new List<VoucherLineInput>
            {
                new(expenseAccountId, employee.CostCenterId, $"Gratuity expense — {who}", amount, 0),
                new(gratuityPayable.Id, null, $"Gratuity payable — {who}", 0, amount),
            };

            var voucher = await JournalPoster.PostAsync(db, _current.CanPost, VoucherType.Journal, null, terminationDate, period.Id,
                $"Gratuity (EOSB) — {who} — {years:0.00} yrs — terminated {terminationDate:yyyy-MM-dd}", null, postedBy, lines, nowUtc);

            payment.JournalVoucher = voucher;
        }

        employee.Status = EmployeeStatus.Terminated;
        employee.TerminationDate = terminationDate;

        db.GratuityPayments.Add(payment);
        await JournalPoster.SaveAndCommitAsync(db, tx);
        return payment;
    }

    /// <summary>Dr <see cref="WellKnownAccounts.GratuityPayable"/> / Cr the bank account — identical
    /// shape to <see cref="PayrollService.MarkPaidAsync"/>.</summary>
    public async Task<JournalVoucher> MarkPaidAsync(int gratuityPaymentId, int bankAccountId, DateOnly paidDate, string postedBy, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var payment = await db.GratuityPayments.Include(g => g.Employee).FirstOrDefaultAsync(g => g.Id == gratuityPaymentId)
            ?? throw new PostingException("Gratuity payment not found.");
        if (payment.Status != VoucherStatus.Posted)
            throw new PostingException("Only a posted gratuity payment can be marked as paid.");
        if (payment.IsPaid)
            throw new PostingException("This gratuity payment has already been marked as paid.");
        if (payment.CalculatedAmount == 0 || payment.JournalVoucherId is null)
            throw new PostingException("No gratuity was owed — there is nothing to mark as paid.");

        var paymentPeriod = await db.FiscalPeriods.FirstOrDefaultAsync(p => paidDate >= p.StartDate && paidDate <= p.EndDate)
            ?? throw new PostingException("No fiscal period covers the payment date.");

        var gratuityPayable = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.GratuityPayable);
        var who = $"{payment.Employee.FullName} ({payment.Employee.EmployeeCode})";
        var lines = new List<VoucherLineInput>
        {
            new(gratuityPayable.Id, null, $"Gratuity paid — {who}", payment.CalculatedAmount, 0),
            new(bankAccountId, null, $"Gratuity paid — {who}", 0, payment.CalculatedAmount),
        };

        var voucher = await JournalPoster.PostAsync(db, _current.CanPost, VoucherType.Journal, null, paidDate, paymentPeriod.Id,
            $"Gratuity payment — {who}", null, postedBy, lines, nowUtc);

        payment.IsPaid = true;
        payment.PaidDate = paidDate;
        payment.PaidFromBankAccountId = bankAccountId;

        await JournalPoster.SaveAndCommitAsync(db, tx);
        return voucher;
    }
}
