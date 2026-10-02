namespace AegisErp.Domain.Entities;

/// <summary>
/// A named item category (e.g. "Visa Services", "Trade License") offered on the Item form's
/// Category picker, and the key the Commission Workflow's <see cref="CommissionCategoryRate"/> table
/// maps a base commission percentage onto. Mirrors <see cref="UnitOfMeasure"/> — deliberately just an
/// admin-curated label list.
/// </summary>
public class ItemCategory : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
