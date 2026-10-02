using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>One line within a Service Kit — the template a document's line gets built from when
/// "Insert Kit" is used.</summary>
public record ServiceKitLineInput(
    string Description, int? RevenueAccountId, decimal GovtFee, decimal CenterFee, decimal BankCharge, decimal VatRate);

/// <summary>
/// Admin CRUD for Service Kits (reusable bundles of PRO-service lines) and their lines — a kit is
/// defined once here, then "Insert Kit" on Estimate/Sales Invoice appends its lines to the document
/// being edited. Only relevant when <see cref="CompanySetup.ProServiceModeEnabled"/> is on.
/// </summary>
public class ServiceKitService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;
    public ServiceKitService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    public async Task<List<ServiceKit>> GetAllAsync(bool activeOnly = false)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.ServiceKits.AsNoTracking().Include(k => k.Lines).ThenInclude(l => l.RevenueAccount)
            .OrderBy(k => k.Name).AsQueryable();
        if (activeOnly) q = q.Where(k => k.IsActive);
        return await q.ToListAsync();
    }

    public async Task<ServiceKit> CreateAsync(string name, IEnumerable<ServiceKitLineInput>? lines = null)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new PostingException("Kit name is required.");

        await using var db = await _dbf.CreateDbContextAsync();
        var kit = new ServiceKit { Name = name };
        foreach (var l in BuildLines(lines ?? Enumerable.Empty<ServiceKitLineInput>()))
            kit.Lines.Add(l);

        db.ServiceKits.Add(kit);
        await db.SaveChangesAsync();
        return kit;
    }

    /// <summary>Updates the kit's name and fully replaces its lines with what's submitted.</summary>
    public async Task UpdateAsync(int id, string name, IEnumerable<ServiceKitLineInput>? lines = null)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new PostingException("Kit name is required.");

        await using var db = await _dbf.CreateDbContextAsync();
        var kit = await db.ServiceKits.Include(k => k.Lines).FirstOrDefaultAsync(k => k.Id == id)
            ?? throw new PostingException("Kit not found.");

        kit.Name = name;
        kit.Lines.Clear();
        foreach (var l in BuildLines(lines ?? Enumerable.Empty<ServiceKitLineInput>()))
            kit.Lines.Add(l);

        await db.SaveChangesAsync();
    }

    public async Task SetActiveAsync(int id, bool isActive)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var kit = await db.ServiceKits.FirstOrDefaultAsync(k => k.Id == id)
            ?? throw new PostingException("Kit not found.");
        kit.IsActive = isActive;
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var kit = await db.ServiceKits.FirstOrDefaultAsync(k => k.Id == id)
            ?? throw new PostingException("Kit not found.");
        db.ServiceKits.Remove(kit); // cascades to its Lines — a kit is just a template, nothing else references it
        await db.SaveChangesAsync();
    }

    private static List<ServiceKitLine> BuildLines(IEnumerable<ServiceKitLineInput> inputs)
    {
        var list = new List<ServiceKitLine>();
        var no = 0;
        foreach (var i in inputs)
        {
            if (string.IsNullOrWhiteSpace(i.Description)) continue; // a stray blank row — ignore, not an error
            list.Add(new ServiceKitLine
            {
                SortOrder = no++,
                Description = i.Description.Trim(),
                RevenueAccountId = i.RevenueAccountId,
                GovtFee = i.GovtFee,
                CenterFee = i.CenterFee,
                BankCharge = i.BankCharge,
                VatRate = i.VatRate,
            });
        }
        return list;
    }
}
