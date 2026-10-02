namespace AegisErp.Domain.Entities;

/// <summary>
/// A reusable product or service (Zoho Books' "Items" list) that can be picked on a sales or
/// purchase document line so its price, revenue/expense account and tax code fill in from one
/// selection instead of being typed by hand on every document.
/// </summary>
public class Item : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ItemKind Kind { get; set; } = ItemKind.Service;
    public string Unit { get; set; } = "unit";

    /// <summary>Drives <see cref="CommissionCategoryRate"/> lookup in the Commission Workflow.</summary>
    public int? CategoryId { get; set; }
    public ItemCategory? Category { get; set; }

    public decimal SellingPrice { get; set; }
    public int? SalesAccountId { get; set; }
    public Account? SalesAccount { get; set; }
    public string? SalesDescription { get; set; }

    public decimal? CostPrice { get; set; }
    public int? PurchaseAccountId { get; set; }
    public Account? PurchaseAccount { get; set; }
    public string? PurchaseDescription { get; set; }

    public int? TaxCodeId { get; set; }
    public TaxCode? TaxCode { get; set; }

    public bool IsActive { get; set; } = true;
}
