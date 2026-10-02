namespace AegisErp.Domain.Entities;

/// <summary>
/// An AP debit note (a claim back against a vendor — returns, allowances, corrections). Posting
/// generates a balanced GL voucher (Dr Accounts Payable gross / Cr expense net per line /
/// Cr VAT input) — the mirror image of a purchase invoice. May be applied against a specific
/// purchase invoice or left on account.
/// </summary>
public class DebitNote : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>Owning company.</summary>
    public int CompanyId { get; set; }

    /// <summary>Document number, e.g. "DN-2026-0002". Shared with the generated GL voucher. Unique within the company.</summary>
    public string DebitNoteNo { get; set; } = string.Empty;

    public int VendorId { get; set; }
    public Vendor Vendor { get; set; } = null!;

    /// <summary>Purchase invoice this debit note is applied against (null = on account).</summary>
    public int? PurchaseInvoiceId { get; set; }
    public PurchaseInvoice? PurchaseInvoice { get; set; }

    public DateOnly Date { get; set; }

    public int FiscalPeriodId { get; set; }
    public FiscalPeriod FiscalPeriod { get; set; } = null!;

    public VoucherStatus Status { get; set; } = VoucherStatus.Draft;

    /// <summary>Reason for the debit note (return, allowance, correction…).</summary>
    public string? Reason { get; set; }
    public string? Narration { get; set; }

    public int? JournalVoucherId { get; set; }
    public JournalVoucher? JournalVoucher { get; set; }

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PostedAtUtc { get; set; }

    public List<DebitNoteLine> Lines { get; set; } = new();

    public decimal TotalNet => Lines.Sum(l => l.Net);
    public decimal TotalVat => Lines.Sum(l => l.Vat);
    public decimal TotalGross => Lines.Sum(l => l.Gross);

    /// <summary>Validates the debit note and transitions it to Posted. The caller generates the GL voucher.</summary>
    public void Post(DateTime nowUtc)
    {
        if (Status == VoucherStatus.Posted)
            throw new PostingException("Debit note is already posted.");
        if (VendorId == 0)
            throw new PostingException("Debit note has no vendor.");
        if (Lines.Count == 0)
            throw new PostingException("Debit note needs at least one line.");

        foreach (var line in Lines)
        {
            if (line.ExpenseAccountId == 0)
                throw new PostingException($"Line {line.LineNo} has no expense/asset account.");
            if (line.Quantity <= 0)
                throw new PostingException($"Line {line.LineNo} quantity must be positive.");
            if (line.UnitPrice < 0)
                throw new PostingException($"Line {line.LineNo} unit price cannot be negative.");
            if (line.VatRate is < 0 or > 1)
                throw new PostingException($"Line {line.LineNo} VAT rate is invalid.");
        }

        if (TotalGross <= 0)
            throw new PostingException("Debit note total must be positive.");

        Status = VoucherStatus.Posted;
        PostedAtUtc = nowUtc;
    }
}

/// <summary>A debit note line: quantity × unit price with a VAT rate, credited back off an expense/asset account.</summary>
public class DebitNoteLine
{
    public int Id { get; set; }

    public int DebitNoteId { get; set; }
    public DebitNote DebitNote { get; set; } = null!;

    public int LineNo { get; set; }
    public string Description { get; set; } = string.Empty;

    public int ExpenseAccountId { get; set; }
    public Account ExpenseAccount { get; set; } = null!;

    public int? CostCenterId { get; set; }
    public CostCenter? CostCenter { get; set; }

    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal VatRate { get; set; } = 0.05m;

    public decimal Net => Math.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);
    public decimal Vat => Math.Round(Net * VatRate, 2, MidpointRounding.AwayFromZero);
    public decimal Gross => Net + Vat;
}
