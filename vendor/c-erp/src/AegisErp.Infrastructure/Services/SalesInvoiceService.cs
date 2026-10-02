using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Input line for creating a sales invoice from the UI.</summary>
public record InvoiceLineInput(string Description, int RevenueAccountId, int? CostCenterId,
    decimal Quantity, decimal UnitPrice, decimal VatRate, int? ItemId = null,
    decimal DiscountValue = 0, string? Uom = null,
    RevenueRecognition Recognition = RevenueRecognition.Direct,
    DiscountType DiscountType = DiscountType.Percent,
    decimal GovtFee = 0, decimal BankCharge = 0, int? AssignedToEmployeeId = null,
    string? ApplicantReference = null);

public class SalesInvoiceService
{
    /// <summary>Hard cap on an attached document's size (5 MB) — stored inline in the database.</summary>
    public const int MaxAttachmentBytes = 5 * 1024 * 1024;

    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly EmailService _emailSvc;
    private readonly ICurrentCompany _current;
    public SalesInvoiceService(IDbContextFactory<AegisDbContext> dbf, EmailService emailSvc, ICurrentCompany current)
    {
        _dbf = dbf;
        _emailSvc = emailSvc;
        _current = current;
    }

    public async Task<List<SalesInvoice>> GetRecentAsync(int take = 50)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.SalesInvoices.AsNoTracking()
            .Include(i => i.Customer).Include(i => i.Lines).Include(i => i.JournalVoucher)
            .OrderByDescending(i => i.Date).ThenByDescending(i => i.Id)
            .Take(take).ToListAsync();
    }

    /// <summary>
    /// Every invoice for the invoice list page, each with its Zoho-style display status
    /// (Draft / Pending / Overdue / Paid / Void) and remaining balance computed from posted
    /// receipts and credit notes applied against it.
    /// </summary>
    public async Task<List<SalesInvoiceRow>> GetAllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoices = await db.SalesInvoices.AsNoTracking()
            .Include(i => i.Customer).Include(i => i.Lines).Include(i => i.JournalVoucher)
            .OrderByDescending(i => i.Date).ThenByDescending(i => i.Id)
            .ToListAsync();

        // Derived from ReceiptAllocation (joined through the invoice line it targets), never from
        // CustomerReceipt.SalesInvoiceId — that FK can't represent a receipt spanning multiple
        // invoices (it's null in that case even though the receipt did settle part of this one).
        var allocatedByInvoice = (await db.ReceiptAllocations.AsNoTracking()
            .Where(a => a.CustomerReceipt.Status == VoucherStatus.Posted)
            .Select(a => new { InvoiceId = a.SalesInvoiceLine.SalesInvoiceId, a.Amount })
            .ToListAsync())
            .GroupBy(a => a.InvoiceId)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
        var credits = await db.CreditNotes.AsNoTracking().Include(n => n.Lines)
            .Where(n => n.Status == VoucherStatus.Posted && n.SalesInvoiceId != null)
            .ToListAsync();
        // On-account credit notes applied after the fact via CreditNoteService.ApplyToInvoiceAsync.
        var appliedCreditByInvoice = (await db.CreditNoteAllocations.AsNoTracking()
            .Where(a => a.CreditNote.Status == VoucherStatus.Posted)
            .Select(a => new { a.SalesInvoiceId, a.Amount })
            .ToListAsync())
            .GroupBy(a => a.SalesInvoiceId)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return invoices.Select(i =>
        {
            var allocated = allocatedByInvoice.GetValueOrDefault(i.Id)
                          + credits.Where(n => n.SalesInvoiceId == i.Id).Sum(n => n.TotalGross)
                          + appliedCreditByInvoice.GetValueOrDefault(i.Id);
            var balance = i.TotalGross - allocated;
            var status = i.Status switch
            {
                VoucherStatus.Draft => ArStatus.Draft,
                VoucherStatus.Void => ArStatus.Void,
                _ => balance <= 0 ? ArStatus.Paid : i.DueDate < today ? ArStatus.Overdue : ArStatus.Pending,
            };
            return new SalesInvoiceRow(i, balance, status);
        }).ToList();
    }

    /// <summary>One invoice with full detail (Customer, Lines with Item, JournalVoucher) for the detail page.</summary>
    public async Task<SalesInvoice?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.SalesInvoices.AsNoTracking()
            .Include(i => i.Customer).Include(i => i.Lines).ThenInclude(l => l.Item).Include(i => i.JournalVoucher)
            .Include(i => i.Lines).ThenInclude(l => l.AssignedToEmployee)
            .Include(i => i.Organization)
            .FirstOrDefaultAsync(i => i.Id == id);
    }

    /// <summary>
    /// Per-line paid/balance breakdown for one invoice, from posted receipt allocations — so
    /// staff can see which specific service on a multi-line invoice is settled vs still owing.
    /// </summary>
    public async Task<List<SalesInvoiceLineBalance>> GetLineBalancesAsync(int invoiceId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        // Route through the company-scoped SalesInvoices — SalesInvoiceLine has no CompanyId/query
        // filter of its own, so querying db.SalesInvoiceLines directly by invoiceId would return
        // another company's lines for a guessed/foreign id.
        var lines = await db.SalesInvoices.AsNoTracking()
            .SelectMany(i => i.Lines).Include(l => l.Item)
            .Where(l => l.SalesInvoiceId == invoiceId)
            .OrderBy(l => l.LineNo)
            .ToListAsync();

        var lineIds = lines.Select(l => l.Id).ToList();
        var allocated = (await db.ReceiptAllocations.AsNoTracking()
            .Where(a => lineIds.Contains(a.SalesInvoiceLineId) && a.CustomerReceipt.Status == VoucherStatus.Posted)
            .Select(a => new { a.SalesInvoiceLineId, a.Amount })
            .ToListAsync())
            .GroupBy(a => a.SalesInvoiceLineId)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));

        return lines.Select(l => new SalesInvoiceLineBalance(
            l.Id, l.Description, l.Item?.Name, l.Gross, allocated.GetValueOrDefault(l.Id))).ToList();
    }

    /// <summary>
    /// Total posted credit notes against this invoice — credit notes reduce the invoice-level
    /// balance but, unlike receipts, aren't yet attributed to a specific line (see
    /// <see cref="GetLineBalancesAsync"/>'s doc comment), so callers use this to show a
    /// disclaimer rather than silently showing per-line totals that don't reconcile to the header.
    /// </summary>
    public async Task<decimal> GetCreditedTotalAsync(int invoiceId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var direct = (await db.CreditNotes.AsNoTracking().Include(n => n.Lines)
            .Where(n => n.SalesInvoiceId == invoiceId && n.Status == VoucherStatus.Posted)
            .ToListAsync()).Sum(n => n.TotalGross);
        var applied = (await db.CreditNoteAllocations.AsNoTracking()
            .Where(a => a.SalesInvoiceId == invoiceId && a.CreditNote.Status == VoucherStatus.Posted)
            .Select(a => a.Amount).ToListAsync()).Sum();
        return direct + applied;
    }

    /// <summary>
    /// The revenue account used the last time this item was billed on a posted sales invoice,
    /// so the invoice line editor can pre-fill it instead of asking the user to pick it again.
    /// </summary>
    public async Task<int?> GetLastRevenueAccountForItemAsync(int itemId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.SalesInvoices.AsNoTracking()
            .SelectMany(i => i.Lines, (i, l) => new { i.Date, i.Id, l.ItemId, l.RevenueAccountId })
            .Where(x => x.ItemId == itemId)
            .OrderByDescending(x => x.Date).ThenByDescending(x => x.Id)
            .Select(x => (int?)x.RevenueAccountId)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Next invoice number, scoped to the SalesInvoices table itself (not the JournalVoucher
    /// sequence) so a still-unposted draft's number can never collide with — or be reused by —
    /// an invoice created directly through <see cref="CreateAndPostAsync"/>.
    /// </summary>
    private static async Task<string> NextInvoiceNoAsync(AegisDbContext db, int year)
    {
        var prefix = db.CurrentCompanyId is int companyId
            ? (await db.CompanySetups.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId))?.InvoiceNumberPrefix
            : null;
        var existing = await db.SalesInvoices.Select(i => i.InvoiceNo).ToListAsync();
        return JournalPoster.NextDocNo(existing, string.IsNullOrWhiteSpace(prefix) ? "INV" : prefix, year);
    }

    /// <summary>
    /// Previews the invoice number that will be assigned to a new invoice raised in
    /// <paramref name="year"/>, so the create-invoice form can display it before saving.
    /// The number isn't reserved — it's only actually assigned when the invoice is created.
    /// </summary>
    public async Task<string> PeekNextInvoiceNoAsync(int year)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await NextInvoiceNoAsync(db, year);
    }

    /// <summary>
    /// Saves a sales invoice as a Draft: no GL voucher is generated and nothing touches the
    /// ledger. The invoice number is reserved up front so it stays the same when later posted.
    /// </summary>
    public async Task<SalesInvoice> CreateDraftAsync(
        int customerId, DateOnly date, int fiscalPeriodId, string? narration,
        string createdBy, IEnumerable<InvoiceLineInput> lines, DateTime nowUtc,
        string? customerPoNo = null, string? deliveryNoteRef = null, string? salesOrderRef = null,
        string? notes = null, int? paymentTermsDays = null,
        string? subject = null, string? termsAndConditions = null, string? salesperson = null,
        int? organizationId = null, string? contactMobile = null, string? contactEmail = null,
        string? contactTrn = null, string? contactPerson = null, string? billingAddress = null,
        string? jobRequest = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();

        var customer = await db.Customers.FindAsync(customerId)
            ?? throw new PostingException("Customer not found.");

        var invoice = new SalesInvoice
        {
            InvoiceNo = await NextInvoiceNoAsync(db, date.Year),
            CustomerId = customerId,
            OrganizationId = organizationId,
            ContactMobile = contactMobile,
            ContactEmail = contactEmail,
            ContactTrn = contactTrn,
            ContactPerson = contactPerson,
            BillingAddressSnapshot = billingAddress,
            JobRequest = jobRequest,
            Date = date,
            DueDate = date.AddDays(paymentTermsDays ?? customer.PaymentTermsDays),
            FiscalPeriodId = fiscalPeriodId,
            Narration = string.IsNullOrWhiteSpace(narration) ? $"Sales invoice — {customer.Name}" : narration,
            CustomerPoNo = string.IsNullOrWhiteSpace(customerPoNo) ? null : customerPoNo.Trim(),
            DeliveryNoteRef = string.IsNullOrWhiteSpace(deliveryNoteRef) ? null : deliveryNoteRef.Trim(),
            SalesOrderRef = string.IsNullOrWhiteSpace(salesOrderRef) ? null : salesOrderRef.Trim(),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            Subject = string.IsNullOrWhiteSpace(subject) ? null : subject.Trim(),
            TermsAndConditions = string.IsNullOrWhiteSpace(termsAndConditions) ? null : termsAndConditions.Trim(),
            Salesperson = string.IsNullOrWhiteSpace(salesperson) ? null : salesperson.Trim(),
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
            Status = VoucherStatus.Draft,
        };

        var no = 1;
        foreach (var l in lines)
        {
            ValidateDraftLine(no, l);
            invoice.Lines.Add(new SalesInvoiceLine
            {
                LineNo = no++,
                Description = l.Description,
                ItemId = l.ItemId,
                RevenueAccountId = l.RevenueAccountId,
                CostCenterId = l.CostCenterId,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                VatRate = l.VatRate,
                DiscountValue = l.DiscountValue,
                DiscountType = l.DiscountType,
                Uom = l.Uom,
                Recognition = l.Recognition,
                GovtFee = l.GovtFee,
                BankCharge = l.BankCharge,
                AssignedToEmployeeId = l.AssignedToEmployeeId,
                ApplicantReference = l.ApplicantReference,
            });
        }
        if (invoice.Lines.Count == 0)
            throw new PostingException("Invoice needs at least one line.");

        db.SalesInvoices.Add(invoice);
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return invoice;
    }

    /// <summary>
    /// Replaces a draft's header fields and lines in place — the invoice number and Draft status
    /// are untouched. Only ever valid while the invoice is still Draft; once posted, an invoice
    /// is immutable and must be reversed with a Credit Note instead of edited.
    /// </summary>
    public async Task<SalesInvoice> UpdateDraftAsync(
        int invoiceId, int customerId, DateOnly date, int fiscalPeriodId, string? narration,
        IEnumerable<InvoiceLineInput> lines,
        string? customerPoNo = null, string? deliveryNoteRef = null, string? salesOrderRef = null,
        string? notes = null, int? paymentTermsDays = null,
        string? subject = null, string? termsAndConditions = null, string? salesperson = null,
        int? organizationId = null, string? contactMobile = null, string? contactEmail = null,
        string? contactTrn = null, string? contactPerson = null, string? billingAddress = null,
        string? jobRequest = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();

        var invoice = await db.SalesInvoices.Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");
        if (invoice.Status != VoucherStatus.Draft)
            throw new PostingException("Only draft invoices can be edited — a posted invoice already hit the ledger; reverse it with a Credit Note instead.");
        EnsureNotLocked(invoice);

        var customer = await db.Customers.FindAsync(customerId)
            ?? throw new PostingException("Customer not found.");

        invoice.CustomerId = customerId;
        invoice.OrganizationId = organizationId;
        invoice.ContactMobile = contactMobile;
        invoice.ContactEmail = contactEmail;
        invoice.ContactTrn = contactTrn;
        invoice.ContactPerson = contactPerson;
        invoice.BillingAddressSnapshot = billingAddress;
        invoice.JobRequest = jobRequest;
        invoice.Date = date;
        invoice.DueDate = date.AddDays(paymentTermsDays ?? customer.PaymentTermsDays);
        invoice.FiscalPeriodId = fiscalPeriodId;
        invoice.Narration = string.IsNullOrWhiteSpace(narration) ? $"Sales invoice — {customer.Name}" : narration;
        invoice.CustomerPoNo = string.IsNullOrWhiteSpace(customerPoNo) ? null : customerPoNo.Trim();
        invoice.DeliveryNoteRef = string.IsNullOrWhiteSpace(deliveryNoteRef) ? null : deliveryNoteRef.Trim();
        invoice.SalesOrderRef = string.IsNullOrWhiteSpace(salesOrderRef) ? null : salesOrderRef.Trim();
        invoice.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        invoice.Subject = string.IsNullOrWhiteSpace(subject) ? null : subject.Trim();
        invoice.TermsAndConditions = string.IsNullOrWhiteSpace(termsAndConditions) ? null : termsAndConditions.Trim();
        invoice.Salesperson = string.IsNullOrWhiteSpace(salesperson) ? null : salesperson.Trim();

        invoice.Lines.Clear();
        var no = 1;
        foreach (var l in lines)
        {
            ValidateDraftLine(no, l);
            invoice.Lines.Add(new SalesInvoiceLine
            {
                LineNo = no++,
                Description = l.Description,
                ItemId = l.ItemId,
                RevenueAccountId = l.RevenueAccountId,
                CostCenterId = l.CostCenterId,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                VatRate = l.VatRate,
                DiscountValue = l.DiscountValue,
                DiscountType = l.DiscountType,
                Uom = l.Uom,
                Recognition = l.Recognition,
                GovtFee = l.GovtFee,
                BankCharge = l.BankCharge,
                AssignedToEmployeeId = l.AssignedToEmployeeId,
                ApplicantReference = l.ApplicantReference,
            });
        }
        if (invoice.Lines.Count == 0)
            throw new PostingException("Invoice needs at least one line.");

        await JournalPoster.SaveChangesTranslatedAsync(db);
        return invoice;
    }

    /// <summary>
    /// A draft is allowed to be incomplete in most respects (that's the point of a draft), but
    /// RevenueAccountId is a required foreign key at the schema level — leaving it unset (the
    /// form's "— select —" placeholder maps to 0) would otherwise reach SaveChanges as a raw FK
    /// violation and surface as a confusing generic "record doesn't exist" error instead of this.
    /// </summary>
    private static void ValidateDraftLine(int lineNo, InvoiceLineInput l)
    {
        if (l.RevenueAccountId == 0)
            throw new PostingException($"Line {lineNo}: pick a Revenue Account before saving (even as a draft).");
    }

    /// <summary>Posts a previously-saved draft, generating its GL voucher under the same invoice number.</summary>
    public async Task<SalesInvoice> PostDraftAsync(int invoiceId, string postedBy, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var invoice = await db.SalesInvoices.Include(i => i.Lines).Include(i => i.Customer)
            .FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");
        if (invoice.Status != VoucherStatus.Draft)
            throw new PostingException("Only draft invoices can be posted.");
        EnsureNotLocked(invoice);

        invoice.Post(postedBy, nowUtc); // domain validation (positive totals, valid lines, due date, approval gate)

        var ar = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.AccountsReceivable);
        var vat = invoice.TotalVat > 0 ? await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.VatPayable) : null;
        var deferred = invoice.Lines.Any(l => l.Recognition == RevenueRecognition.Deferred)
            ? await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.DeferredRevenue) : null;

        invoice.JournalVoucher = await JournalPoster.PostAsync(
            db, _current.CanPost, VoucherType.SalesInvoice, invoice.InvoiceNo, invoice.Date, invoice.FiscalPeriodId,
            invoice.Narration, invoice.InvoiceNo, postedBy, BuildVoucherLines(invoice, ar.Id, vat?.Id, deferred?.Id), nowUtc);

        await CommissionService.CalculateForInvoiceAsync(db, invoice, nowUtc);

        await JournalPoster.SaveAndCommitAsync(db, tx);
        return invoice;
    }

    /// <summary>Voids a draft invoice. Posted invoices already hit the ledger — reverse those with a Credit Note instead.</summary>
    public async Task VoidDraftAsync(int invoiceId, string voidedBy, DateTime nowUtc)
    {
        if (!_current.CanPost)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.SalesInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");
        EnsureNotLocked(invoice);

        invoice.Void(voidedBy, nowUtc);
        await db.SaveChangesAsync();
    }

    /// <summary>Submits a draft invoice for approval. Posting is blocked while pending.</summary>
    public async Task SubmitForApprovalAsync(int invoiceId, string submittedBy, DateTime nowUtc)
    {
        if (!_current.CanPost)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.SalesInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");

        invoice.SubmitForApproval(submittedBy, nowUtc);
        await db.SaveChangesAsync();
    }

    /// <summary>Approves a pending invoice, clearing the block on posting. Requires CanAdminister,
    /// not just CanPost — the UI only shows Approve/Reject to an Admin (see
    /// SalesInvoiceDetailPanel's <c>_canApprove</c>), so an Accountant who can post their own
    /// drafts shouldn't be able to approve them server-side either.</summary>
    public async Task ApproveAsync(int invoiceId, string approvedBy, DateTime nowUtc)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.SalesInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");

        invoice.Approve(approvedBy, nowUtc);
        await db.SaveChangesAsync();
    }

    /// <summary>Rejects a pending invoice. It stays a Draft, editable and re-submittable. Same
    /// CanAdminister requirement as <see cref="ApproveAsync"/> — same UI gate, same decision.</summary>
    public async Task RejectAsync(int invoiceId, string rejectedBy, DateTime nowUtc, string? note)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.SalesInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");

        invoice.Reject(rejectedBy, nowUtc, note);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Attaches (or replaces) the invoice's single supporting document. Allowed regardless of the
    /// invoice's posting status — attaching a document doesn't touch financial data.
    /// </summary>
    public async Task SetAttachmentAsync(int invoiceId, string fileName, string contentType, byte[] data)
    {
        if (data.Length > MaxAttachmentBytes)
            throw new PostingException($"Attachment is too large — the limit is {MaxAttachmentBytes / (1024 * 1024)} MB.");

        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.SalesInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");
        EnsureNotLocked(invoice);

        invoice.AttachmentFileName = fileName;
        invoice.AttachmentContentType = contentType;
        invoice.AttachmentData = data;
        await db.SaveChangesAsync();
    }

    public async Task RemoveAttachmentAsync(int invoiceId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.SalesInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");
        EnsureNotLocked(invoice);

        invoice.AttachmentFileName = null;
        invoice.AttachmentContentType = null;
        invoice.AttachmentData = null;
        await db.SaveChangesAsync();
    }

    public async Task<(string FileName, string ContentType, byte[] Data)?> GetAttachmentAsync(int invoiceId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.SalesInvoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId);
        if (invoice?.AttachmentData is null || invoice.AttachmentFileName is null) return null;
        return (invoice.AttachmentFileName, invoice.AttachmentContentType ?? "application/octet-stream", invoice.AttachmentData);
    }

    /// <summary>
    /// Attaches (or replaces) one invoice line's own supporting document — e.g. a spec sheet or
    /// delivery note for that specific item, distinct from the invoice-level attachment.
    /// </summary>
    public async Task SetLineAttachmentAsync(int lineId, string fileName, string contentType, byte[] data)
    {
        if (data.Length > MaxAttachmentBytes)
            throw new PostingException($"Attachment is too large — the limit is {MaxAttachmentBytes / (1024 * 1024)} MB.");

        await using var db = await _dbf.CreateDbContextAsync();
        // Route through SalesInvoices (company-scoped) rather than db.SalesInvoiceLines directly —
        // SalesInvoiceLine has no CompanyId/query filter of its own, so a bare lineId lookup would
        // reach another company's line.
        var line = await db.SalesInvoices.SelectMany(i => i.Lines).FirstOrDefaultAsync(l => l.Id == lineId)
            ?? throw new PostingException("Invoice line not found.");

        line.AttachmentFileName = fileName;
        line.AttachmentContentType = contentType;
        line.AttachmentData = data;
        await db.SaveChangesAsync();
    }

    public async Task RemoveLineAttachmentAsync(int lineId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var line = await db.SalesInvoices.SelectMany(i => i.Lines).FirstOrDefaultAsync(l => l.Id == lineId)
            ?? throw new PostingException("Invoice line not found.");

        line.AttachmentFileName = null;
        line.AttachmentContentType = null;
        line.AttachmentData = null;
        await db.SaveChangesAsync();
    }

    public async Task<(string FileName, string ContentType, byte[] Data)?> GetLineAttachmentAsync(int lineId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var line = await db.SalesInvoices.AsNoTracking().SelectMany(i => i.Lines).FirstOrDefaultAsync(l => l.Id == lineId);
        if (line?.AttachmentData is null || line.AttachmentFileName is null) return null;
        return (line.AttachmentFileName, line.AttachmentContentType ?? "application/octet-stream", line.AttachmentData);
    }

    private static void EnsureNotLocked(SalesInvoice invoice)
    {
        if (invoice.IsLocked)
            throw new PostingException($"{invoice.InvoiceNo} is locked — unlock it first.");
    }

    /// <summary>Admin-only lock blocking further edits/void/attachment changes on this invoice. The
    /// docstring said "Admin-only" before this had any actual check backing it — the UI only shows
    /// the Lock/Unlock menu item behind Session.CanAdminister, but nothing here enforced it.</summary>
    public async Task LockAsync(int invoiceId, string lockedBy, DateTime nowUtc)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.SalesInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");
        if (invoice.IsLocked) throw new PostingException("Invoice is already locked.");

        invoice.IsLocked = true;
        invoice.LockedBy = lockedBy;
        invoice.LockedAtUtc = nowUtc;
        await db.SaveChangesAsync();
    }

    public async Task UnlockAsync(int invoiceId)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.SalesInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");

        invoice.IsLocked = false;
        invoice.LockedBy = null;
        invoice.LockedAtUtc = null;
        await db.SaveChangesAsync();
    }

    /// <summary>The token backing this invoice's public "Share" link — generated once, then stable.</summary>
    public async Task<string> GetOrCreateShareTokenAsync(int invoiceId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.SalesInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");

        if (string.IsNullOrEmpty(invoice.ShareToken))
        {
            invoice.ShareToken = Guid.NewGuid().ToString("N");
            await db.SaveChangesAsync();
        }
        return invoice.ShareToken;
    }

    /// <summary>
    /// Looks up an invoice by its public share token, for the anonymous Share page. Returns null for
    /// an unknown/never-shared token so the page can show "not found" rather than leaking which
    /// tokens exist. Runs unscoped (no authenticated session sets a current company on this route),
    /// which is correct here — the opaque token is the security boundary, not company scoping.
    /// </summary>
    public async Task<SalesInvoice?> GetBySharedTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.SalesInvoices.AsNoTracking()
            .Include(i => i.Customer).Include(i => i.Lines).ThenInclude(l => l.Item)
            .Include(i => i.Lines).ThenInclude(l => l.AssignedToEmployee)
            .Include(i => i.Organization)
            .FirstOrDefaultAsync(i => i.ShareToken == token);
    }

    public async Task<DateTime?> GetLastReminderSentAtAsync(int invoiceId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.InvoiceReminderLogs.AsNoTracking()
            .Where(r => r.SalesInvoiceId == invoiceId)
            .OrderByDescending(r => r.SentAtUtc)
            .Select(r => (DateTime?)r.SentAtUtc)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Sends a payment-reminder email for this invoice (manual "Send Reminder Now", or automated —
    /// see <c>InvoiceAutomationHostedService</c>) and logs it. Requires the invoice to be posted,
    /// have an outstanding balance, and the customer to have an email on file.
    /// </summary>
    public async Task SendReminderAsync(int invoiceId, string sentBy, DateTime nowUtc, bool isAutomated)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.SalesInvoices.Include(i => i.Customer).Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");
        if (invoice.Status != VoucherStatus.Posted)
            throw new PostingException("Only posted invoices can be reminded about.");
        if (string.IsNullOrWhiteSpace(invoice.Customer.Email))
            throw new PostingException($"{invoice.Customer.Name} has no email address on file.");

        var lineBalances = await GetLineBalancesAsync(invoiceId);
        var creditedTotal = await GetCreditedTotalAsync(invoiceId);
        var balance = invoice.TotalGross - lineBalances.Sum(l => l.Allocated) - creditedTotal;
        if (balance <= 0)
            throw new PostingException($"{invoice.InvoiceNo} is already fully paid — nothing to remind about.");

        var subject = $"Payment Reminder: Invoice {invoice.InvoiceNo} — AED {balance:N2} overdue";
        var body = $"Dear {invoice.Customer.Name},\n\n" +
                   $"This is a reminder that invoice {invoice.InvoiceNo} dated {invoice.Date:dd MMM yyyy} for AED {invoice.TotalGross:N2}, " +
                   $"due {invoice.DueDate:dd MMM yyyy}, has an outstanding balance of AED {balance:N2}.\n\n" +
                   "Please arrange payment at your earliest convenience. Thank you.";
        await _emailSvc.SendAsync(invoice.Customer.Email, subject, body);

        db.InvoiceReminderLogs.Add(new InvoiceReminderLog
        {
            SalesInvoiceId = invoiceId,
            SentAtUtc = nowUtc,
            SentBy = sentBy,
            IsAutomated = isAutomated,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Dr AR for the gross, Cr revenue per line (or Cr Deferred Revenue for a line whose
    /// <see cref="RevenueRecognition"/> is Deferred — see <see cref="WellKnownAccounts.DeferredRevenue"/>),
    /// Cr VAT for the total tax.
    /// </summary>
    private static List<VoucherLineInput> BuildVoucherLines(
        SalesInvoice invoice, int arAccountId, int? vatAccountId, int? deferredRevenueAccountId)
    {
        var lines = new List<VoucherLineInput>
        {
            new(arAccountId, null, $"{invoice.Customer.Name} — {invoice.InvoiceNo}", invoice.TotalGross, 0),
        };
        // Skip zero-net lines (e.g. complimentary items) — the invoice still records them,
        // but a zero GL line would fail the double-entry rules.
        lines.AddRange(invoice.Lines.Where(l => l.Net > 0).Select(l =>
        {
            var creditAccountId = l.Recognition == RevenueRecognition.Deferred && deferredRevenueAccountId is int defId
                ? defId : l.RevenueAccountId;
            return new VoucherLineInput(creditAccountId, l.CostCenterId, l.Description, 0, l.Net);
        }));
        if (invoice.TotalVat > 0 && vatAccountId is int vatId)
            lines.Add(new VoucherLineInput(vatId, null, $"Output VAT — {invoice.InvoiceNo}", 0, invoice.TotalVat));
        return lines;
    }

    /// <summary>
    /// Creates and posts a sales invoice in one transaction: the invoice, its lines and the
    /// generated GL voucher (Dr AR gross / Cr revenue net per line / Cr VAT payable) are
    /// persisted atomically. Invoice number and voucher number are the same document number.
    /// </summary>
    public async Task<SalesInvoice> CreateAndPostAsync(
        int customerId, DateOnly date, int fiscalPeriodId, string? narration,
        string createdBy, IEnumerable<InvoiceLineInput> lines, DateTime nowUtc,
        string? customerPoNo = null, string? deliveryNoteRef = null, string? salesOrderRef = null,
        string? notes = null, int? paymentTermsDays = null,
        string? subject = null, string? termsAndConditions = null, string? salesperson = null,
        int? organizationId = null, string? contactMobile = null, string? contactEmail = null,
        string? contactTrn = null, string? contactPerson = null, string? billingAddress = null,
        string? jobRequest = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var customer = await db.Customers.FindAsync(customerId)
            ?? throw new PostingException("Customer not found.");

        var invoiceNo = await NextInvoiceNoAsync(db, date.Year);

        var invoice = new SalesInvoice
        {
            InvoiceNo = invoiceNo,
            CustomerId = customerId,
            OrganizationId = organizationId,
            ContactMobile = contactMobile,
            ContactEmail = contactEmail,
            ContactTrn = contactTrn,
            ContactPerson = contactPerson,
            BillingAddressSnapshot = billingAddress,
            JobRequest = jobRequest,
            Date = date,
            DueDate = date.AddDays(paymentTermsDays ?? customer.PaymentTermsDays),
            FiscalPeriodId = fiscalPeriodId,
            Narration = string.IsNullOrWhiteSpace(narration) ? $"Sales invoice — {customer.Name}" : narration,
            CustomerPoNo = string.IsNullOrWhiteSpace(customerPoNo) ? null : customerPoNo.Trim(),
            DeliveryNoteRef = string.IsNullOrWhiteSpace(deliveryNoteRef) ? null : deliveryNoteRef.Trim(),
            SalesOrderRef = string.IsNullOrWhiteSpace(salesOrderRef) ? null : salesOrderRef.Trim(),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            Subject = string.IsNullOrWhiteSpace(subject) ? null : subject.Trim(),
            TermsAndConditions = string.IsNullOrWhiteSpace(termsAndConditions) ? null : termsAndConditions.Trim(),
            Salesperson = string.IsNullOrWhiteSpace(salesperson) ? null : salesperson.Trim(),
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
            Customer = customer,
        };

        var no = 1;
        foreach (var l in lines)
            invoice.Lines.Add(new SalesInvoiceLine
            {
                LineNo = no++,
                Description = l.Description,
                ItemId = l.ItemId,
                RevenueAccountId = l.RevenueAccountId,
                CostCenterId = l.CostCenterId,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                VatRate = l.VatRate,
                DiscountValue = l.DiscountValue,
                DiscountType = l.DiscountType,
                Uom = l.Uom,
                Recognition = l.Recognition,
                GovtFee = l.GovtFee,
                BankCharge = l.BankCharge,
                AssignedToEmployeeId = l.AssignedToEmployeeId,
                ApplicantReference = l.ApplicantReference,
            });

        invoice.Post(createdBy, nowUtc); // domain validation (positive totals, valid lines, due date)

        var ar = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.AccountsReceivable);
        var vat = invoice.TotalVat > 0 ? await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.VatPayable) : null;
        var deferred = invoice.Lines.Any(l => l.Recognition == RevenueRecognition.Deferred)
            ? await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.DeferredRevenue) : null;

        invoice.JournalVoucher = await JournalPoster.PostAsync(
            db, _current.CanPost, VoucherType.SalesInvoice, invoiceNo, date, fiscalPeriodId,
            invoice.Narration, invoiceNo, createdBy, BuildVoucherLines(invoice, ar.Id, vat?.Id, deferred?.Id), nowUtc);

        db.SalesInvoices.Add(invoice);
        await CommissionService.CalculateForInvoiceAsync(db, invoice, nowUtc);
        await JournalPoster.SaveAndCommitAsync(db, tx);
        return invoice;
    }

    /// <summary>
    /// Brings forward one historical/opening invoice from a prior system: a real, editable
    /// SalesInvoice tied to <paramref name="customerId"/> (so it shows on that customer's
    /// Invoices tab and can be settled by a normal Receipt Voucher going forward), but posted
    /// Dr Accounts Receivable / Cr the opening-balance equity account (31010) instead of a
    /// revenue account — the same account <see cref="ChartOfAccountsService"/>'s Opening
    /// Balances screen plugs into — so it never re-recognizes revenue that the old system
    /// already reported. Posted under <see cref="VoucherType.Opening"/> and dated at the very
    /// start of the books (earliest fiscal period), exactly like that screen, even though the
    /// invoice's own <paramref name="date"/> keeps its true historical value for display on the
    /// invoice and customer statement. <paramref name="amount"/> should be the balance still
    /// outstanding today, not the original invoice total — a historical invoice already fully
    /// paid off has nothing left to bring forward into this ledger.
    /// Caller is responsible for not double-counting: if a lump-sum AR opening balance was
    /// already entered manually on the Opening Balances screen, importing invoices on top of it
    /// will overstate AR — see <see cref="InvoiceImportService"/>'s pre-import check.
    /// </summary>
    public async Task<SalesInvoice> ImportOpeningInvoiceAsync(
        int customerId, string? reference, DateOnly date, DateOnly? dueDate, decimal amount,
        string? notes, string importedBy, DateTime nowUtc)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        if (amount <= 0) throw new PostingException("Outstanding amount must be positive.");

        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var customer = await db.Customers.FindAsync(customerId)
            ?? throw new PostingException("Customer not found.");
        var openingPeriod = await db.FiscalPeriods.OrderBy(p => p.StartDate).FirstOrDefaultAsync()
            ?? throw new PostingException("No fiscal period is defined for the opening entry.");
        var equity = await db.Accounts.FirstOrDefaultAsync(a => a.Code == "31010")
            ?? throw new PostingException("Opening-balance equity account 31010 is missing.");

        var invoiceNo = await NextInvoiceNoAsync(db, date.Year);
        var narration = $"Imported opening balance — {customer.Name}" +
                         (string.IsNullOrWhiteSpace(reference) ? "" : $" (Ref: {reference.Trim()})");

        var invoice = new SalesInvoice
        {
            InvoiceNo = invoiceNo,
            CustomerId = customerId,
            Date = date,
            DueDate = dueDate ?? date,
            FiscalPeriodId = openingPeriod.Id,
            Narration = narration,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            CreatedBy = importedBy,
            CreatedAtUtc = nowUtc,
            Customer = customer,
        };
        invoice.Lines.Add(new SalesInvoiceLine
        {
            LineNo = 1,
            Description = "Opening balance brought forward",
            RevenueAccountId = equity.Id,
            Quantity = 1,
            UnitPrice = amount,
            VatRate = 0,
        });

        invoice.Post(importedBy, nowUtc); // domain validation (positive totals, valid lines, due date)

        var ar = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.AccountsReceivable);
        // Dated at openingPeriod.StartDate, not the invoice's own (earlier) historical date — an
        // opening entry has to predate every reporting range it should show up in, or the Trial
        // Balance's "Opening Balance" column treats it as an in-period transaction instead (same
        // reasoning as ChartOfAccountsService.CreateAsync's opening-balance voucher).
        invoice.JournalVoucher = await JournalPoster.PostAsync(
            db, _current.CanPost, VoucherType.Opening, invoiceNo, openingPeriod.StartDate, openingPeriod.Id,
            narration, invoiceNo, importedBy, BuildVoucherLines(invoice, ar.Id, null, null), nowUtc);

        db.SalesInvoices.Add(invoice);
        // Deliberately no CommissionService call — this is a ledger seed, not a real sale that
        // should generate a fresh commission entry for whoever the customer is assigned to today.
        await JournalPoster.SaveAndCommitAsync(db, tx);
        return invoice;
    }
}
