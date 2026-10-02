using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

public class RecurringInvoiceService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly SalesInvoiceService _invoiceSvc;
    private readonly ICurrentCompany _current;

    public RecurringInvoiceService(IDbContextFactory<AegisDbContext> dbf, SalesInvoiceService invoiceSvc, ICurrentCompany current)
    {
        _dbf = dbf;
        _invoiceSvc = invoiceSvc;
        _current = current;
    }

    public async Task<List<RecurringInvoiceProfile>> GetAllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.RecurringInvoiceProfiles.AsNoTracking()
            .Include(p => p.Customer).Include(p => p.Lines)
            .OrderBy(p => p.NextGenerationDate)
            .ToListAsync();
    }

    public async Task<RecurringInvoiceProfile?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.RecurringInvoiceProfiles.AsNoTracking()
            .Include(p => p.Customer).Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    /// <summary>Creates a recurring profile from an existing sales invoice's customer + lines — the "Make Recurring" action.</summary>
    public async Task<RecurringInvoiceProfile> CreateFromInvoiceAsync(
        int sourceInvoiceId, RecurringFrequency frequency, int repeatEvery, DateOnly startDate, DateOnly? endDate,
        string createdBy, DateTime nowUtc)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        if (repeatEvery <= 0) throw new PostingException("Repeat interval must be at least 1.");
        if (endDate is DateOnly end && end < startDate) throw new PostingException("End date cannot be before the start date.");

        await using var db = await _dbf.CreateDbContextAsync();
        var source = await db.SalesInvoices.AsNoTracking().Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.Id == sourceInvoiceId)
            ?? throw new PostingException("Source invoice not found.");

        var profile = new RecurringInvoiceProfile
        {
            CustomerId = source.CustomerId,
            Frequency = frequency,
            RepeatEvery = repeatEvery,
            StartDate = startDate,
            EndDate = endDate,
            NextGenerationDate = startDate,
            Narration = source.Narration,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
        };

        var no = 1;
        foreach (var l in source.Lines.OrderBy(l => l.LineNo))
            profile.Lines.Add(new RecurringInvoiceProfileLine
            {
                LineNo = no++,
                Description = l.Description,
                RevenueAccountId = l.RevenueAccountId,
                CostCenterId = l.CostCenterId,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                VatRate = l.VatRate,
            });
        if (profile.Lines.Count == 0)
            throw new PostingException("Source invoice has no lines to recur.");

        db.RecurringInvoiceProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile;
    }

    public async Task PauseAsync(int id)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        await using var db = await _dbf.CreateDbContextAsync();
        var profile = await db.RecurringInvoiceProfiles.FirstOrDefaultAsync(p => p.Id == id)
            ?? throw new PostingException("Recurring profile not found.");
        profile.IsActive = false;
        await db.SaveChangesAsync();
    }

    public async Task ResumeAsync(int id)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        await using var db = await _dbf.CreateDbContextAsync();
        var profile = await db.RecurringInvoiceProfiles.FirstOrDefaultAsync(p => p.Id == id)
            ?? throw new PostingException("Recurring profile not found.");
        profile.IsActive = true;
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        await using var db = await _dbf.CreateDbContextAsync();
        var profile = await db.RecurringInvoiceProfiles.FirstOrDefaultAsync(p => p.Id == id)
            ?? throw new PostingException("Recurring profile not found.");
        db.RecurringInvoiceProfiles.Remove(profile);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Generates one Draft sales invoice (never auto-posted — someone still reviews and posts each
    /// one, consistent with this app's "nothing touches the ledger without a human posting it"
    /// convention) for every active profile whose <see cref="RecurringInvoiceProfile.NextGenerationDate"/>
    /// is on or before <paramref name="asOf"/>, then advances that date by the profile's own
    /// Frequency/RepeatEvery. A profile whose next run would fall after its <see cref="RecurringInvoiceProfile.EndDate"/>
    /// is deactivated instead. Returns how many invoices were generated.
    /// </summary>
    public async Task<int> GenerateDueInvoicesAsync(DateOnly asOf, string createdBy, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var due = await db.RecurringInvoiceProfiles.Include(p => p.Lines)
            .Where(p => p.IsActive && p.NextGenerationDate <= asOf)
            .ToListAsync();

        var generated = 0;
        foreach (var profile in due)
        {
            var period = await db.FiscalPeriods.AsNoTracking()
                .FirstOrDefaultAsync(p => asOf >= p.StartDate && asOf <= p.EndDate);
            if (period is null) continue; // no open fiscal period covers this date yet — skip, retry next tick

            var lines = profile.Lines.OrderBy(l => l.LineNo)
                .Select(l => new InvoiceLineInput(l.Description, l.RevenueAccountId, l.CostCenterId, l.Quantity, l.UnitPrice, l.VatRate))
                .ToList();
            await _invoiceSvc.CreateDraftAsync(profile.CustomerId, asOf, period.Id, profile.Narration, createdBy, lines, nowUtc);

            profile.NextGenerationDate = profile.NextGenerationDate.AddFrequency(profile.Frequency, profile.RepeatEvery);
            if (profile.EndDate is DateOnly end && profile.NextGenerationDate > end)
                profile.IsActive = false;
            generated++;
        }

        await db.SaveChangesAsync();
        return generated;
    }
}
