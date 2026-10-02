namespace AegisErp.Domain.Entities;

/// <summary>
/// A vendor payment (money out). Posting generates a GL voucher
/// (Dr Accounts Payable / Cr bank). May be allocated to a specific
/// purchase invoice or left on account.
/// </summary>
public class VendorPayment : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>Owning company.</summary>
    public int CompanyId { get; set; }

    /// <summary>Document number, e.g. "PV-2026-0045". Shared with the generated GL voucher. Unique within the company.</summary>
    public string PaymentNo { get; set; } = string.Empty;

    public int VendorId { get; set; }
    public Vendor Vendor { get; set; } = null!;

    /// <summary>
    /// Display convenience only: set when this payment's <see cref="Allocations"/> resolve to
    /// exactly one purchase invoice (null for a pure on-account payment, or when it spans multiple
    /// invoices — a single FK can't represent that). Balance/aging/outstanding calculations must
    /// always derive invoice attribution from <see cref="Allocations"/>, never from this field.
    /// </summary>
    public int? PurchaseInvoiceId { get; set; }
    public PurchaseInvoice? PurchaseInvoice { get; set; }

    public DateOnly Date { get; set; }

    public int FiscalPeriodId { get; set; }
    public FiscalPeriod FiscalPeriod { get; set; } = null!;

    /// <summary>Bank/cash account the money went out of.</summary>
    public int BankAccountId { get; set; }
    public Account BankAccount { get; set; } = null!;

    public decimal Amount { get; set; }
    public PaymentMode PaymentMode { get; set; } = PaymentMode.Cash;

    /// <summary>Cheque or other payment reference number, if any.</summary>
    public string? ReferenceNo { get; set; }
    /// <summary>Cheque date, relevant for Cheque/PostDatedCheque modes.</summary>
    public DateOnly? ChequeDate { get; set; }

    /// <summary>One supporting document attached to the payment (e.g. a scanned cheque/receipt).</summary>
    public string? AttachmentFileName { get; set; }
    public string? AttachmentContentType { get; set; }
    public byte[]? AttachmentData { get; set; }

    public VoucherStatus Status { get; set; } = VoucherStatus.Draft;
    public string? Narration { get; set; }

    public int? JournalVoucherId { get; set; }
    public JournalVoucher? JournalVoucher { get; set; }

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PostedAtUtc { get; set; }

    /// <summary>How this payment's amount is split across the invoice's individual lines/services.</summary>
    public List<VendorPaymentAllocation> Allocations { get; set; } = new();

    /// <summary>Validates the payment and transitions it to Posted. The caller generates the GL voucher.</summary>
    public void Post(DateTime nowUtc)
    {
        if (Status == VoucherStatus.Posted)
            throw new PostingException("Payment is already posted.");
        if (VendorId == 0)
            throw new PostingException("Payment has no vendor.");
        if (BankAccountId == 0)
            throw new PostingException("Payment has no bank account.");
        if (Amount <= 0)
            throw new PostingException("Payment amount must be positive.");

        Status = VoucherStatus.Posted;
        PostedAtUtc = nowUtc;
    }
}

/// <summary>
/// Records how much of a <see cref="VendorPayment"/> was applied against one specific
/// <see cref="PurchaseInvoiceLine"/> — so, on a multi-line bill, staff can see exactly which
/// charge was paid instead of only a single lump-sum balance for the whole invoice.
/// </summary>
public class VendorPaymentAllocation
{
    public int Id { get; set; }

    public int VendorPaymentId { get; set; }
    public VendorPayment VendorPayment { get; set; } = null!;

    public int PurchaseInvoiceLineId { get; set; }
    public PurchaseInvoiceLine PurchaseInvoiceLine { get; set; } = null!;

    public decimal Amount { get; set; }
}
