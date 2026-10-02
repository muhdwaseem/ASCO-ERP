using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

public record FixedAssetInput(
    string Name, string? Category, DateOnly PurchaseDate, decimal PurchaseCost, decimal SalvageValue,
    int UsefulLifeMonths, int AssetAccountId, int DepreciationExpenseAccountId, int? CostCenterId);

public class FixedAssetService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;
    public FixedAssetService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    public async Task<List<FixedAsset>> GetAllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.FixedAssets.AsNoTracking()
            .Include(a => a.AssetAccount).Include(a => a.DepreciationExpenseAccount).Include(a => a.CostCenter)
            .Include(a => a.DepreciationEntries)
            .OrderBy(a => a.AssetCode)
            .ToListAsync();
    }

    public async Task<FixedAsset?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.FixedAssets.AsNoTracking()
            .Include(a => a.AssetAccount).Include(a => a.DepreciationExpenseAccount).Include(a => a.CostCenter)
            .Include(a => a.DepreciationEntries).ThenInclude(e => e.FiscalPeriod)
            .FirstOrDefaultAsync(a => a.Id == id);
    }

    /// <summary>
    /// Registers a fixed asset. Purely administrative — does NOT post to the GL. Capitalizing the
    /// purchase cost is assumed to already have happened via a normal Purchase Invoice or Journal
    /// Voucher coded to <paramref name="input"/>'s <see cref="FixedAssetInput.AssetAccountId"/>;
    /// this record is a subsidiary tracking register on top of that, not a second posting of it.
    /// </summary>
    public async Task<FixedAsset> CreateAsync(FixedAssetInput input, string createdBy, DateTime nowUtc)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        if (string.IsNullOrWhiteSpace(input.Name)) throw new PostingException("Asset name is required.");
        if (input.PurchaseCost <= 0) throw new PostingException("Purchase cost must be positive.");
        if (input.SalvageValue < 0) throw new PostingException("Salvage value cannot be negative.");
        if (input.SalvageValue >= input.PurchaseCost) throw new PostingException("Salvage value must be less than purchase cost.");
        if (input.UsefulLifeMonths <= 0) throw new PostingException("Useful life must be at least 1 month.");

        await using var db = await _dbf.CreateDbContextAsync();

        // Next code = max numeric suffix + 1, same convention as Customer/Vendor codes.
        var codes = await db.FixedAssets.Select(a => a.AssetCode).ToListAsync();
        var max = 0;
        foreach (var code in codes)
            if (code.StartsWith("FA-") && int.TryParse(code.AsSpan(3), out var n) && n > max)
                max = n;

        var asset = new FixedAsset
        {
            AssetCode = $"FA-{max + 1:0000}",
            Name = input.Name.Trim(),
            Category = string.IsNullOrWhiteSpace(input.Category) ? null : input.Category.Trim(),
            PurchaseDate = input.PurchaseDate,
            PurchaseCost = input.PurchaseCost,
            SalvageValue = input.SalvageValue,
            UsefulLifeMonths = input.UsefulLifeMonths,
            AssetAccountId = input.AssetAccountId,
            DepreciationExpenseAccountId = input.DepreciationExpenseAccountId,
            CostCenterId = input.CostCenterId,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
        };

        db.FixedAssets.Add(asset);
        await db.SaveChangesAsync();
        return asset;
    }

    /// <summary>
    /// Posts one period's straight-line depreciation for every Active asset that hasn't already
    /// been depreciated for <paramref name="fiscalPeriodId"/> — one combined <see cref="JournalVoucher"/>
    /// for the whole run (Dr each asset's own Depreciation Expense account / Cr the shared
    /// AccumulatedDepreciation control account), not one voucher per asset. Returns null if no
    /// asset was due (e.g. everything's already posted, or every asset is fully depreciated) —
    /// a zero-line voucher would fail <see cref="JournalVoucher"/>'s own validation anyway.
    /// Safe to call repeatedly for the same period: the DB has a unique (FixedAssetId,
    /// FiscalPeriodId) index, and this method also checks first so a repeat call is a no-op.
    /// </summary>
    public async Task<JournalVoucher?> RunDepreciationAsync(int fiscalPeriodId, string postedBy, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var period = await db.FiscalPeriods.FindAsync(fiscalPeriodId)
            ?? throw new PostingException("Fiscal period not found.");

        // Route through the company-scoped FixedAssets set (not db.AssetDepreciationEntries
        // directly) so a fiscalPeriodId can never surface another company's already-posted rows —
        // AssetDepreciationEntry has no CompanyId/filter of its own, same reasoning as every other
        // line-level entity in this codebase.
        var alreadyPostedAssetIds = (await db.FixedAssets.SelectMany(a => a.DepreciationEntries)
            .Where(e => e.FiscalPeriodId == fiscalPeriodId)
            .Select(e => e.FixedAssetId)
            .ToListAsync())
            .ToHashSet();

        var assets = await db.FixedAssets
            .Where(a => a.Status == FixedAssetStatus.Active && !alreadyPostedAssetIds.Contains(a.Id))
            .ToListAsync();

        var due = assets.Select(a => (Asset: a, Amount: a.NextDepreciationAmount()))
            .Where(x => x.Amount > 0).ToList();
        if (due.Count == 0) return null;

        var lines = due.Select(x => new VoucherLineInput(
            x.Asset.DepreciationExpenseAccountId, x.Asset.CostCenterId,
            $"{x.Asset.AssetCode} — {x.Asset.Name} — {period.Name} depreciation", x.Amount, 0)).ToList();

        // Credit total is the sum of the already-rounded per-asset debit amounts — never an
        // independently recomputed figure — so the voucher can never be a cent off balanced.
        var total = lines.Sum(l => l.Debit);
        var accDep = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.AccumulatedDepreciation);
        lines.Add(new VoucherLineInput(accDep.Id, null, $"Accumulated depreciation — {period.Name}", 0, total));

        var voucher = await JournalPoster.PostAsync(db, _current.CanPost, VoucherType.Journal, null, period.EndDate, fiscalPeriodId,
            $"Depreciation run — {period.Name}", null, postedBy, lines, nowUtc);

        foreach (var (asset, amount) in due)
            db.AssetDepreciationEntries.Add(new AssetDepreciationEntry
            {
                FixedAssetId = asset.Id,
                FiscalPeriodId = fiscalPeriodId,
                Date = period.EndDate,
                Amount = amount,
                JournalVoucher = voucher,
            });

        await JournalPoster.SaveAndCommitAsync(db, tx);
        return voucher;
    }

    /// <summary>
    /// Retires an asset (sale or scrap): Dr the bank/cash account for any proceeds received, Dr
    /// Accumulated Depreciation to clear it, Cr the asset's own account for its full original cost,
    /// and a balancing gain (Cr) or loss (Dr) to <paramref name="gainLossAccountId"/> — gain credits,
    /// loss debits, same normal-balance direction as revenue/expense. Reviewed with the Bookkeeper
    /// &amp; Controller agent: verified the four-line shape ties out exactly in both the gain and
    /// loss case. Does not itself post a final partial-period depreciation charge — if the asset is
    /// due for depreciation in its disposal period, run that first; skipping it just folds the
    /// un-charged amount into the gain/loss instead of Depreciation Expense (a documented v1
    /// limitation, not a bug).
    /// </summary>
    public async Task<JournalVoucher> DisposeAsync(
        int assetId, DateOnly disposalDate, decimal proceeds, int? bankAccountId, int gainLossAccountId,
        string postedBy, DateTime nowUtc)
    {
        if (proceeds < 0) throw new PostingException("Disposal proceeds cannot be negative.");
        if (proceeds > 0 && bankAccountId is null)
            throw new PostingException("Select the bank/cash account the disposal proceeds were received into.");

        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var asset = await db.FixedAssets.Include(a => a.DepreciationEntries).FirstOrDefaultAsync(a => a.Id == assetId)
            ?? throw new PostingException("Asset not found.");
        if (asset.Status == FixedAssetStatus.Disposed)
            throw new PostingException("This asset has already been disposed.");

        var period = await db.FiscalPeriods.FirstOrDefaultAsync(p => disposalDate >= p.StartDate && disposalDate <= p.EndDate)
            ?? throw new PostingException("No fiscal period covers the disposal date.");

        var accumulatedDepreciation = asset.AccumulatedDepreciation;
        var netBookValue = asset.PurchaseCost - accumulatedDepreciation;
        var gain = proceeds - netBookValue; // > 0 gain (credit), < 0 loss (debit), never both

        var lines = new List<VoucherLineInput>();
        if (proceeds > 0)
            lines.Add(new VoucherLineInput(bankAccountId!.Value, null, $"Disposal proceeds — {asset.AssetCode}", proceeds, 0));
        if (accumulatedDepreciation > 0)
        {
            var accDep = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.AccumulatedDepreciation);
            lines.Add(new VoucherLineInput(accDep.Id, null, $"Clear accumulated depreciation — {asset.AssetCode}", accumulatedDepreciation, 0));
        }
        lines.Add(new VoucherLineInput(asset.AssetAccountId, asset.CostCenterId, $"Disposal — {asset.AssetCode}", 0, asset.PurchaseCost));
        if (gain > 0)
            lines.Add(new VoucherLineInput(gainLossAccountId, null, $"Gain on disposal — {asset.AssetCode}", 0, gain));
        else if (gain < 0)
            lines.Add(new VoucherLineInput(gainLossAccountId, null, $"Loss on disposal — {asset.AssetCode}", -gain, 0));

        var voucher = await JournalPoster.PostAsync(db, _current.CanPost, VoucherType.Journal, null, disposalDate, period.Id,
            $"Disposal — {asset.AssetCode} — {asset.Name}", null, postedBy, lines, nowUtc);

        asset.Status = FixedAssetStatus.Disposed;
        asset.DisposalDate = disposalDate;
        asset.DisposalProceeds = proceeds;

        await JournalPoster.SaveAndCommitAsync(db, tx);
        return voucher;
    }
}
