namespace AegisErp.Domain.Entities;

/// <summary>
/// A subsidiary asset register entry — tracks cost, depreciation and disposal for one fixed asset.
/// Does NOT itself post the original purchase: capitalizing the cost is assumed to already have
/// happened via a normal Purchase Invoice or Journal Voucher coded to <see cref="AssetAccountId"/>,
/// so creating a <see cref="FixedAsset"/> record never touches the GL. Only
/// <see cref="Services.FixedAssetService.RunDepreciationAsync"/> and
/// <see cref="Services.FixedAssetService.DisposeAsync"/> (in Infrastructure) post anything.
/// </summary>
public class FixedAsset : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    /// <summary>Auto-numbered, e.g. "FA-0001" — same "max numeric suffix + 1" convention as Customer/Vendor codes.</summary>
    public string AssetCode { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Free text (e.g. "Furniture", "IT Equipment") — no curated list for v1.</summary>
    public string? Category { get; set; }

    public DateOnly PurchaseDate { get; set; }
    public decimal PurchaseCost { get; set; }

    /// <summary>Estimated residual value at the end of its useful life — depreciation never reduces
    /// the asset's book value below this.</summary>
    public decimal SalvageValue { get; set; }

    public int UsefulLifeMonths { get; set; }

    /// <summary>The Balance Sheet asset account this asset's cost sits in.</summary>
    public int AssetAccountId { get; set; }
    public Account AssetAccount { get; set; } = null!;

    /// <summary>The P&amp;L account this asset's monthly depreciation charges to.</summary>
    public int DepreciationExpenseAccountId { get; set; }
    public Account DepreciationExpenseAccount { get; set; } = null!;

    public int? CostCenterId { get; set; }
    public CostCenter? CostCenter { get; set; }

    public FixedAssetStatus Status { get; set; } = FixedAssetStatus.Active;

    public DateOnly? DisposalDate { get; set; }
    public decimal? DisposalProceeds { get; set; }

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }

    public List<AssetDepreciationEntry> DepreciationEntries { get; set; } = new();

    /// <summary>Total depreciation posted so far across every period.</summary>
    public decimal AccumulatedDepreciation => DepreciationEntries.Sum(e => e.Amount);

    /// <summary>Cost less accumulated depreciation — never below <see cref="SalvageValue"/>.</summary>
    public decimal NetBookValue => PurchaseCost - AccumulatedDepreciation;

    /// <summary>The straight-line amount one more period of depreciation would add, capped so
    /// accumulated depreciation never exceeds PurchaseCost - SalvageValue. Zero once fully
    /// depreciated or already disposed.</summary>
    public decimal NextDepreciationAmount()
    {
        if (Status == FixedAssetStatus.Disposed || UsefulLifeMonths <= 0) return 0m;
        var depreciableBase = PurchaseCost - SalvageValue;
        var monthly = Math.Round(depreciableBase / UsefulLifeMonths, 2, MidpointRounding.AwayFromZero);
        var remaining = depreciableBase - AccumulatedDepreciation;
        if (remaining <= 0) return 0m;
        return Math.Min(monthly, remaining);
    }
}

/// <summary>One period's posted depreciation for one asset — at most one row per (FixedAssetId, FiscalPeriodId).</summary>
public class AssetDepreciationEntry
{
    public int Id { get; set; }

    public int FixedAssetId { get; set; }
    public FixedAsset FixedAsset { get; set; } = null!;

    public int FiscalPeriodId { get; set; }
    public FiscalPeriod FiscalPeriod { get; set; } = null!;

    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }

    public int? JournalVoucherId { get; set; }
    public JournalVoucher? JournalVoucher { get; set; }
}

public enum FixedAssetStatus
{
    Active = 1,
    Disposed = 2,
}
