namespace AegisErp.Domain.Entities;

/// <summary>
/// An AR credit note (money owed back to / off a customer). Posting generates a balanced GL
/// voucher (Dr revenue net per line / Dr VAT payable / Cr Accounts Receivable gross) — the
/// mirror image of a sales invoice. May be applied against a specific invoice or left on account.
/// </summary>
public class CreditNote : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>Owning company.</summary>
    public int CompanyId { get; set; }

    /// <summary>Document number, e.g. "CN-2026-0003". Shared with the generated GL voucher. Unique within the company.</summary>
    public string CreditNoteNo { get; set; } = string.Empty;

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>Invoice this credit note is applied against (null = on account).</summary>
    public int? SalesInvoiceId { get; set; }
    public SalesInvoice? SalesInvoice { get; set; }

    public DateOnly Date { get; set; }

    public int FiscalPeriodId { get; set; }
    public FiscalPeriod FiscalPeriod { get; set; } = null!;

    public VoucherStatus Status { get; set; } = VoucherStatus.Draft;

    /// <summary>Reason for the credit (return, discount, correction…).</summary>
    public string? Reason { get; set; }
    public string? Narration { get; set; }

    /// <summary>How this credit settles — reduce the invoice's balance, leave a credit on
    /// account, or refund the customer in cash immediately.</summary>
    public CreditNoteSettlementMethod SettlementMethod { get; set; } = CreditNoteSettlementMethod.CreditOnAccount;

    /// <summary>Bank/cash GL account the refund was paid from. Only set for <see cref="CreditNoteSettlementMethod.CashRefund"/>.</summary>
    public int? BankAccountId { get; set; }
    public Account? BankAccount { get; set; }

    public int? JournalVoucherId { get; set; }
    public JournalVoucher? JournalVoucher { get; set; }

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PostedAtUtc { get; set; }

    public List<CreditNoteLine> Lines { get; set; } = new();

    /// <summary>
    /// How this on-account credit note's balance has been applied to invoices after the fact —
    /// see <see cref="CreditNoteAllocation"/>. Only ever populated for
    /// <see cref="CreditNoteSettlementMethod.CreditOnAccount"/> notes; one created directly
    /// against an invoice (<see cref="CreditNoteSettlementMethod.ApplyToInvoice"/>) already fully
    /// consumed itself via <see cref="SalesInvoiceId"/> at posting time and never gets allocations.
    /// </summary>
    public List<CreditNoteAllocation> Allocations { get; set; } = new();

    public decimal TotalNet => Lines.Sum(l => l.Net);
    public decimal TotalVat => Lines.Sum(l => l.Vat);
    public decimal TotalGross => Lines.Sum(l => l.Gross);

    /// <summary>Validates the credit note and transitions it to Posted. The caller generates the GL voucher.</summary>
    public void Post(DateTime nowUtc)
    {
        if (Status == VoucherStatus.Posted)
            throw new PostingException("Credit note is already posted.");
        if (CustomerId == 0)
            throw new PostingException("Credit note has no customer.");
        if (Lines.Count == 0)
            throw new PostingException("Credit note needs at least one line.");

        foreach (var line in Lines)
        {
            if (line.RevenueAccountId == 0)
                throw new PostingException($"Line {line.LineNo} has no revenue account.");
            if (line.Quantity <= 0)
                throw new PostingException($"Line {line.LineNo} quantity must be positive.");
            if (line.UnitPrice < 0)
                throw new PostingException($"Line {line.LineNo} unit price cannot be negative.");
            if (line.VatRate is < 0 or > 1)
                throw new PostingException($"Line {line.LineNo} VAT rate is invalid.");
        }

        if (TotalGross <= 0)
            throw new PostingException("Credit note total must be positive.");

        Status = VoucherStatus.Posted;
        PostedAtUtc = nowUtc;
    }
}

/// <summary>
/// One application of an on-account credit note's balance against a specific invoice — the
/// Zoho-style "Apply Credit" action. A credit note can be split across several invoices over
/// time (unlike <see cref="CreditNote.SalesInvoiceId"/>, which only ever links one invoice, fixed
/// at posting). No new GL entry is posted here — the credit already hit Accounts Receivable when
/// the note itself was posted; this only records which invoice(s) it's been matched against.
/// </summary>
public class CreditNoteAllocation
{
    public int Id { get; set; }

    public int CreditNoteId { get; set; }
    public CreditNote CreditNote { get; set; } = null!;

    public int SalesInvoiceId { get; set; }
    public SalesInvoice SalesInvoice { get; set; } = null!;

    public decimal Amount { get; set; }
    public DateTime AllocatedAtUtc { get; set; }
    public string AllocatedBy { get; set; } = "System Admin";
}

/// <summary>A credit note line: quantity × unit price with a VAT rate, credited back off a revenue account.</summary>
public class CreditNoteLine
{
    public int Id { get; set; }

    public int CreditNoteId { get; set; }
    public CreditNote CreditNote { get; set; } = null!;

    public int LineNo { get; set; }
    public string Description { get; set; } = string.Empty;

    public int RevenueAccountId { get; set; }
    public Account RevenueAccount { get; set; } = null!;

    public int? CostCenterId { get; set; }
    public CostCenter? CostCenter { get; set; }

    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal VatRate { get; set; } = 0.05m;

    public decimal Net => Math.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);
    public decimal Vat => Math.Round(Net * VatRate, 2, MidpointRounding.AwayFromZero);
    public decimal Gross => Net + Vat;
}
