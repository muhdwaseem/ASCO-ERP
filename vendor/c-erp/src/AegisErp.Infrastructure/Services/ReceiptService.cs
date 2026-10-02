using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>How much of a receipt is applied to one specific invoice line/service. Which invoice
/// a line belongs to is implicit (via <see cref="SalesInvoiceLine.SalesInvoiceId"/>), so this type
/// works unchanged whether a receipt targets one invoice or spans several.</summary>
public record ReceiptLineAllocationInput(int SalesInvoiceLineId, decimal Amount);

public class ReceiptService
{
    /// <summary>Hard cap on an attached document's size (5 MB) — stored inline in the database.</summary>
    public const int MaxAttachmentBytes = 5 * 1024 * 1024;

    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;
    public ReceiptService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    public async Task<List<CustomerReceipt>> GetRecentAsync(int take = 50)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CustomerReceipts.AsNoTracking()
            .Include(r => r.Customer).Include(r => r.BankAccount).Include(r => r.SalesInvoice)
            .Include(r => r.Allocations).ThenInclude(a => a.SalesInvoiceLine).ThenInclude(l => l.SalesInvoice)
            .OrderByDescending(r => r.Date).ThenByDescending(r => r.Id)
            .Take(take).ToListAsync();
    }

    /// <summary>Every receipt for one customer, for the customer detail view's Payments tab.</summary>
    public async Task<List<CustomerReceipt>> GetByCustomerAsync(int customerId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CustomerReceipts.AsNoTracking()
            .Include(r => r.BankAccount).Include(r => r.SalesInvoice)
            .Where(r => r.CustomerId == customerId)
            .OrderByDescending(r => r.Date).ThenByDescending(r => r.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Creates and posts a customer receipt in one transaction, generating the GL voucher
    /// (Dr bank / Cr Accounts Receivable). Two ways to target invoices:
    ///
    /// - <paramref name="salesInvoiceId"/> set: settles that one invoice (unchanged, original
    ///   behavior). When it has more than one billable (Net &gt; 0) line, <paramref name="allocations"/>
    ///   is required and must sum to exactly <paramref name="amount"/>; a single-billable-line
    ///   invoice auto-allocates the whole amount to that line if no allocations are given.
    /// - <paramref name="salesInvoiceId"/> null with non-empty <paramref name="allocations"/>: a
    ///   multi-invoice receipt — one payment settling parts of several different invoices at once.
    ///   Each invoice referenced must get an explicit allocation entry (no auto-allocate here);
    ///   the total across every invoice must still sum to exactly <paramref name="amount"/>.
    /// - Both null/empty: a pure on-account receipt, unattached to any invoice.
    /// </summary>
    public async Task<CustomerReceipt> CreateAndPostAsync(
        int customerId, int? salesInvoiceId, DateOnly date, int fiscalPeriodId,
        int bankAccountId, decimal amount, string? narration, string createdBy, DateTime nowUtc,
        IEnumerable<ReceiptLineAllocationInput>? allocations = null, PaymentMode paymentMode = PaymentMode.Cash,
        string? referenceNo = null, DateOnly? chequeDate = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var customer = await db.Customers.FindAsync(customerId)
            ?? throw new PostingException("Customer not found.");
        var inputs = allocations?.Where(a => a.Amount > 0).ToList() ?? new List<ReceiptLineAllocationInput>();

        var (resolvedInvoiceId, lineAllocations) = salesInvoiceId is int invId
            ? (invId, await ValidateSingleInvoiceAsync(db, await LoadInvoiceAsync(db, invId, customerId), amount, inputs))
            : await ValidateMultiInvoiceAsync(db, customerId, amount, inputs);

        var receiptNo = await NextReceiptNoAsync(db, date.Year);

        var receipt = new CustomerReceipt
        {
            ReceiptNo = receiptNo,
            CustomerId = customerId,
            SalesInvoiceId = resolvedInvoiceId,
            Date = date,
            FiscalPeriodId = fiscalPeriodId,
            BankAccountId = bankAccountId,
            Amount = amount,
            PaymentMode = paymentMode,
            ReferenceNo = string.IsNullOrWhiteSpace(referenceNo) ? null : referenceNo.Trim(),
            ChequeDate = chequeDate,
            Narration = string.IsNullOrWhiteSpace(narration) ? $"Customer receipt — {customer.Name}" : narration,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
        };
        receipt.Allocations = lineAllocations;

        receipt.Post(nowUtc); // domain validation

        receipt.JournalVoucher = await JournalPoster.PostAsync(
            db, _current.CanPost, VoucherType.Receipt, receiptNo, date, fiscalPeriodId,
            receipt.Narration, receiptNo, createdBy,
            await BuildVoucherLinesAsync(db, bankAccountId, customer.Name, receiptNo, receipt.Narration, amount), nowUtc);

        db.CustomerReceipts.Add(receipt);
        await JournalPoster.SaveAndCommitAsync(db, tx);
        return receipt;
    }

    /// <summary>
    /// Saves a receipt as a Draft: no GL voucher, nothing touches the ledger, and the intended
    /// allocations are stored as-is without validation — outstanding balances can move between now
    /// and when it's actually posted, so the real checks run fresh in <see cref="PostDraftAsync"/>.
    /// </summary>
    public async Task<CustomerReceipt> CreateDraftAsync(
        int customerId, int? salesInvoiceId, DateOnly date, int fiscalPeriodId,
        int bankAccountId, decimal amount, string? narration, string createdBy, DateTime nowUtc,
        IEnumerable<ReceiptLineAllocationInput>? allocations = null, PaymentMode paymentMode = PaymentMode.Cash,
        string? referenceNo = null, DateOnly? chequeDate = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var customer = await db.Customers.FindAsync(customerId)
            ?? throw new PostingException("Customer not found.");

        var receipt = new CustomerReceipt
        {
            ReceiptNo = await NextReceiptNoAsync(db, date.Year),
            CustomerId = customerId,
            SalesInvoiceId = salesInvoiceId,
            Date = date,
            FiscalPeriodId = fiscalPeriodId,
            BankAccountId = bankAccountId,
            Amount = amount,
            PaymentMode = paymentMode,
            ReferenceNo = string.IsNullOrWhiteSpace(referenceNo) ? null : referenceNo.Trim(),
            ChequeDate = chequeDate,
            Narration = string.IsNullOrWhiteSpace(narration) ? $"Customer receipt — {customer.Name}" : narration,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
            Status = VoucherStatus.Draft,
            Allocations = (allocations ?? Enumerable.Empty<ReceiptLineAllocationInput>())
                .Where(a => a.Amount > 0)
                .Select(a => new ReceiptAllocation { SalesInvoiceLineId = a.SalesInvoiceLineId, Amount = a.Amount })
                .ToList(),
        };

        db.CustomerReceipts.Add(receipt);
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return receipt;
    }

    /// <summary>Posts a previously-saved draft, re-validating its allocations fresh and generating its GL voucher under the same receipt number.</summary>
    public async Task<CustomerReceipt> PostDraftAsync(int receiptId, string postedBy, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var receipt = await db.CustomerReceipts.Include(r => r.Allocations).Include(r => r.Customer)
            .FirstOrDefaultAsync(r => r.Id == receiptId)
            ?? throw new PostingException("Receipt not found.");
        if (receipt.Status != VoucherStatus.Draft)
            throw new PostingException("Only draft receipts can be posted.");

        var requested = receipt.Allocations.Select(a => new ReceiptLineAllocationInput(a.SalesInvoiceLineId, a.Amount)).ToList();

        var (resolvedInvoiceId, validated) = receipt.SalesInvoiceId is int invId
            ? (invId, await ValidateSingleInvoiceAsync(db, await LoadInvoiceAsync(db, invId, receipt.CustomerId), receipt.Amount, requested))
            : await ValidateMultiInvoiceAsync(db, receipt.CustomerId, receipt.Amount, requested);

        receipt.SalesInvoiceId = resolvedInvoiceId;
        receipt.Allocations.Clear();
        foreach (var a in validated) receipt.Allocations.Add(a);

        receipt.Post(nowUtc); // domain validation

        receipt.JournalVoucher = await JournalPoster.PostAsync(
            db, _current.CanPost, VoucherType.Receipt, receipt.ReceiptNo, receipt.Date, receipt.FiscalPeriodId,
            receipt.Narration, receipt.ReceiptNo, postedBy,
            await BuildVoucherLinesAsync(db, receipt.BankAccountId, receipt.Customer.Name, receipt.ReceiptNo, receipt.Narration, receipt.Amount),
            nowUtc);

        await JournalPoster.SaveAndCommitAsync(db, tx);
        return receipt;
    }

    /// <summary>Voids a draft receipt. Posted receipts already hit the ledger — reverse those with a Journal/refund instead.</summary>
    public async Task VoidDraftAsync(int receiptId)
    {
        if (!_current.CanPost)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var receipt = await db.CustomerReceipts.FirstOrDefaultAsync(r => r.Id == receiptId)
            ?? throw new PostingException("Receipt not found.");
        if (receipt.Status != VoucherStatus.Draft)
            throw new PostingException("Only draft receipts can be voided — a posted receipt already hit the ledger.");

        receipt.Status = VoucherStatus.Void;
        await db.SaveChangesAsync();
    }

    /// <summary>One receipt with full detail (Customer, BankAccount, Allocations) — for the Send by
    /// Email dialog and the receipt detail view.</summary>
    public async Task<CustomerReceipt?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CustomerReceipts.AsNoTracking()
            .Include(r => r.Customer).Include(r => r.BankAccount).Include(r => r.JournalVoucher)
            .Include(r => r.Allocations).ThenInclude(a => a.SalesInvoiceLine).ThenInclude(l => l.SalesInvoice)
            .FirstOrDefaultAsync(r => r.Id == id);
    }

    /// <summary>
    /// Attaches (or replaces) the receipt's single supporting document (e.g. a scanned cheque).
    /// Allowed regardless of the receipt's posting status.
    /// </summary>
    public async Task SetAttachmentAsync(int receiptId, string fileName, string contentType, byte[] data)
    {
        if (data.Length > MaxAttachmentBytes)
            throw new PostingException($"Attachment is too large — the limit is {MaxAttachmentBytes / (1024 * 1024)} MB.");

        await using var db = await _dbf.CreateDbContextAsync();
        var receipt = await db.CustomerReceipts.FirstOrDefaultAsync(r => r.Id == receiptId)
            ?? throw new PostingException("Receipt not found.");

        receipt.AttachmentFileName = fileName;
        receipt.AttachmentContentType = contentType;
        receipt.AttachmentData = data;
        await db.SaveChangesAsync();
    }

    public async Task RemoveAttachmentAsync(int receiptId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var receipt = await db.CustomerReceipts.FirstOrDefaultAsync(r => r.Id == receiptId)
            ?? throw new PostingException("Receipt not found.");

        receipt.AttachmentFileName = null;
        receipt.AttachmentContentType = null;
        receipt.AttachmentData = null;
        await db.SaveChangesAsync();
    }

    public async Task<(string FileName, string ContentType, byte[] Data)?> GetAttachmentAsync(int receiptId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var receipt = await db.CustomerReceipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == receiptId);
        if (receipt?.AttachmentData is null || receipt.AttachmentFileName is null) return null;
        return (receipt.AttachmentFileName, receipt.AttachmentContentType ?? "application/octet-stream", receipt.AttachmentData);
    }

    private static async Task<SalesInvoice> LoadInvoiceAsync(AegisDbContext db, int invoiceId, int customerId)
    {
        await LockInvoiceRowAsync(db, invoiceId);
        var invoice = await db.SalesInvoices.AsNoTracking().Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");
        if (invoice.CustomerId != customerId)
            throw new PostingException("Invoice belongs to a different customer.");
        if (invoice.Status != VoucherStatus.Posted)
            throw new PostingException("Only posted invoices can be settled.");
        return invoice;
    }

    /// <summary>Legacy single-invoice validation — unchanged behavior. Requires a per-line split
    /// whenever the invoice has more than one billable line; auto-allocates a single billable line.</summary>
    private static async Task<List<ReceiptAllocation>> ValidateSingleInvoiceAsync(
        AegisDbContext db, SalesInvoice invoice, decimal amount, List<ReceiptLineAllocationInput> inputs)
    {
        await CheckInvoiceOutstandingAsync(db, invoice, amount);

        var billableLines = invoice.Lines.Where(l => l.Net > 0).ToList();
        if (billableLines.Count > 1)
        {
            if (inputs.Count == 0)
                throw new PostingException(
                    $"{invoice.InvoiceNo} has multiple services — specify how this receipt is allocated across its lines.");
            if (inputs.Sum(a => a.Amount) != amount)
                throw new PostingException(
                    $"Line allocations ({inputs.Sum(a => a.Amount):N2}) must add up to the receipt amount ({amount:N2}).");
            return await ValidateLineCapsAsync(db, billableLines, inputs);
        }
        if (billableLines.Count == 1)
            return new List<ReceiptAllocation> { new() { SalesInvoiceLineId = billableLines[0].Id, Amount = amount } };
        return new List<ReceiptAllocation>();
    }

    /// <summary>
    /// New multi-invoice validation — one receipt spanning several different invoices, each with
    /// an explicit line-level allocation (no auto-allocate convenience here; the caller always has
    /// each invoice's own line list in hand). Invoice groups are processed in ascending invoice-id
    /// order so two overlapping multi-invoice receipts never contend for locks in opposite order.
    /// </summary>
    private static async Task<(int? ResolvedSalesInvoiceId, List<ReceiptAllocation> Allocations)> ValidateMultiInvoiceAsync(
        AegisDbContext db, int customerId, decimal amount, List<ReceiptLineAllocationInput> inputs)
    {
        if (inputs.Count == 0)
            return (null, new List<ReceiptAllocation>()); // pure on-account

        if (inputs.Sum(a => a.Amount) != amount)
            throw new PostingException(
                $"Line allocations ({inputs.Sum(a => a.Amount):N2}) must add up to the receipt amount ({amount:N2}).");

        // Note: no .Include(l => l.SalesInvoice).ThenInclude(i => i.Lines) here — that Include path
        // cycles back to SalesInvoiceLine itself, which EF Core rejects on no-tracking queries.
        // Each invoice's full line list is instead fetched separately, per group, below.
        //
        // Routed through the company-scoped SalesInvoices (not db.SalesInvoiceLines directly) —
        // otherwise a lineId belonging to another company would resolve here and leak that
        // company's invoice number in the "belongs to a different customer" message below.
        var lineIds = inputs.Select(a => a.SalesInvoiceLineId).Distinct().ToList();
        var lines = await db.SalesInvoices.AsNoTracking()
            .SelectMany(i => i.Lines).Include(l => l.SalesInvoice)
            .Where(l => lineIds.Contains(l.Id))
            .ToListAsync();
        if (lines.Count != lineIds.Count)
            throw new PostingException("An allocation refers to a line that doesn't exist.");

        var byInvoice = lines.GroupBy(l => l.SalesInvoiceId).OrderBy(g => g.Key).ToList();

        var result = new List<ReceiptAllocation>();
        foreach (var group in byInvoice)
        {
            await LockInvoiceRowAsync(db, group.Key);
            var invoice = group.First().SalesInvoice;
            if (invoice.CustomerId != customerId)
                throw new PostingException($"Invoice {invoice.InvoiceNo} belongs to a different customer.");
            if (invoice.Status != VoucherStatus.Posted)
                throw new PostingException($"Only posted invoices can be settled ({invoice.InvoiceNo}).");

            var thisInvoicesLineIds = group.Select(l => l.Id).ToHashSet();
            var thisInvoicesInputs = inputs.Where(a => thisInvoicesLineIds.Contains(a.SalesInvoiceLineId)).ToList();
            var groupAmount = thisInvoicesInputs.Sum(a => a.Amount);

            // Fetch this invoice's full line set explicitly and wire it up before computing
            // TotalGross — the query above only pulled in whichever lines were referenced by
            // the caller's allocations, so `invoice.Lines` isn't reliably complete without this.
            var allLinesForInvoice = await db.SalesInvoiceLines.AsNoTracking()
                .Where(l => l.SalesInvoiceId == invoice.Id)
                .ToListAsync();
            invoice.Lines = allLinesForInvoice;

            await CheckInvoiceOutstandingAsync(db, invoice, groupAmount);
            result.AddRange(await ValidateLineCapsAsync(db, allLinesForInvoice, thisInvoicesInputs));
        }

        var distinctInvoiceIds = byInvoice.Select(g => g.Key).ToList();
        return (distinctInvoiceIds.Count == 1 ? distinctInvoiceIds[0] : null, result);
    }

    /// <summary>
    /// Locks the invoice's row on Postgres (transaction-scoped, auto-released at commit/rollback)
    /// so two simultaneous receipts against the same invoice serialize instead of both reading the
    /// same outstanding balance and both passing (the "Concurrent receipt allocation" gap noted in
    /// the README). No-op on Sqlite or outside an ambient transaction. NOT yet exercised against a
    /// live Postgres server — verify with a real concurrency test before relying on it in production.
    /// </summary>
    private static async Task LockInvoiceRowAsync(AegisDbContext db, int invoiceId)
    {
        if (!db.Database.IsNpgsql() || db.Database.CurrentTransaction is null) return;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"SalesInvoices\" WHERE \"Id\" = {invoiceId} FOR UPDATE");
    }

    /// <summary>
    /// This invoice's own outstanding must not be exceeded by <paramref name="amountBeingApplied"/>.
    /// Derived from <see cref="ReceiptAllocation"/> (joined through the line it targets) — never
    /// from <see cref="CustomerReceipt.SalesInvoiceId"/>, which can't represent a receipt that
    /// also settles other invoices and would otherwise under-count what's already been applied here.
    /// </summary>
    private static async Task CheckInvoiceOutstandingAsync(AegisDbContext db, SalesInvoice invoice, decimal amountBeingApplied)
    {
        var allocated = (await db.ReceiptAllocations.AsNoTracking()
            .Where(a => a.SalesInvoiceLine.SalesInvoiceId == invoice.Id && a.CustomerReceipt.Status == VoucherStatus.Posted)
            .Select(a => a.Amount)
            .ToListAsync()).Sum();
        var credited = (await db.CreditNotes.Include(n => n.Lines)
            .Where(n => n.SalesInvoiceId == invoice.Id && n.Status == VoucherStatus.Posted)
            .ToListAsync()).Sum(n => n.TotalGross);
        // Plus any on-account credit note balance since applied here via CreditNoteService.ApplyToInvoiceAsync.
        var appliedCredit = (await db.CreditNoteAllocations
            .Where(a => a.SalesInvoiceId == invoice.Id && a.CreditNote.Status == VoucherStatus.Posted)
            .Select(a => a.Amount).ToListAsync()).Sum();
        var outstanding = invoice.TotalGross - allocated - credited - appliedCredit;
        if (amountBeingApplied > outstanding)
            throw new PostingException(
                $"Amount {amountBeingApplied:N2} exceeds the outstanding {outstanding:N2} on {invoice.InvoiceNo}.");
    }

    /// <summary>Each input must belong to one of <paramref name="lines"/> and not push that line's cumulative posted allocations past its own Gross.</summary>
    private static async Task<List<ReceiptAllocation>> ValidateLineCapsAsync(
        AegisDbContext db, List<SalesInvoiceLine> lines, List<ReceiptLineAllocationInput> inputs)
    {
        var lineIds = lines.Select(l => l.Id).ToHashSet();
        if (inputs.Any(a => !lineIds.Contains(a.SalesInvoiceLineId)))
            throw new PostingException("An allocation refers to a line that is not part of this invoice.");

        var idsList = lines.Select(l => l.Id).ToList();
        var existingByLine = (await db.ReceiptAllocations
            .Where(a => idsList.Contains(a.SalesInvoiceLineId) && a.CustomerReceipt.Status == VoucherStatus.Posted)
            .Select(a => new { a.SalesInvoiceLineId, a.Amount })
            .ToListAsync())
            .GroupBy(a => a.SalesInvoiceLineId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

        var result = new List<ReceiptAllocation>();
        foreach (var a in inputs)
        {
            var line = lines.First(l => l.Id == a.SalesInvoiceLineId);
            var already = existingByLine.GetValueOrDefault(a.SalesInvoiceLineId);
            if (already + a.Amount > line.Gross)
                throw new PostingException(
                    $"Allocating {a.Amount:N2} to '{line.Description}' would exceed its remaining balance ({line.Gross - already:N2}).");
            result.Add(new ReceiptAllocation { SalesInvoiceLineId = a.SalesInvoiceLineId, Amount = a.Amount });
        }
        return result;
    }

    /// <summary>Dr bank for the amount, Cr Accounts Receivable — unaffected by how many invoices/lines the payment settles.</summary>
    private static async Task<List<VoucherLineInput>> BuildVoucherLinesAsync(
        AegisDbContext db, int bankAccountId, string customerName, string receiptNo, string? narration, decimal amount)
    {
        var ar = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.AccountsReceivable);
        return new List<VoucherLineInput>
        {
            new(bankAccountId, null, narration, amount, 0),
            new(ar.Id, null, $"{customerName} — {receiptNo}", 0, amount),
        };
    }

    /// <summary>
    /// Next receipt number, scoped to the CustomerReceipts table itself (not the JournalVoucher
    /// sequence) so a still-unposted draft's number can never collide with one issued directly
    /// through <see cref="CreateAndPostAsync"/> — mirrors <c>SalesInvoiceService.NextInvoiceNoAsync</c>.
    /// </summary>
    private static async Task<string> NextReceiptNoAsync(AegisDbContext db, int year)
    {
        var existing = await db.CustomerReceipts.Select(r => r.ReceiptNo).ToListAsync();
        return JournalPoster.NextDocNo(existing, "RV", year);
    }
}
