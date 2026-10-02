namespace AegisErp.Domain.Entities;

/// <summary>
/// An AR sales invoice. Posting it generates a balanced GL voucher
/// (Dr Accounts Receivable / Cr revenue per line / Cr VAT payable).
/// </summary>
public class SalesInvoice : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>Owning company.</summary>
    public int CompanyId { get; set; }

    /// <summary>Document number, e.g. "INV-2026-0141". Shared with the generated GL voucher. Unique within the company.</summary>
    public string InvoiceNo { get; set; } = string.Empty;

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>The customer's trade license/legal entity this invoice is for — only relevant when
    /// <see cref="CompanySetup.ProServiceModeEnabled"/> is on. Picking one fills <see cref="ContactTrn"/>
    /// from the organization's own TRN instead of the customer's.</summary>
    public int? OrganizationId { get; set; }
    public CustomerOrganization? Organization { get; set; }

    /// <summary>Contact snapshot fields (PRO Service Mode only) — auto-filled from the selected
    /// Customer/Organization on the form but stored here so a later edit to the Customer record
    /// doesn't retroactively change a past invoice. Also carried straight through from an Estimate
    /// on <see cref="AegisErp.Infrastructure.Services.EstimateService.ConvertToInvoiceAsync"/>.</summary>
    public string? ContactMobile { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactTrn { get; set; }
    public string? ContactPerson { get; set; }
    public string? BillingAddressSnapshot { get; set; }

    /// <summary>Free-text reference to whatever internal request/case this invoice is for (PRO
    /// Service Mode only) — e.g. "Visa Renewal — Ahmed" — not a tracked entity, just a label.</summary>
    public string? JobRequest { get; set; }

    public DateOnly Date { get; set; }
    public DateOnly DueDate { get; set; }

    public int FiscalPeriodId { get; set; }
    public FiscalPeriod FiscalPeriod { get; set; } = null!;

    public VoucherStatus Status { get; set; } = VoucherStatus.Draft;

    public string? Narration { get; set; }

    /// <summary>Customer's own purchase order reference, shown on the printed invoice.</summary>
    public string? CustomerPoNo { get; set; }

    /// <summary>Reference to the delivery note this invoice bills against, if any.</summary>
    public string? DeliveryNoteRef { get; set; }

    /// <summary>Reference to the sales order this invoice was raised from, if any.</summary>
    public string? SalesOrderRef { get; set; }

    /// <summary>Customer-facing note printed on the invoice (payment terms, delivery notes, etc.) —
    /// distinct from <see cref="Narration"/>, which is the internal GL voucher description.</summary>
    public string? Notes { get; set; }

    /// <summary>One-line, customer-facing summary of what the invoice is for — shown near the
    /// invoice number on the printed document. Distinct from <see cref="Narration"/> (internal).</summary>
    public string? Subject { get; set; }

    /// <summary>Legal/payment boilerplate printed at the bottom of the invoice — distinct from
    /// <see cref="Notes"/>, which is a short thank-you/context note.</summary>
    public string? TermsAndConditions { get; set; }

    /// <summary>Free-text salesperson credited with this invoice — internal only, never printed on
    /// the customer-facing document. Mirrors <see cref="Customer.Salesperson"/>; only collected when
    /// <see cref="CompanySetup.SalespersonEnabled"/> is on.</summary>
    public string? Salesperson { get; set; }

    /// <summary>Approval workflow state — independent of <see cref="Status"/>. An invoice can be
    /// submitted for approval while still a Draft; approval does not itself post it.</summary>
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.None;
    public DateTime? SubmittedForApprovalAtUtc { get; set; }
    public string? SubmittedForApprovalBy { get; set; }
    public DateTime? ApprovalDecisionAtUtc { get; set; }
    public string? ApprovalDecisionBy { get; set; }
    /// <summary>Rejection reason, if the last decision was a rejection.</summary>
    public string? ApprovalNote { get; set; }

    /// <summary>One supporting document attached to the invoice (e.g. signed PO, delivery proof).</summary>
    public string? AttachmentFileName { get; set; }
    public string? AttachmentContentType { get; set; }
    public byte[]? AttachmentData { get; set; }

    /// <summary>The GL voucher generated when this invoice was posted.</summary>
    public int? JournalVoucherId { get; set; }
    public JournalVoucher? JournalVoucher { get; set; }

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? PostedAtUtc { get; set; }
    public string? VoidedBy { get; set; }
    public DateTime? VoidedAtUtc { get; set; }

    /// <summary>Admin-only flag blocking further edits/void/attachment changes — e.g. during a
    /// closed audit period. Posted invoices are already immutable for accounting purposes; this
    /// additionally locks down the document itself.</summary>
    public bool IsLocked { get; set; }
    public DateTime? LockedAtUtc { get; set; }
    public string? LockedBy { get; set; }

    /// <summary>Opaque token for the public, anonymous read-only "Share" link — generated lazily
    /// the first time Share is used, then stable.</summary>
    public string? ShareToken { get; set; }

    public List<SalesInvoiceLine> Lines { get; set; } = new();
    public List<InvoiceReminderLog> ReminderLogs { get; set; } = new();

    public decimal TotalNet => Lines.Sum(l => l.Net);
    public decimal TotalVat => Lines.Sum(l => l.Vat);
    public decimal TotalGross => Lines.Sum(l => l.Gross);

    /// <summary>Validates the invoice and transitions it to Posted. The caller generates the GL voucher.</summary>
    public void Post(string postedBy, DateTime nowUtc)
    {
        if (Status == VoucherStatus.Posted)
            throw new PostingException("Invoice is already posted.");
        if (ApprovalStatus == ApprovalStatus.PendingApproval)
            throw new PostingException("Invoice is pending approval — approve or reject it before posting.");
        if (CustomerId == 0)
            throw new PostingException("Invoice has no customer.");
        if (Lines.Count == 0)
            throw new PostingException("Invoice needs at least one line.");
        if (DueDate < Date)
            throw new PostingException("Due date cannot be before the invoice date.");

        foreach (var line in Lines)
        {
            if (line.RevenueAccountId == 0)
                throw new PostingException($"Line {line.LineNo} has no revenue account.");
            if (line.Quantity <= 0)
                throw new PostingException($"Line {line.LineNo} quantity must be positive.");
            if (line.UnitPrice < 0)
                throw new PostingException($"Line {line.LineNo} unit price cannot be negative.");
            if (line.GovtFee < 0)
                throw new PostingException($"Line {line.LineNo} govt fee cannot be negative.");
            if (line.BankCharge < 0)
                throw new PostingException($"Line {line.LineNo} bank charge cannot be negative.");
            if (line.VatRate is < 0 or > 1)
                throw new PostingException($"Line {line.LineNo} VAT rate is invalid.");
            if (line.DiscountType == DiscountType.Percent && line.DiscountValue is < 0 or > 100)
                throw new PostingException($"Line {line.LineNo} discount % is invalid.");
            if (line.DiscountType == DiscountType.Amount && line.DiscountValue < 0)
                throw new PostingException($"Line {line.LineNo} discount amount cannot be negative.");
        }

        if (TotalGross <= 0)
            throw new PostingException("Invoice total must be positive.");

        Status = VoucherStatus.Posted;
        PostedBy = postedBy;
        PostedAtUtc = nowUtc;
    }

    /// <summary>Submits a draft invoice into the approval queue. Posting is blocked while pending.</summary>
    public void SubmitForApproval(string submittedBy, DateTime nowUtc)
    {
        if (Status != VoucherStatus.Draft)
            throw new PostingException("Only draft invoices can be submitted for approval.");
        if (ApprovalStatus == ApprovalStatus.PendingApproval)
            throw new PostingException("Invoice is already pending approval.");

        ApprovalStatus = ApprovalStatus.PendingApproval;
        SubmittedForApprovalAtUtc = nowUtc;
        SubmittedForApprovalBy = submittedBy;
        ApprovalDecisionAtUtc = null;
        ApprovalDecisionBy = null;
        ApprovalNote = null;
    }

    /// <summary>Approves a pending invoice, clearing the block on posting.</summary>
    public void Approve(string approvedBy, DateTime nowUtc)
    {
        if (ApprovalStatus != ApprovalStatus.PendingApproval)
            throw new PostingException("Invoice is not pending approval.");

        ApprovalStatus = ApprovalStatus.Approved;
        ApprovalDecisionAtUtc = nowUtc;
        ApprovalDecisionBy = approvedBy;
    }

    /// <summary>Rejects a pending invoice. It stays a Draft, editable and re-submittable.</summary>
    public void Reject(string rejectedBy, DateTime nowUtc, string? note)
    {
        if (ApprovalStatus != ApprovalStatus.PendingApproval)
            throw new PostingException("Invoice is not pending approval.");

        ApprovalStatus = ApprovalStatus.Rejected;
        ApprovalDecisionAtUtc = nowUtc;
        ApprovalDecisionBy = rejectedBy;
        ApprovalNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    /// <summary>Voids a draft invoice. Posted invoices already hit the ledger — reverse those with a Credit Note instead.</summary>
    public void Void(string voidedBy, DateTime nowUtc)
    {
        if (Status != VoucherStatus.Draft)
            throw new PostingException("Only draft invoices can be voided — a posted invoice already hit the ledger; reverse it with a Credit Note instead.");

        Status = VoucherStatus.Void;
        VoidedBy = voidedBy;
        VoidedAtUtc = nowUtc;
    }
}

/// <summary>A sales invoice line: quantity × unit price with a VAT rate, posted to a revenue account.</summary>
public class SalesInvoiceLine
{
    public int Id { get; set; }

    public int SalesInvoiceId { get; set; }
    public SalesInvoice SalesInvoice { get; set; } = null!;

    public int LineNo { get; set; }
    public string Description { get; set; } = string.Empty;

    /// <summary>The catalog item this line was billed from, if any — used to remember which
    /// revenue account was last used for this item so it can be pre-filled next time.</summary>
    public int? ItemId { get; set; }
    public Item? Item { get; set; }

    public int RevenueAccountId { get; set; }
    public Account RevenueAccount { get; set; } = null!;

    /// <summary>Direct (default) credits <see cref="RevenueAccountId"/> as usual, hitting the P&amp;L
    /// now. Deferred credits the Deferred Revenue control account (a Balance Sheet liability)
    /// instead — see <see cref="AegisErp.Domain.RevenueRecognition"/>.</summary>
    public RevenueRecognition Recognition { get; set; } = RevenueRecognition.Direct;

    public int? CostCenterId { get; set; }
    public CostCenter? CostCenter { get; set; }

    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }

    /// <summary>Fractional VAT rate, e.g. 0.05 for UAE 5%.</summary>
    public decimal VatRate { get; set; } = 0.05m;

    /// <summary>Line discount — a percentage (0-100) or a flat amount, per <see cref="DiscountType"/>.</summary>
    public decimal DiscountValue { get; set; }

    /// <summary>Whether <see cref="DiscountValue"/> is a percentage of the line subtotal or a flat
    /// currency amount subtracted from it.</summary>
    public DiscountType DiscountType { get; set; } = DiscountType.Percent;

    /// <summary>Unit of measure, e.g. "pcs", "hrs" — free text, defaults from the item's own Unit.</summary>
    public string? Uom { get; set; }

    /// <summary>Supporting document for this specific line (e.g. a delivery note or spec sheet for that item).</summary>
    public string? AttachmentFileName { get; set; }
    public string? AttachmentContentType { get; set; }
    public byte[]? AttachmentData { get; set; }

    /// <summary>Fulfillment tracking for the cross-invoice Transactions view — independent of the
    /// invoice's own posting/payment status, since a service can be delivered before or after
    /// it's paid for.</summary>
    public bool IsCompleted { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? CompletedBy { get; set; }

    /// <summary>Expiry date of the government document this PRO-service line actually produced (e.g.
    /// a trade license or visa) — set when the line is completed, so upcoming renewals can be tracked
    /// on the Transactions view. Unrelated to the invoice's own dates; only meaningful once completed.</summary>
    public DateOnly? ExpiryDate { get; set; }

    /// <summary>The vendor/subcontractor who actually fulfills this line, if the service is
    /// outsourced rather than delivered in-house — surfaced as "Supplier" on the Transactions view.</summary>
    public int? SupplierId { get; set; }
    public Vendor? Supplier { get; set; }

    /// <summary>Non-taxable disbursements (PRO Service Mode only) — a government fee and a bank
    /// charge passed straight through to the customer at cost, with no VAT applied. Default 0, so
    /// every invoice saved before these fields existed has an unchanged <see cref="Net"/>.</summary>
    public decimal GovtFee { get; set; }
    public decimal BankCharge { get; set; }

    /// <summary>Employee tasked with delivering this line's PRO service (PRO Service Mode only) —
    /// assigned via the Transactions view or at invoice creation. Commission attribution prefers
    /// this over the customer's generic assigned employee (<see cref="Customer.AssignedEmployeeId"/>)
    /// when set — see <see cref="AegisErp.Infrastructure.Services.CommissionService"/>.</summary>
    public int? AssignedToEmployeeId { get; set; }
    public Employee? AssignedToEmployee { get; set; }

    /// <summary>Applicant / file reference (PRO Service Mode only) — who the government service is
    /// actually being filed for, when that differs from the paying Customer (e.g. billed to a
    /// company but the visa/license is for a named individual). Free text. Default null.</summary>
    public string? ApplicantReference { get; set; }

    /// <summary>The taxable base — <see cref="UnitPrice"/> doubles as "Center Fee" in PRO Service
    /// Mode. VAT applies only to this, never to <see cref="GovtFee"/>/<see cref="BankCharge"/>.</summary>
    public decimal TaxableNet => DiscountType == DiscountType.Percent
        ? Math.Round(Quantity * UnitPrice * (1 - DiscountValue / 100m), 2, MidpointRounding.AwayFromZero)
        : Math.Round(Math.Max(0, Quantity * UnitPrice - DiscountValue), 2, MidpointRounding.AwayFromZero);
    public decimal Net => TaxableNet + Math.Round(Quantity * (GovtFee + BankCharge), 2, MidpointRounding.AwayFromZero);
    public decimal Vat => Math.Round(TaxableNet * VatRate, 2, MidpointRounding.AwayFromZero);
    public decimal Gross => Net + Vat;
}

/// <summary>One payment reminder sent for an invoice — manual ("Send Reminder Now") or automated
/// (the background service, per the day-offsets configured in Invoice Preferences). Lets the UI show
/// "last reminded on" and stops the background service from re-sending the same day twice.</summary>
public class InvoiceReminderLog
{
    public int Id { get; set; }
    public int SalesInvoiceId { get; set; }
    public SalesInvoice SalesInvoice { get; set; } = null!;

    public DateTime SentAtUtc { get; set; }
    public string SentBy { get; set; } = "System Admin";
    public bool IsAutomated { get; set; }
}
