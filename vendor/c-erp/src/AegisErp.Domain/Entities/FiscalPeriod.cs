namespace AegisErp.Domain.Entities;

/// <summary>An accounting period (e.g. "May 2026"). Vouchers post into exactly one period.</summary>
public class FiscalPeriod : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>Owning company. Each company runs its own financial calendar.</summary>
    public int CompanyId { get; set; }

    public string Name { get; set; } = string.Empty;
    public int Year { get; set; }
    public int PeriodNo { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public bool IsClosed { get; set; }
}
