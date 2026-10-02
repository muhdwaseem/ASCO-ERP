namespace AegisErp.Domain.Entities;

/// <summary>
/// A direct cash/bank expense (Dr Expense account(s) / Cr bank), posted in one step with no
/// vendor invoice or Accounts Payable cycle involved — e.g. fuel, a taxi, a debit-card purchase.
/// Vendor and Customer are both optional: Vendor is just a record of who was paid (no payable is
/// created), and Customer is a "billable to" tag with no automated re-billing behavior.
/// </summary>
public class DirectExpense : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>Owning company.</summary>
    public int CompanyId { get; set; }

    /// <summary>Document number, e.g. "EXP-2026-0001". Shared with the generated GL voucher. Unique within the company.</summary>
    public string ExpenseNo { get; set; } = string.Empty;

    /// <summary>Who was paid, if relevant to record — optional, since this never creates a payable.</summary>
    public int? VendorId { get; set; }
    public Vendor? Vendor { get; set; }

    /// <summary>Optional "billable to" tag — no automated re-billing effect.</summary>
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public DateOnly Date { get; set; }

    public int FiscalPeriodId { get; set; }
    public FiscalPeriod FiscalPeriod { get; set; } = null!;

    /// <summary>Bank/cash account the money went out of ("Paid Through") — null when
    /// <see cref="IsPayLater"/>, since nothing has been paid out yet at posting time.</summary>
    public int? BankAccountId { get; set; }
    public Account? BankAccount { get; set; }

    /// <summary>
    /// When true, this expense was posted as owing rather than settled: it credits
    /// <see cref="AegisErp.Domain.WellKnownAccounts.ExpensesPayable"/> instead of a bank account,
    /// and its lines are later paid off — in full or split across specific lines — via
    /// <c>DirectExpensePaymentService</c>. Set once at creation, never re-derived.
    /// </summary>
    public bool IsPayLater { get; set; }

    public string? Reference { get; set; }

    /// <summary>The vendor's own invoice/receipt number, if the expense has a paper or emailed
    /// receipt to trace back to — distinct from <see cref="Reference"/>, which flows into the
    /// generated GL voucher's own reference field.</summary>
    public string? VendorInvoiceNo { get; set; }

    public string? Narration { get; set; }

    /// <summary>Whether every line's <see cref="DirectExpenseLine.Amount"/> already includes VAT
    /// (the total on a receipt) or excludes it (VAT added on top) — set once for the whole
    /// expense, since a single receipt is entered the same way throughout.</summary>
    public bool AmountsIncludeVat { get; set; }

    /// <summary>One supporting document attached to the expense (e.g. a receipt photo or PDF).</summary>
    public string? AttachmentFileName { get; set; }
    public string? AttachmentContentType { get; set; }
    public byte[]? AttachmentData { get; set; }

    public VoucherStatus Status { get; set; } = VoucherStatus.Draft;

    public int? JournalVoucherId { get; set; }
    public JournalVoucher? JournalVoucher { get; set; }

    /// <summary>The Sales Invoice line this expense was created from, via the Transactions
    /// "Complete → Direct" flow (see <c>TransactionService.CompleteDirectAsync</c>). Null for
    /// every ordinary expense.</summary>
    public int? SourceSalesInvoiceLineId { get; set; }
    public SalesInvoiceLine? SourceSalesInvoiceLine { get; set; }

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PostedAtUtc { get; set; }

    public List<DirectExpenseLine> Lines { get; set; } = new();

    public decimal TotalNet => Lines.Sum(l => l.Split(AmountsIncludeVat).Net);
    public decimal TotalVat => Lines.Sum(l => l.Split(AmountsIncludeVat).Vat);

    /// <summary>Total actually paid out (Net + VAT) — what's credited to the "Paid Through" account.</summary>
    public decimal TotalAmount => Lines.Sum(l => l.Split(AmountsIncludeVat).Gross);

    /// <summary>Validates the expense and transitions it to Posted. The caller generates the GL voucher.</summary>
    public void Post(DateTime nowUtc)
    {
        if (Status == VoucherStatus.Posted)
            throw new PostingException("Expense is already posted.");
        if (!IsPayLater && (BankAccountId is null or 0))
            throw new PostingException("Expense has no \"Paid Through\" account.");
        if (Lines.Count == 0)
            throw new PostingException("Expense needs at least one line.");

        foreach (var line in Lines)
        {
            if (line.ExpenseAccountId == 0)
                throw new PostingException($"Line {line.LineNo} has no expense account.");
            if (line.Amount <= 0)
                throw new PostingException($"Line {line.LineNo} amount must be positive.");
            if (line.VatRate is < 0 or > 1)
                throw new PostingException($"Line {line.LineNo} VAT rate is invalid.");
        }

        Status = VoucherStatus.Posted;
        PostedAtUtc = nowUtc;
    }
}

/// <summary>One expense line: an amount charged to a specific expense account.</summary>
public class DirectExpenseLine
{
    public int Id { get; set; }

    public int DirectExpenseId { get; set; }
    public DirectExpense DirectExpense { get; set; } = null!;

    public int LineNo { get; set; }

    /// <summary>Optional catalog item this charge was picked from — auto-fills the account/description/amount below, editable afterward.</summary>
    public int? ItemId { get; set; }
    public Item? Item { get; set; }

    public int ExpenseAccountId { get; set; }
    public Account ExpenseAccount { get; set; } = null!;

    public int? CostCenterId { get; set; }
    public CostCenter? CostCenter { get; set; }

    public string? Description { get; set; }

    /// <summary>Entered amount — interpreted as Gross or Net depending on the parent
    /// <see cref="DirectExpense.AmountsIncludeVat"/> flag (see <see cref="Split"/>).</summary>
    public decimal Amount { get; set; }

    /// <summary>Fractional VAT rate for this line, e.g. 0.05 for UAE 5% — zero means no VAT
    /// (e.g. a bank charge or government fee). Each line can carry a different rate, same as
    /// <see cref="PurchaseInvoiceLine.VatRate"/>.</summary>
    public decimal VatRate { get; set; }

    /// <summary>
    /// Splits <see cref="Amount"/> into Net/VAT/Gross for a given inclusive/exclusive treatment.
    /// A pure function independent of the <see cref="DirectExpense"/> navigation property (which
    /// may not be fixed up yet before the parent is attached to a tracked context) — the caller
    /// always has the parent's <see cref="DirectExpense.AmountsIncludeVat"/> flag in hand anyway.
    /// </summary>
    public (decimal Net, decimal Vat, decimal Gross) Split(bool amountsIncludeVat)
    {
        if (amountsIncludeVat)
        {
            var gross = Amount;
            var net = Math.Round(gross / (1 + VatRate), 2, MidpointRounding.AwayFromZero);
            return (net, gross - net, gross);
        }
        else
        {
            var net = Amount;
            var vat = Math.Round(net * VatRate, 2, MidpointRounding.AwayFromZero);
            return (net, vat, net + vat);
        }
    }
}
