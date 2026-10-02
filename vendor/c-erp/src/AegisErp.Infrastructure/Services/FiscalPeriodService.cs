using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Manages a company's accounting calendar. Vouchers can only post into a period created here.</summary>
public class FiscalPeriodService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;
    public FiscalPeriodService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    public async Task<List<FiscalPeriod>> GetAllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.FiscalPeriods.AsNoTracking().OrderBy(p => p.StartDate).ToListAsync();
    }

    public async Task<FiscalPeriod> CreateAsync(string name, int year, int periodNo, DateOnly start, DateOnly end)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");
        if (string.IsNullOrWhiteSpace(name))
            throw new PostingException("Period name is required.");
        if (end < start)
            throw new PostingException("End date must be on or after the start date.");

        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.FiscalPeriods.AnyAsync(p => start <= p.EndDate && p.StartDate <= end))
            throw new PostingException("This date range overlaps an existing period.");

        var period = new FiscalPeriod { Name = name.Trim(), Year = year, PeriodNo = periodNo, StartDate = start, EndDate = end };
        db.FiscalPeriods.Add(period);
        await db.SaveChangesAsync();
        return period;
    }

    public async Task SetClosedAsync(int id, bool closed)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var period = await db.FiscalPeriods.FirstOrDefaultAsync(p => p.Id == id)
            ?? throw new PostingException("Period not found.");
        period.IsClosed = closed;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Generates twelve consecutive monthly periods starting from <paramref name="yearStart"/> —
    /// covers the common "just tell me the financial year start" case so a new company doesn't
    /// need twelve individual periods entered by hand.
    /// </summary>
    public static List<FiscalPeriod> BuildMonthlyYear(DateOnly yearStart)
    {
        var periods = new List<FiscalPeriod>();
        for (var i = 0; i < 12; i++)
        {
            var start = yearStart.AddMonths(i);
            var end = yearStart.AddMonths(i + 1).AddDays(-1);
            periods.Add(new FiscalPeriod
            {
                Name = start.ToString("MMM yyyy"),
                Year = start.Year,
                PeriodNo = i + 1,
                StartDate = start,
                EndDate = end,
            });
        }
        return periods;
    }

    /// <summary>Same twelve-month generation, but persisted for the active company — used from the
    /// Fiscal Periods settings page to backfill a company that has none yet.</summary>
    public async Task<List<FiscalPeriod>> GenerateMonthlyYearAsync(DateOnly yearStart)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.FiscalPeriods.AnyAsync(p => p.Year == yearStart.Year))
            throw new PostingException($"{yearStart.Year} already has periods — add any missing ones individually below.");

        var periods = BuildMonthlyYear(yearStart);
        db.FiscalPeriods.AddRange(periods);
        await db.SaveChangesAsync();
        return periods;
    }
}
