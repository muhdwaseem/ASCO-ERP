namespace AegisErp.Domain.Entities;

/// <summary>A reusable VAT/tax code that can be applied to sales and purchase lines.</summary>
public class TaxCode : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Rate { get; set; }
    public VatTaxType TaxType { get; set; } = VatTaxType.StandardRated;

    /// <summary>Legacy single-account mapping, superseded by <see cref="OutputAccountId"/>/
    /// <see cref="InputAccountId"/> — kept read-only in the UI for reference, never written to by
    /// new code.</summary>
    public int? GlAccountId { get; set; }
    public Account? GlAccount { get; set; }

    /// <summary>GL account this code credits when charged on a sale (Output VAT).</summary>
    public int? OutputAccountId { get; set; }
    public Account? OutputAccount { get; set; }

    /// <summary>GL account this code debits when paid on a purchase (Input VAT).</summary>
    public int? InputAccountId { get; set; }
    public Account? InputAccount { get; set; }

    public DateOnly EffectiveFrom { get; set; }
    public bool IsActive { get; set; } = true;
}
