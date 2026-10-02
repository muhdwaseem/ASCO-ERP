namespace AegisErp.Domain.Entities;

/// <summary>
/// A reusable named bundle of predefined lines (e.g. "New Company Formation Kit") that a PRO
/// services company can insert onto an Estimate or Sales Invoice in one action instead of typing
/// the same government/center/bank fee lines out by hand every time. Only relevant when
/// <see cref="CompanySetup.ProServiceModeEnabled"/> is on.
/// </summary>
public class ServiceKit : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    public List<ServiceKitLine> Lines { get; set; } = new();
}

/// <summary>One predefined line within a <see cref="ServiceKit"/> — the same Govt/Center/Bank fee
/// shape as <see cref="EstimateLine"/>/<see cref="SalesInvoiceLine"/>, stored as a template rather
/// than posted anywhere itself.</summary>
public class ServiceKitLine
{
    public int Id { get; set; }

    public int ServiceKitId { get; set; }
    public ServiceKit ServiceKit { get; set; } = null!;

    public int SortOrder { get; set; }
    public string Description { get; set; } = string.Empty;

    /// <summary>Revenue account this line should post to when inserted — optional so a kit can be
    /// saved before every account is finalized; the user picks one on the document if left blank.</summary>
    public int? RevenueAccountId { get; set; }
    public Account? RevenueAccount { get; set; }

    public decimal GovtFee { get; set; }
    public decimal CenterFee { get; set; }
    public decimal BankCharge { get; set; }
    public decimal VatRate { get; set; } = 0.05m;
}
