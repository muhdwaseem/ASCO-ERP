namespace AegisErp.Domain.Entities;

/// <summary>
/// The base commission percentage for one <see cref="ItemCategory"/> — the starting rate before the
/// profit-slab addition (<see cref="CommissionProfitSlab"/>) and position multiplier
/// (<see cref="CommissionPositionRate"/>) are applied. Admin-editable, not hard-coded, per the
/// Commission Workflow spec.
/// </summary>
public class CommissionCategoryRate : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    public int ItemCategoryId { get; set; }
    public ItemCategory ItemCategory { get; set; } = null!;

    public decimal BaseCommissionPercent { get; set; }
    public bool IsActive { get; set; } = true;
}
