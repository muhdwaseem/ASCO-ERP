namespace AegisErp.Domain.Entities;

/// <summary>
/// A delivery note (goods dispatched to a customer). A non-posting document: it records what was
/// delivered and when, and may reference the sales invoice it relates to. It never touches the ledger.
/// </summary>
public class DeliveryNote : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>Owning company.</summary>
    public int CompanyId { get; set; }

    /// <summary>Document number, e.g. "DLV-2026-0005". Unique within the company.</summary>
    public string DeliveryNoteNo { get; set; } = string.Empty;

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>Sales invoice this delivery relates to (optional).</summary>
    public int? SalesInvoiceId { get; set; }
    public SalesInvoice? SalesInvoice { get; set; }

    public DateOnly Date { get; set; }

    /// <summary>Where the goods are being delivered.</summary>
    public string? DeliveryAddress { get; set; }

    public DocumentStatus Status { get; set; } = DocumentStatus.Draft;

    public string? Narration { get; set; }

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }

    public List<DeliveryNoteLine> Lines { get; set; } = new();

    public decimal TotalQuantity => Lines.Sum(l => l.Quantity);
}

/// <summary>A delivery note line: an item/description and the quantity delivered (no pricing — non-financial).</summary>
public class DeliveryNoteLine
{
    public int Id { get; set; }

    public int DeliveryNoteId { get; set; }
    public DeliveryNote DeliveryNote { get; set; } = null!;

    public int LineNo { get; set; }
    public string Description { get; set; } = string.Empty;

    /// <summary>Unit of measure, e.g. "pcs", "box", "kg" (optional).</summary>
    public string? Unit { get; set; }

    public decimal Quantity { get; set; } = 1;
}
