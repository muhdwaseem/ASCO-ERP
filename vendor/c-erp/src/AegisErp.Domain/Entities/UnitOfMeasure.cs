namespace AegisErp.Domain.Entities;

/// <summary>
/// A named unit of measure (e.g. "pcs", "kg", "hrs") offered on the Item form's Unit picker.
/// Deliberately just a label list — <see cref="Item.Unit"/> stores the chosen name directly as a
/// plain string rather than a foreign key, so this exists only to drive that dropdown (and the
/// "Units" admin page) instead of the free-text/hardcoded list used before.
/// </summary>
public class UnitOfMeasure : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
