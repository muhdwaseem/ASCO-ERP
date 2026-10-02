namespace AegisErp.Domain.Entities;

/// <summary>
/// A recurring billing schedule for one customer, seeded from an existing Sales Invoice ("Make
/// Recurring"). The background service (<c>InvoiceAutomationHostedService</c>) generates a new
/// Draft invoice from this profile whenever <see cref="NextGenerationDate"/> is reached — never
/// auto-posted, since nothing touches the ledger in this app without a human posting it.
/// </summary>
public class RecurringInvoiceProfile : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public RecurringFrequency Frequency { get; set; } = RecurringFrequency.Monthly;

    /// <summary>Repeat every N units of <see cref="Frequency"/>, e.g. 2 + Monthly = every 2 months.</summary>
    public int RepeatEvery { get; set; } = 1;

    public DateOnly StartDate { get; set; }

    /// <summary>Null = repeats indefinitely until paused/deleted.</summary>
    public DateOnly? EndDate { get; set; }

    public DateOnly NextGenerationDate { get; set; }

    public bool IsActive { get; set; } = true;

    public string? Narration { get; set; }

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }

    public List<RecurringInvoiceProfileLine> Lines { get; set; } = new();
}

/// <summary>One line template on a recurring profile — same shape as a sales invoice line input,
/// re-applied verbatim (quantities/prices don't change automatically between runs).</summary>
public class RecurringInvoiceProfileLine
{
    public int Id { get; set; }
    public int RecurringInvoiceProfileId { get; set; }
    public RecurringInvoiceProfile RecurringInvoiceProfile { get; set; } = null!;

    public int LineNo { get; set; }
    public string Description { get; set; } = string.Empty;
    public int RevenueAccountId { get; set; }
    public Account RevenueAccount { get; set; } = null!;
    public int? CostCenterId { get; set; }
    public CostCenter? CostCenter { get; set; }
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal VatRate { get; set; } = 0.05m;
}
