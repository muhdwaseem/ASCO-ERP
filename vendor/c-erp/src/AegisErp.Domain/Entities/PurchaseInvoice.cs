namespace AegisErp.Domain.Entities;

/// <summary>
/// An AP purchase (vendor) invoice. Posting it generates a balanced GL voucher
/// (Dr expense/asset net per line / Dr VAT input / Cr Accounts Payable gross).
/// </summary>
public class PurchaseInvoice : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>Owning company.</summary>
    public int CompanyId { get; set; }

    /// <summary>Document number, e.g. "PINV-2026-0007". Shared with the generated GL voucher. Unique within the company.</summary>
    public string InvoiceNo { get; set; } = string.Empty;

    /// <summary>The vendor's own invoice/bill reference (optional).</summary>
    public string? VendorRef { get; set; }

    /// <summary>Our own Local Purchase Order number this invoice bills against, if any.</summary>
    public string? LpoNo { get; set; }

    public int VendorId { get; set; }
    public Vendor Vendor { get; set; } = null!;

    /// <summary>One Cost Center for the whole bill — only used by the "PRO Service" invoice
    /// format (<see cref="CompanySetup.ProServiceModeEnabled"/>). The standard format leaves
    /// this null and keeps each line's own <see cref="PurchaseInvoiceLine.CostCenterId"/> instead.</summary>
    public int? CostCenterId { get; set; }
    public CostCenter? CostCenter { get; set; }

    public DateOnly Date { get; set; }
    public DateOnly DueDate { get; set; }

    public int FiscalPeriodId { get; set; }
    public FiscalPeriod FiscalPeriod { get; set; } = null!;

    public VoucherStatus Status { get; set; } = VoucherStatus.Draft;

    public string? Narration { get; set; }

    /// <summary>Internal note about this bill (payment arrangement, dispute, etc.) — distinct from
    /// <see cref="Narration"/>, which is the GL voucher description.</summary>
    public string? Notes { get; set; }

    /// <summary>One supporting document attached to the invoice (e.g. the vendor's bill/receipt).</summary>
    public string? AttachmentFileName { get; set; }
    public string? AttachmentContentType { get; set; }
    public byte[]? AttachmentData { get; set; }

    /// <summary>The GL voucher generated when this invoice was posted.</summary>
    public int? JournalVoucherId { get; set; }
    public JournalVoucher? JournalVoucher { get; set; }

    /// <summary>The Sales Invoice line this bill was created from, via the Transactions
    /// "Complete → Supplier" flow (see <c>TransactionService.CompleteSupplierAsync</c>). Null for
    /// every ordinary purchase invoice.</summary>
    public int? SourceSalesInvoiceLineId { get; set; }
    public SalesInvoiceLine? SourceSalesInvoiceLine { get; set; }

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PostedAtUtc { get; set; }

    public List<PurchaseInvoiceLine> Lines { get; set; } = new();

    public decimal TotalNet => Lines.Sum(l => l.Net);
    public decimal TotalVat => Lines.Sum(l => l.Vat);
    public decimal TotalGross => Lines.Sum(l => l.Gross);

    /// <summary>Validates the invoice and transitions it to Posted. The caller generates the GL voucher.</summary>
    public void Post(DateTime nowUtc)
    {
        if (Status == VoucherStatus.Posted)
            throw new PostingException("Purchase invoice is already posted.");
        if (VendorId == 0)
            throw new PostingException("Purchase invoice has no vendor.");
        if (Lines.Count == 0)
            throw new PostingException("Purchase invoice needs at least one line.");
        if (DueDate < Date)
            throw new PostingException("Due date cannot be before the invoice date.");

        foreach (var line in Lines)
        {
            if (line.ExpenseAccountId == 0)
                throw new PostingException($"Line {line.LineNo} has no expense/asset account.");
            if (line.Quantity <= 0)
                throw new PostingException($"Line {line.LineNo} quantity must be positive.");
            if (line.UnitPrice < 0)
                throw new PostingException($"Line {line.LineNo} unit price cannot be negative.");
            if (line.NonTaxableAmount < 0)
                throw new PostingException($"Line {line.LineNo} non-taxable amount cannot be negative.");
            if (line.VatRate is < 0 or > 1)
                throw new PostingException($"Line {line.LineNo} VAT rate is invalid.");
        }

        if (TotalGross <= 0)
            throw new PostingException("Purchase invoice total must be positive.");

        Status = VoucherStatus.Posted;
        PostedAtUtc = nowUtc;
    }
}

/// <summary>A purchase invoice line: quantity × unit price with a VAT rate, posted to an expense/asset account.</summary>
public class PurchaseInvoiceLine
{
    public int Id { get; set; }

    public int PurchaseInvoiceId { get; set; }
    public PurchaseInvoice PurchaseInvoice { get; set; } = null!;

    public int LineNo { get; set; }
    public string Description { get; set; } = string.Empty;

    /// <summary>The expense or asset account this line is charged to.</summary>
    public int ExpenseAccountId { get; set; }
    public Account ExpenseAccount { get; set; } = null!;

    public int? CostCenterId { get; set; }
    public CostCenter? CostCenter { get; set; }

    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }

    /// <summary>Fractional VAT rate, e.g. 0.05 for UAE 5%.</summary>
    public decimal VatRate { get; set; } = 0.05m;

    /// <summary>An amount on this line that carries no VAT at all (e.g. a government fee
    /// disbursement) — only used by the "PRO Service" invoice format
    /// (<see cref="CompanySetup.ProServiceModeEnabled"/>); zero for every line entered the
    /// standard Quantity × Unit Price way.</summary>
    public decimal NonTaxableAmount { get; set; }

    /// <summary>Whether <see cref="NonTaxableAmount"/> is a per-unit rate (multiplied by
    /// <see cref="Quantity"/>, matching how Sales Invoice's GovtFee/BankCharge always worked) or a
    /// flat one-time amount regardless of quantity. Every line created before this field existed
    /// used the flat interpretation (defaults to <c>false</c> so their already-posted GL totals
    /// never silently change); every line created since defaults to <c>true</c> so a multi-quantity
    /// PRO Service bill (e.g. 3 visa stamps at one govt fee each) books the correct total.</summary>
    public bool NonTaxablePerUnit { get; set; }

    /// <summary>Quantity × Unit Price, rounded — the taxable base VAT is charged on. Never
    /// includes <see cref="NonTaxableAmount"/>.</summary>
    private decimal TaxableNet => Math.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);

    /// <summary><see cref="NonTaxableAmount"/> after applying <see cref="NonTaxablePerUnit"/> —
    /// the actual non-taxable amount this line contributes. Use this everywhere instead of the
    /// raw field so quantity is never silently dropped (e.g. invoice breakdown footers).</summary>
    public decimal NonTaxableTotal => NonTaxablePerUnit
        ? Math.Round(Quantity * NonTaxableAmount, 2, MidpointRounding.AwayFromZero)
        : NonTaxableAmount;

    /// <summary>Taxable base plus whatever's non-taxable on this line.</summary>
    public decimal Net => TaxableNet + NonTaxableTotal;
    public decimal Vat => Math.Round(TaxableNet * VatRate, 2, MidpointRounding.AwayFromZero);
    public decimal Gross => Net + Vat;

    /// <summary>Fulfillment tracking for the Expense Transactions view — has this purchased
    /// item/service actually been received, independent of whether it's been paid for.</summary>
    public bool IsCompleted { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? CompletedBy { get; set; }
}
