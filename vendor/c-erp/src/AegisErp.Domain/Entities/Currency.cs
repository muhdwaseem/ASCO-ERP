namespace AegisErp.Domain.Entities;

/// <summary>
/// A currency the company transacts in, with its exchange rate against the company's base
/// currency. The base currency itself (<see cref="CompanySetup.BaseCurrency"/>) is not stored
/// here — it always has an implicit rate of 1 and is shown separately in the UI.
/// </summary>
public class Currency : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    /// <summary>ISO code, e.g. "USD".</summary>
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>How many units of the base currency one unit of this currency is worth.</summary>
    public decimal RateToBase { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime? UpdatedAtUtc { get; set; }
}
