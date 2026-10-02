namespace AegisErp.Domain.Entities;

/// <summary>
/// A customer receipt (money in). Posting generates a GL voucher
/// (Dr bank / Cr Accounts Receivable). May be allocated to a specific
/// invoice or left on account.
/// </summary>
public class CustomerReceipt : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>Owning company.</summary>
    public int CompanyId { get; set; }

    /// <summary>Document number, e.g. "RV-2026-0028". Shared with the generated GL voucher. Unique within the company.</summary>
    public string ReceiptNo { get; set; } = string.Empty;

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>
    /// Display convenience only: set when this receipt's <see cref="Allocations"/> resolve to
    /// exactly one invoice (null for a pure on-account receipt, or when it spans multiple
    /// invoices — a single FK can't represent that). Balance/aging/outstanding calculations must
    /// always derive invoice attribution from <see cref="Allocations"/>, never from this field.
    /// </summary>
    public int? SalesInvoiceId { get; set; }
    public SalesInvoice? SalesInvoice { get; set; }

    public DateOnly Date { get; set; }

    public int FiscalPeriodId { get; set; }
    public FiscalPeriod FiscalPeriod { get; set; } = null!;

    /// <summary>Bank/cash account the money landed in.</summary>
    public int BankAccountId { get; set; }
    public Account BankAccount { get; set; } = null!;

    public decimal Amount { get; set; }
    public PaymentMode PaymentMode { get; set; } = PaymentMode.Cash;

    /// <summary>Cheque or other payment reference number, if any.</summary>
    public string? ReferenceNo { get; set; }
    /// <summary>Cheque date, relevant for Cheque/PostDatedCheque modes.</summary>
    public DateOnly? ChequeDate { get; set; }

    /// <summary>One supporting document attached to the receipt (e.g. a scanned cheque).</summary>
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

    /// <summary>How this receipt's amount is split across the invoice's individual lines/services.</summary>
    public List<ReceiptAllocation> Allocations { get; set; } = new();

    /// <summary>Validates the receipt and transitions it to Posted. The caller generates the GL voucher.</summary>
    public void Post(DateTime nowUtc)
    {
        if (Status == VoucherStatus.Posted)
            throw new PostingException("Receipt is already posted.");
        if (CustomerId == 0)
            throw new PostingException("Receipt has no customer.");
        if (BankAccountId == 0)
            throw new PostingException("Receipt has no bank account.");
        if (Amount <= 0)
            throw new PostingException("Receipt amount must be positive.");

        Status = VoucherStatus.Posted;
        PostedAtUtc = nowUtc;
    }
}

/// <summary>
/// Records how much of a <see cref="CustomerReceipt"/> was applied against one specific
/// <see cref="SalesInvoiceLine"/> — so, on a multi-service invoice, staff can see exactly which
/// service was paid instead of only a single lump-sum balance for the whole invoice.
/// </summary>
public class ReceiptAllocation
{
    public int Id { get; set; }

    public int CustomerReceiptId { get; set; }
    public CustomerReceipt CustomerReceipt { get; set; } = null!;

    public int SalesInvoiceLineId { get; set; }
    public SalesInvoiceLine SalesInvoiceLine { get; set; } = null!;

    public decimal Amount { get; set; }
}
