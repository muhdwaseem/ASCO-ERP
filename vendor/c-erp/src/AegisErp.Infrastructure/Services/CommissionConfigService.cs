using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

public record CommissionCategoryRateInput(int ItemCategoryId, decimal BaseCommissionPercent);
public record CommissionProfitSlabInput(decimal GrossProfitFrom, decimal? GrossProfitTo, decimal CommissionAdditionPercent);
public record CommissionPositionRateInput(string Position, decimal RateMultiplier);

/// <summary>
/// CRUD for the Commission Workflow's three admin-editable rate tables — category base rate, gross-
/// profit slab addition, and employee-position multiplier — mirroring <see cref="TaxCodeService"/>'s
/// shape. Unlike a Tax Code, nothing else stores a foreign key to these rows (a calculated
/// <see cref="SalesCommissionRecord"/> snapshots the resulting amounts, not a reference back to the
/// rate row that produced them), so deleting a rate never needs a "still in use" guard.
/// </summary>
public class CommissionConfigService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;

    public CommissionConfigService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    // ── Category rates ──

    public async Task<List<CommissionCategoryRate>> GetCategoryRatesAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CommissionCategoryRates.AsNoTracking().Include(r => r.ItemCategory)
            .OrderBy(r => r.ItemCategory.Name).ToListAsync();
    }

    public async Task<CommissionCategoryRate> AddCategoryRateAsync(CommissionCategoryRateInput input)
    {
        if (!_current.CanAdminister) throw new PostingException("You don't have permission to do this.");
        ValidatePercent(input.BaseCommissionPercent, "Base commission");

        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.CommissionCategoryRates.AnyAsync(r => r.ItemCategoryId == input.ItemCategoryId && r.IsActive))
            throw new PostingException("This category already has an active commission rate — edit it instead.");

        var rate = new CommissionCategoryRate { ItemCategoryId = input.ItemCategoryId, BaseCommissionPercent = input.BaseCommissionPercent };
        db.CommissionCategoryRates.Add(rate);
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return rate;
    }

    public async Task UpdateCategoryRateAsync(int id, CommissionCategoryRateInput input)
    {
        if (!_current.CanAdminister) throw new PostingException("You don't have permission to do this.");
        ValidatePercent(input.BaseCommissionPercent, "Base commission");

        await using var db = await _dbf.CreateDbContextAsync();
        var rate = await db.CommissionCategoryRates.FirstOrDefaultAsync(r => r.Id == id)
            ?? throw new PostingException("Category rate not found.");
        if (await db.CommissionCategoryRates.AnyAsync(r => r.Id != id && r.ItemCategoryId == input.ItemCategoryId && r.IsActive))
            throw new PostingException("This category already has an active commission rate — edit it instead.");

        rate.ItemCategoryId = input.ItemCategoryId;
        rate.BaseCommissionPercent = input.BaseCommissionPercent;
        await JournalPoster.SaveChangesTranslatedAsync(db);
    }

    public async Task SetCategoryRateActiveAsync(int id, bool isActive)
    {
        if (!_current.CanAdminister) throw new PostingException("You don't have permission to do this.");
        await using var db = await _dbf.CreateDbContextAsync();
        var rate = await db.CommissionCategoryRates.FirstOrDefaultAsync(r => r.Id == id)
            ?? throw new PostingException("Category rate not found.");
        rate.IsActive = isActive;
        await JournalPoster.SaveChangesTranslatedAsync(db);
    }

    public async Task DeleteCategoryRateAsync(int id)
    {
        if (!_current.CanAdminister) throw new PostingException("You don't have permission to do this.");
        await using var db = await _dbf.CreateDbContextAsync();
        var rate = await db.CommissionCategoryRates.FirstOrDefaultAsync(r => r.Id == id)
            ?? throw new PostingException("Category rate not found.");
        db.CommissionCategoryRates.Remove(rate);
        await db.SaveChangesAsync();
    }

    // ── Profit slabs ──

    public async Task<List<CommissionProfitSlab>> GetProfitSlabsAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        // Ordered client-side — Sqlite can't translate ORDER BY on a decimal column.
        return (await db.CommissionProfitSlabs.AsNoTracking().ToListAsync())
            .OrderBy(s => s.GrossProfitFrom).ToList();
    }

    public async Task<CommissionProfitSlab> AddProfitSlabAsync(CommissionProfitSlabInput input)
    {
        if (!_current.CanAdminister) throw new PostingException("You don't have permission to do this.");
        ValidateSlab(input);

        await using var db = await _dbf.CreateDbContextAsync();
        var slab = new CommissionProfitSlab
        {
            GrossProfitFrom = input.GrossProfitFrom,
            GrossProfitTo = input.GrossProfitTo,
            CommissionAdditionPercent = input.CommissionAdditionPercent,
        };
        db.CommissionProfitSlabs.Add(slab);
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return slab;
    }

    public async Task UpdateProfitSlabAsync(int id, CommissionProfitSlabInput input)
    {
        if (!_current.CanAdminister) throw new PostingException("You don't have permission to do this.");
        ValidateSlab(input);

        await using var db = await _dbf.CreateDbContextAsync();
        var slab = await db.CommissionProfitSlabs.FirstOrDefaultAsync(s => s.Id == id)
            ?? throw new PostingException("Profit slab not found.");
        slab.GrossProfitFrom = input.GrossProfitFrom;
        slab.GrossProfitTo = input.GrossProfitTo;
        slab.CommissionAdditionPercent = input.CommissionAdditionPercent;
        await JournalPoster.SaveChangesTranslatedAsync(db);
    }

    public async Task SetProfitSlabActiveAsync(int id, bool isActive)
    {
        if (!_current.CanAdminister) throw new PostingException("You don't have permission to do this.");
        await using var db = await _dbf.CreateDbContextAsync();
        var slab = await db.CommissionProfitSlabs.FirstOrDefaultAsync(s => s.Id == id)
            ?? throw new PostingException("Profit slab not found.");
        slab.IsActive = isActive;
        await JournalPoster.SaveChangesTranslatedAsync(db);
    }

    public async Task DeleteProfitSlabAsync(int id)
    {
        if (!_current.CanAdminister) throw new PostingException("You don't have permission to do this.");
        await using var db = await _dbf.CreateDbContextAsync();
        var slab = await db.CommissionProfitSlabs.FirstOrDefaultAsync(s => s.Id == id)
            ?? throw new PostingException("Profit slab not found.");
        db.CommissionProfitSlabs.Remove(slab);
        await db.SaveChangesAsync();
    }

    // ── Position rates ──

    public async Task<List<CommissionPositionRate>> GetPositionRatesAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CommissionPositionRates.AsNoTracking().OrderBy(p => p.Position).ToListAsync();
    }

    public async Task<CommissionPositionRate> AddPositionRateAsync(CommissionPositionRateInput input)
    {
        if (!_current.CanAdminister) throw new PostingException("You don't have permission to do this.");
        var position = input.Position.Trim();
        if (string.IsNullOrWhiteSpace(position)) throw new PostingException("Position is required.");
        if (input.RateMultiplier < 0) throw new PostingException("Rate multiplier cannot be negative.");

        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.CommissionPositionRates.AnyAsync(p => p.Position.ToLower() == position.ToLower()))
            throw new PostingException($"'{position}' already has a commission rate — edit it instead.");

        var rate = new CommissionPositionRate { Position = position, RateMultiplier = input.RateMultiplier };
        db.CommissionPositionRates.Add(rate);
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return rate;
    }

    public async Task UpdatePositionRateAsync(int id, CommissionPositionRateInput input)
    {
        if (!_current.CanAdminister) throw new PostingException("You don't have permission to do this.");
        var position = input.Position.Trim();
        if (string.IsNullOrWhiteSpace(position)) throw new PostingException("Position is required.");
        if (input.RateMultiplier < 0) throw new PostingException("Rate multiplier cannot be negative.");

        await using var db = await _dbf.CreateDbContextAsync();
        var rate = await db.CommissionPositionRates.FirstOrDefaultAsync(p => p.Id == id)
            ?? throw new PostingException("Position rate not found.");
        if (await db.CommissionPositionRates.AnyAsync(p => p.Id != id && p.Position.ToLower() == position.ToLower()))
            throw new PostingException($"'{position}' already has a commission rate — edit it instead.");

        rate.Position = position;
        rate.RateMultiplier = input.RateMultiplier;
        await JournalPoster.SaveChangesTranslatedAsync(db);
    }

    public async Task SetPositionRateActiveAsync(int id, bool isActive)
    {
        if (!_current.CanAdminister) throw new PostingException("You don't have permission to do this.");
        await using var db = await _dbf.CreateDbContextAsync();
        var rate = await db.CommissionPositionRates.FirstOrDefaultAsync(p => p.Id == id)
            ?? throw new PostingException("Position rate not found.");
        rate.IsActive = isActive;
        await JournalPoster.SaveChangesTranslatedAsync(db);
    }

    public async Task DeletePositionRateAsync(int id)
    {
        if (!_current.CanAdminister) throw new PostingException("You don't have permission to do this.");
        await using var db = await _dbf.CreateDbContextAsync();
        var rate = await db.CommissionPositionRates.FirstOrDefaultAsync(p => p.Id == id)
            ?? throw new PostingException("Position rate not found.");
        db.CommissionPositionRates.Remove(rate);
        await db.SaveChangesAsync();
    }

    private static void ValidatePercent(decimal percent, string label)
    {
        if (percent < 0) throw new PostingException($"{label} percent cannot be negative.");
    }

    private static void ValidateSlab(CommissionProfitSlabInput input)
    {
        if (input.GrossProfitFrom < 0) throw new PostingException("Gross Profit From cannot be negative.");
        if (input.GrossProfitTo is decimal to && to <= input.GrossProfitFrom)
            throw new PostingException("Gross Profit To must be greater than Gross Profit From.");
        if (input.CommissionAdditionPercent < 0) throw new PostingException("Commission addition percent cannot be negative.");
    }
}
