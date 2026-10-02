namespace AegisErp.Domain.Entities;

/// <summary>
/// A sales estimate / quotation. A non-posting document: it never touches the ledger. When a
/// customer accepts it, it can be converted into a sales invoice (which is what posts to the GL).
/// </summary>
public class Estimate : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>Owning company.</summary>
    public int CompanyId { get; set; }

    /// <summary>Document number, e.g. "EST-2026-0004". Unique within the company.</summary>
    public string EstimateNo { get; set; } = string.Empty;

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>The customer's trade license/legal entity this estimate is for — only relevant when
    /// <see cref="CompanySetup.ProServiceModeEnabled"/> is on. Picking one fills <see cref="ContactTrn"/>
    /// from the organization's own TRN instead of the customer's.</summary>
    public int? OrganizationId { get; set; }
    public CustomerOrganization? Organization { get; set; }

    /// <summary>Contact snapshot fields (PRO Service Mode only) — auto-filled from the selected
    /// Customer/Organization on the form but stored here so a later edit to the Customer record
    /// doesn't retroactively change a past estimate.</summary>
    public string? ContactMobile { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactTrn { get; set; }
    public string? ContactPerson { get; set; }
    public string? BillingAddressSnapshot { get; set; }

    /// <summary>Free-text reference to whatever internal request/case this estimate is for (PRO
    /// Service Mode only) — e.g. "Visa Renewal — Ahmed" — not a tracked entity, just a label.</summary>
    public string? JobRequest { get; set; }

    public DateOnly Date { get; set; }

    /// <summary>Date the quotation is valid until.</summary>
    public DateOnly ValidUntil { get; set; }

    public DocumentStatus Status { get; set; } = DocumentStatus.Draft;

    public string? Narration { get; set; }

    /// <summary>Sales invoice this estimate was converted into (set once accepted &amp; converted).</summary>
    public int? ConvertedInvoiceId { get; set; }
    public SalesInvoice? ConvertedInvoice { get; set; }

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Token backing this quotation's public "Share" link — null until first shared.</summary>
    public string? ShareToken { get; set; }

    public List<EstimateLine> Lines { get; set; } = new();

    public decimal TotalNet => Lines.Sum(l => l.Net);
    public decimal TotalVat => Lines.Sum(l => l.Vat);
    public decimal TotalGross => Lines.Sum(l => l.Gross);
}

/// <summary>A quotation line: quantity × unit price with a VAT rate, mapped to a revenue account for conversion.</summary>
public class EstimateLine
{
    public int Id { get; set; }

    public int EstimateId { get; set; }
    public Estimate Estimate { get; set; } = null!;

    public int LineNo { get; set; }
    public string Description { get; set; } = string.Empty;

    public int RevenueAccountId { get; set; }
    public Account RevenueAccount { get; set; } = null!;

    public int? CostCenterId { get; set; }
    public CostCenter? CostCenter { get; set; }

    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal VatRate { get; set; } = 0.05m;

    /// <summary>Non-taxable disbursements (PRO Service Mode only) — a government fee and a bank
    /// charge passed straight through to the customer at cost, with no VAT applied. Default 0, so
    /// every estimate saved before these fields existed has an unchanged <see cref="Net"/>.</summary>
    public decimal GovtFee { get; set; }
    public decimal BankCharge { get; set; }

    /// <summary>Employee tasked with delivering this line's PRO service (PRO Service Mode only) —
    /// carried through to the Sales Invoice on <see cref="AegisErp.Infrastructure.Services.EstimateService.ConvertToInvoiceAsync"/>.</summary>
    public int? AssignedToEmployeeId { get; set; }
    public Employee? AssignedToEmployee { get; set; }

    /// <summary>The taxable base — <see cref="UnitPrice"/> doubles as "Center Fee" in PRO Service
    /// Mode. VAT applies only to this, never to <see cref="GovtFee"/>/<see cref="BankCharge"/>.</summary>
    public decimal TaxableNet => Math.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);
    public decimal Net => TaxableNet + Math.Round(Quantity * (GovtFee + BankCharge), 2, MidpointRounding.AwayFromZero);
    public decimal Vat => Math.Round(TaxableNet * VatRate, 2, MidpointRounding.AwayFromZero);
    public decimal Gross => Net + Vat;
}
