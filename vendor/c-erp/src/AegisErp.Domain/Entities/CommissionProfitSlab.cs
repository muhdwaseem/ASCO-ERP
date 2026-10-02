namespace AegisErp.Domain.Entities;

/// <summary>
/// One Gross Profit bracket (AED) and the commission percentage it adds on top of the item
/// category's base rate. The bottom slab (e.g. 0–5,000) is expected to carry
/// <see cref="CommissionAdditionPercent"/> = 0 — that IS the "minimum profit eligibility" cut-off the
/// Commission Workflow spec calls for, so no separate threshold field is needed. Admin-editable, not
/// hard-coded.
/// </summary>
public class CommissionProfitSlab : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    public decimal GrossProfitFrom { get; set; }

    /// <summary>Null means "and above" — the top-open-ended slab.</summary>
    public decimal? GrossProfitTo { get; set; }

    public decimal CommissionAdditionPercent { get; set; }
    public bool IsActive { get; set; } = true;
}
