using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>How much of a payment is applied to one specific invoice line/charge. Which invoice
/// a line belongs to is implicit (via <see cref="PurchaseInvoiceLine.PurchaseInvoiceId"/>), so this
/// type works unchanged whether a payment targets one invoice or spans several.</summary>
public record PaymentLineAllocationInput(int PurchaseInvoiceLineId, decimal Amount);

public class VendorPaymentService
{
    /// <summary>Hard cap on an attached document's size (5 MB) — stored inline in the database.</summary>
    public const int MaxAttachmentBytes = 5 * 1024 * 1024;

    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;
    public VendorPaymentService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    public async Task<List<VendorPayment>> GetRecentAsync(int take = 50)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.VendorPayments.AsNoTracking()
            .Include(p => p.Vendor).Include(p => p.BankAccount).Include(p => p.PurchaseInvoice)
            .Include(p => p.Allocations).ThenInclude(a => a.PurchaseInvoiceLine).ThenInclude(l => l.PurchaseInvoice)
            .OrderByDescending(p => p.Date).ThenByDescending(p => p.Id)
            .Take(take).ToListAsync();
    }

    /// <summary>Every payment for one vendor, for the vendor detail view's Payments tab.</summary>
    public async Task<List<VendorPayment>> GetByVendorAsync(int vendorId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.VendorPayments.AsNoTracking()
            .Include(p => p.BankAccount).Include(p => p.PurchaseInvoice)
            .Where(p => p.VendorId == vendorId)
            .OrderByDescending(p => p.Date).ThenByDescending(p => p.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Creates and posts a vendor payment in one transaction, generating the GL voucher
    /// (Dr Accounts Payable / Cr bank). Two ways to target invoices:
    ///
    /// - <paramref name="purchaseInvoiceId"/> set: settles that one invoice (unchanged, original
    ///   behavior). When it has more than one billable (Net &gt; 0) line, <paramref name="allocations"/>
    ///   is required and must sum to exactly <paramref name="amount"/>; a single-billable-line
    ///   invoice auto-allocates the whole amount to that line if no allocations are given.
    /// - <paramref name="purchaseInvoiceId"/> null with non-empty <paramref name="allocations"/>: a
    ///   multi-invoice payment — one payment settling parts of several different bills at once.
    ///   Each invoice referenced must get an explicit allocation entry (no auto-allocate here);
    ///   the total across every invoice must still sum to exactly <paramref name="amount"/>.
    /// - Both null/empty: a pure on-account payment, unattached to any invoice.
    /// </summary>
    public async Task<VendorPayment> CreateAndPostAsync(
        int vendorId, int? purchaseInvoiceId, DateOnly date, int fiscalPeriodId,
        int bankAccountId, decimal amount, string? narration, string createdBy, DateTime nowUtc,
        IEnumerable<PaymentLineAllocationInput>? allocations = null, PaymentMode paymentMode = PaymentMode.Cash,
        string? referenceNo = null, DateOnly? chequeDate = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var vendor = await db.Vendors.FindAsync(vendorId)
            ?? throw new PostingException("Vendor not found.");
        var inputs = allocations?.Where(a => a.Amount > 0).ToList() ?? new List<PaymentLineAllocationInput>();

        var (resolvedInvoiceId, lineAllocations) = purchaseInvoiceId is int invId
            ? (invId, await ValidateSingleInvoiceAsync(db, await LoadInvoiceAsync(db, invId, vendorId), amount, inputs))
            : await ValidateMultiInvoiceAsync(db, vendorId, amount, inputs);

        var paymentNo = await NextPaymentNoAsync(db, date.Year);

        var payment = new VendorPayment
        {
            PaymentNo = paymentNo,
            VendorId = vendorId,
            PurchaseInvoiceId = resolvedInvoiceId,
            Date = date,
            FiscalPeriodId = fiscalPeriodId,
            BankAccountId = bankAccountId,
            Amount = amount,
            PaymentMode = paymentMode,
            ReferenceNo = string.IsNullOrWhiteSpace(referenceNo) ? null : referenceNo.Trim(),
            ChequeDate = chequeDate,
            Narration = string.IsNullOrWhiteSpace(narration) ? $"Vendor payment — {vendor.Name}" : narration,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
        };
        payment.Allocations = lineAllocations;

        payment.Post(nowUtc); // domain validation

        payment.JournalVoucher = await JournalPoster.PostAsync(
            db, _current.CanPost, VoucherType.Payment, paymentNo, date, fiscalPeriodId,
            payment.Narration, paymentNo, createdBy,
            await BuildVoucherLinesAsync(db, bankAccountId, vendor.Name, paymentNo, payment.Narration, amount), nowUtc);

        db.VendorPayments.Add(payment);
        await JournalPoster.SaveAndCommitAsync(db, tx);
        return payment;
    }

    /// <summary>
    /// Saves a payment as a Draft: no GL voucher, nothing touches the ledger, and the intended
    /// allocations are stored as-is without validation — outstanding balances can move between now
    /// and when it's actually posted, so the real checks run fresh in <see cref="PostDraftAsync"/>.
    /// </summary>
    public async Task<VendorPayment> CreateDraftAsync(
        int vendorId, int? purchaseInvoiceId, DateOnly date, int fiscalPeriodId,
        int bankAccountId, decimal amount, string? narration, string createdBy, DateTime nowUtc,
        IEnumerable<PaymentLineAllocationInput>? allocations = null, PaymentMode paymentMode = PaymentMode.Cash,
        string? referenceNo = null, DateOnly? chequeDate = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var vendor = await db.Vendors.FindAsync(vendorId)
            ?? throw new PostingException("Vendor not found.");

        var payment = new VendorPayment
        {
            PaymentNo = await NextPaymentNoAsync(db, date.Year),
            VendorId = vendorId,
            PurchaseInvoiceId = purchaseInvoiceId,
            Date = date,
            FiscalPeriodId = fiscalPeriodId,
            BankAccountId = bankAccountId,
            Amount = amount,
            PaymentMode = paymentMode,
            ReferenceNo = string.IsNullOrWhiteSpace(referenceNo) ? null : referenceNo.Trim(),
            ChequeDate = chequeDate,
            Narration = string.IsNullOrWhiteSpace(narration) ? $"Vendor payment — {vendor.Name}" : narration,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
            Status = VoucherStatus.Draft,
            Allocations = (allocations ?? Enumerable.Empty<PaymentLineAllocationInput>())
                .Where(a => a.Amount > 0)
                .Select(a => new VendorPaymentAllocation { PurchaseInvoiceLineId = a.PurchaseInvoiceLineId, Amount = a.Amount })
                .ToList(),
        };

        db.VendorPayments.Add(payment);
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return payment;
    }

    /// <summary>Posts a previously-saved draft, re-validating its allocations fresh and generating its GL voucher under the same payment number.</summary>
    public async Task<VendorPayment> PostDraftAsync(int paymentId, string postedBy, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var payment = await db.VendorPayments.Include(p => p.Allocations).Include(p => p.Vendor)
            .FirstOrDefaultAsync(p => p.Id == paymentId)
            ?? throw new PostingException("Payment not found.");
        if (payment.Status != VoucherStatus.Draft)
            throw new PostingException("Only draft payments can be posted.");

        var requested = payment.Allocations.Select(a => new PaymentLineAllocationInput(a.PurchaseInvoiceLineId, a.Amount)).ToList();

        var (resolvedInvoiceId, validated) = payment.PurchaseInvoiceId is int invId
            ? (invId, await ValidateSingleInvoiceAsync(db, await LoadInvoiceAsync(db, invId, payment.VendorId), payment.Amount, requested))
            : await ValidateMultiInvoiceAsync(db, payment.VendorId, payment.Amount, requested);

        payment.PurchaseInvoiceId = resolvedInvoiceId;
        payment.Allocations.Clear();
        foreach (var a in validated) payment.Allocations.Add(a);

        payment.Post(nowUtc); // domain validation

        payment.JournalVoucher = await JournalPoster.PostAsync(
            db, _current.CanPost, VoucherType.Payment, payment.PaymentNo, payment.Date, payment.FiscalPeriodId,
            payment.Narration, payment.PaymentNo, postedBy,
            await BuildVoucherLinesAsync(db, payment.BankAccountId, payment.Vendor.Name, payment.PaymentNo, payment.Narration, payment.Amount),
            nowUtc);

        await JournalPoster.SaveAndCommitAsync(db, tx);
        return payment;
    }

    /// <summary>Voids a draft payment. Posted payments already hit the ledger — reverse those with a Journal instead.</summary>
    public async Task VoidDraftAsync(int paymentId)
    {
        if (!_current.CanPost)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var payment = await db.VendorPayments.FirstOrDefaultAsync(p => p.Id == paymentId)
            ?? throw new PostingException("Payment not found.");
        if (payment.Status != VoucherStatus.Draft)
            throw new PostingException("Only draft payments can be voided — a posted payment already hit the ledger.");

        payment.Status = VoucherStatus.Void;
        await db.SaveChangesAsync();
    }

    /// <summary>One payment with full detail (Vendor, BankAccount, Allocations) — for the detail/email views.</summary>
    public async Task<VendorPayment?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.VendorPayments.AsNoTracking()
            .Include(p => p.Vendor).Include(p => p.BankAccount).Include(p => p.JournalVoucher)
            .Include(p => p.Allocations).ThenInclude(a => a.PurchaseInvoiceLine).ThenInclude(l => l.PurchaseInvoice)
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    /// <summary>
    /// Attaches (or replaces) the payment's single supporting document (e.g. a scanned cheque).
    /// Allowed regardless of the payment's posting status.
    /// </summary>
    public async Task SetAttachmentAsync(int paymentId, string fileName, string contentType, byte[] data)
    {
        if (data.Length > MaxAttachmentBytes)
            throw new PostingException($"Attachment is too large — the limit is {MaxAttachmentBytes / (1024 * 1024)} MB.");

        await using var db = await _dbf.CreateDbContextAsync();
        var payment = await db.VendorPayments.FirstOrDefaultAsync(p => p.Id == paymentId)
            ?? throw new PostingException("Payment not found.");

        payment.AttachmentFileName = fileName;
        payment.AttachmentContentType = contentType;
        payment.AttachmentData = data;
        await db.SaveChangesAsync();
    }

    public async Task RemoveAttachmentAsync(int paymentId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var payment = await db.VendorPayments.FirstOrDefaultAsync(p => p.Id == paymentId)
            ?? throw new PostingException("Payment not found.");

        payment.AttachmentFileName = null;
        payment.AttachmentContentType = null;
        payment.AttachmentData = null;
        await db.SaveChangesAsync();
    }

    public async Task<(string FileName, string ContentType, byte[] Data)?> GetAttachmentAsync(int paymentId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var payment = await db.VendorPayments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId);
        if (payment?.AttachmentData is null || payment.AttachmentFileName is null) return null;
        return (payment.AttachmentFileName, payment.AttachmentContentType ?? "application/octet-stream", payment.AttachmentData);
    }

    private static async Task<PurchaseInvoice> LoadInvoiceAsync(AegisDbContext db, int invoiceId, int vendorId)
    {
        await LockInvoiceRowAsync(db, invoiceId);
        var invoice = await db.PurchaseInvoices.AsNoTracking().Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Purchase invoice not found.");
        if (invoice.VendorId != vendorId)
            throw new PostingException("Purchase invoice belongs to a different vendor.");
        if (invoice.Status != VoucherStatus.Posted)
            throw new PostingException("Only posted purchase invoices can be settled.");
        return invoice;
    }

    /// <summary>Legacy single-invoice validation — unchanged behavior. Requires a per-line split
    /// whenever the invoice has more than one billable line; auto-allocates a single billable line.</summary>
    private static async Task<List<VendorPaymentAllocation>> ValidateSingleInvoiceAsync(
        AegisDbContext db, PurchaseInvoice invoice, decimal amount, List<PaymentLineAllocationInput> inputs)
    {
        await CheckInvoiceOutstandingAsync(db, invoice, amount);

        var billableLines = invoice.Lines.Where(l => l.Net > 0).ToList();
        if (billableLines.Count > 1)
        {
            if (inputs.Count == 0)
                throw new PostingException(
                    $"{invoice.InvoiceNo} has multiple charges — specify how this payment is allocated across its lines.");
            if (inputs.Sum(a => a.Amount) != amount)
                throw new PostingException(
                    $"Line allocations ({inputs.Sum(a => a.Amount):N2}) must add up to the payment amount ({amount:N2}).");
            return await ValidateLineCapsAsync(db, billableLines, inputs);
        }
        if (billableLines.Count == 1)
            return new List<VendorPaymentAllocation> { new() { PurchaseInvoiceLineId = billableLines[0].Id, Amount = amount } };
        return new List<VendorPaymentAllocation>();
    }

    /// <summary>
    /// New multi-invoice validation — one payment spanning several different invoices, each with
    /// an explicit line-level allocation (no auto-allocate convenience here; the caller always has
    /// each invoice's own line list in hand). Invoice groups are processed in ascending invoice-id
    /// order so two overlapping multi-invoice payments never contend for locks in opposite order.
    /// </summary>
    private static async Task<(int? ResolvedPurchaseInvoiceId, List<VendorPaymentAllocation> Allocations)> ValidateMultiInvoiceAsync(
        AegisDbContext db, int vendorId, decimal amount, List<PaymentLineAllocationInput> inputs)
    {
        if (inputs.Count == 0)
            return (null, new List<VendorPaymentAllocation>()); // pure on-account

        if (inputs.Sum(a => a.Amount) != amount)
            throw new PostingException(
                $"Line allocations ({inputs.Sum(a => a.Amount):N2}) must add up to the payment amount ({amount:N2}).");

        // Note: no .Include(l => l.PurchaseInvoice).ThenInclude(i => i.Lines) here — that Include
        // path cycles back to PurchaseInvoiceLine itself, which EF Core rejects on no-tracking
        // queries. Each invoice's full line list is instead fetched separately, per group, below.
        //
        // Routed through the company-scoped PurchaseInvoices (not db.PurchaseInvoiceLines
        // directly) — otherwise a lineId belonging to another company would resolve here and leak
        // that company's invoice number in the "belongs to a different vendor" message below.
        var lineIds = inputs.Select(a => a.PurchaseInvoiceLineId).Distinct().ToList();
        var lines = await db.PurchaseInvoices.AsNoTracking()
            .SelectMany(i => i.Lines).Include(l => l.PurchaseInvoice)
            .Where(l => lineIds.Contains(l.Id))
            .ToListAsync();
        if (lines.Count != lineIds.Count)
            throw new PostingException("An allocation refers to a line that doesn't exist.");

        var byInvoice = lines.GroupBy(l => l.PurchaseInvoiceId).OrderBy(g => g.Key).ToList();

        var result = new List<VendorPaymentAllocation>();
        foreach (var group in byInvoice)
        {
            await LockInvoiceRowAsync(db, group.Key);
            var invoice = group.First().PurchaseInvoice;
            if (invoice.VendorId != vendorId)
                throw new PostingException($"Invoice {invoice.InvoiceNo} belongs to a different vendor.");
            if (invoice.Status != VoucherStatus.Posted)
                throw new PostingException($"Only posted invoices can be settled ({invoice.InvoiceNo}).");

            var thisInvoicesLineIds = group.Select(l => l.Id).ToHashSet();
            var thisInvoicesInputs = inputs.Where(a => thisInvoicesLineIds.Contains(a.PurchaseInvoiceLineId)).ToList();
            var groupAmount = thisInvoicesInputs.Sum(a => a.Amount);

            // Fetch this invoice's full line set explicitly and wire it up before computing
            // TotalGross — the query above only pulled in whichever lines were referenced by
            // the caller's allocations, so `invoice.Lines` isn't reliably complete without this.
            var allLinesForInvoice = await db.PurchaseInvoiceLines.AsNoTracking()
                .Where(l => l.PurchaseInvoiceId == invoice.Id)
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
    /// so two simultaneous payments against the same invoice serialize instead of both reading the
    /// same outstanding balance and both passing. No-op on Sqlite or outside an ambient
    /// transaction. NOT yet exercised against a live Postgres server — verify with a real
    /// concurrency test before relying on it in production.
    /// </summary>
    private static async Task LockInvoiceRowAsync(AegisDbContext db, int invoiceId)
    {
        if (!db.Database.IsNpgsql() || db.Database.CurrentTransaction is null) return;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"PurchaseInvoices\" WHERE \"Id\" = {invoiceId} FOR UPDATE");
    }

    /// <summary>
    /// This invoice's own outstanding must not be exceeded by <paramref name="amountBeingApplied"/>.
    /// Derived from <see cref="VendorPaymentAllocation"/> (joined through the line it targets) —
    /// never from <see cref="VendorPayment.PurchaseInvoiceId"/>, which can't represent a payment
    /// that also settles other invoices and would otherwise under-count what's already been
    /// applied here.
    /// </summary>
    private static async Task CheckInvoiceOutstandingAsync(AegisDbContext db, PurchaseInvoice invoice, decimal amountBeingApplied)
    {
        var allocated = (await db.VendorPaymentAllocations.AsNoTracking()
            .Where(a => a.PurchaseInvoiceLine.PurchaseInvoiceId == invoice.Id && a.VendorPayment.Status == VoucherStatus.Posted)
            .Select(a => a.Amount)
            .ToListAsync()).Sum();
        var debited = (await db.DebitNotes.Include(d => d.Lines)
            .Where(d => d.PurchaseInvoiceId == invoice.Id && d.Status == VoucherStatus.Posted)
            .ToListAsync()).Sum(d => d.TotalGross);
        var outstanding = invoice.TotalGross - allocated - debited;
        if (amountBeingApplied > outstanding)
            throw new PostingException(
                $"Amount {amountBeingApplied:N2} exceeds the outstanding {outstanding:N2} on {invoice.InvoiceNo}.");
    }

    /// <summary>Each input must belong to one of <paramref name="lines"/> and not push that line's cumulative posted allocations past its own Gross.</summary>
    private static async Task<List<VendorPaymentAllocation>> ValidateLineCapsAsync(
        AegisDbContext db, List<PurchaseInvoiceLine> lines, List<PaymentLineAllocationInput> inputs)
    {
        var lineIds = lines.Select(l => l.Id).ToHashSet();
        if (inputs.Any(a => !lineIds.Contains(a.PurchaseInvoiceLineId)))
            throw new PostingException("An allocation refers to a line that is not part of this invoice.");

        var idsList = lines.Select(l => l.Id).ToList();
        var existingByLine = (await db.VendorPaymentAllocations
            .Where(a => idsList.Contains(a.PurchaseInvoiceLineId) && a.VendorPayment.Status == VoucherStatus.Posted)
            .Select(a => new { a.PurchaseInvoiceLineId, a.Amount })
            .ToListAsync())
            .GroupBy(a => a.PurchaseInvoiceLineId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

        var result = new List<VendorPaymentAllocation>();
        foreach (var a in inputs)
        {
            var line = lines.First(l => l.Id == a.PurchaseInvoiceLineId);
            var already = existingByLine.GetValueOrDefault(a.PurchaseInvoiceLineId);
            if (already + a.Amount > line.Gross)
                throw new PostingException(
                    $"Allocating {a.Amount:N2} to '{line.Description}' would exceed its remaining balance ({line.Gross - already:N2}).");
            result.Add(new VendorPaymentAllocation { PurchaseInvoiceLineId = a.PurchaseInvoiceLineId, Amount = a.Amount });
        }
        return result;
    }

    /// <summary>Dr Accounts Payable for the amount, Cr bank — unaffected by how many invoices/lines the payment settles.</summary>
    private static async Task<List<VoucherLineInput>> BuildVoucherLinesAsync(
        AegisDbContext db, int bankAccountId, string vendorName, string paymentNo, string? narration, decimal amount)
    {
        var ap = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.AccountsPayable);
        return new List<VoucherLineInput>
        {
            new(ap.Id, null, $"{vendorName} — {paymentNo}", amount, 0),
            new(bankAccountId, null, narration, 0, amount),
        };
    }

    /// <summary>
    /// Next payment number, scoped to the VendorPayments table itself (not the JournalVoucher
    /// sequence) so a still-unposted draft's number can never collide with one issued directly
    /// through <see cref="CreateAndPostAsync"/> — mirrors <c>ReceiptService.NextReceiptNoAsync</c>.
    /// </summary>
    private static async Task<string> NextPaymentNoAsync(AegisDbContext db, int year)
    {
        var existing = await db.VendorPayments.Select(p => p.PaymentNo).ToListAsync();
        return JournalPoster.NextDocNo(existing, "PV", year);
    }
}
