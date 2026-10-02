namespace AegisErp.Domain.Entities;

/// <summary>
/// The commission-rate multiplier for one employee position/designation (e.g. Sales Executive ×1.0,
/// Sales Manager ×1.5), applied on top of the combined category+profit-slab rate when calculating an
/// Employee's commission. <see cref="Position"/> is matched case-insensitively against
/// <see cref="Employee.Designation"/> — that field is free text, not an enum/FK, so the admin UI for
/// this table should offer a dropdown of the company's existing Designation values rather than free
/// text, to avoid typo-mismatches. Admin-editable, not hard-coded.
/// </summary>
public class CommissionPositionRate : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    public string Position { get; set; } = string.Empty;
    public decimal RateMultiplier { get; set; } = 1.0m;
    public bool IsActive { get; set; } = true;
}
