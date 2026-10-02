using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Input line for creating a purchase invoice from the UI. <paramref name="NonTaxableAmount"/>
/// is only ever nonzero for the "PRO Service" invoice format — standard entry leaves it at 0.</summary>
public record PurchaseLineInput(string Description, int ExpenseAccountId, int? CostCenterId,
    decimal Quantity, decimal UnitPrice, decimal VatRate, decimal NonTaxableAmount = 0);

public class PurchaseInvoiceService
{
    public const int MaxAttachmentBytes = 5 * 1024 * 1024;

    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;
    public PurchaseInvoiceService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    public async Task<List<PurchaseInvoice>> GetRecentAsync(int take = 50)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.PurchaseInvoices.AsNoTracking()
            .Include(i => i.Vendor).Include(i => i.Lines).Include(i => i.JournalVoucher)
            .OrderByDescending(i => i.Date).ThenByDescending(i => i.Id)
            .Take(take).ToListAsync();
    }

    /// <summary>Every invoice, for the invoice list page.</summary>
    public async Task<List<PurchaseInvoice>> GetAllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.PurchaseInvoices.AsNoTracking()
            .Include(i => i.Vendor).Include(i => i.Lines).Include(i => i.JournalVoucher)
            .OrderByDescending(i => i.Date).ThenByDescending(i => i.Id)
            .ToListAsync();
    }

    /// <summary>One invoice with full detail (Vendor, Lines, JournalVoucher) for the detail page.</summary>
    public async Task<PurchaseInvoice?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.PurchaseInvoices.AsNoTracking()
            .Include(i => i.Vendor).Include(i => i.Lines).Include(i => i.JournalVoucher).Include(i => i.CostCenter)
            .FirstOrDefaultAsync(i => i.Id == id);
    }

    /// <summary>
    /// This invoice's remaining balance — its gross total minus posted vendor payments (via
    /// <see cref="VendorPaymentAllocation"/>, joined through the line each allocation targets —
    /// never <see cref="VendorPayment.PurchaseInvoiceId"/>, which can't represent a payment
    /// spanning multiple invoices) and debit notes applied against it. Exposed here purely for
    /// display on the detail page.
    /// </summary>
    public async Task<decimal> GetOutstandingAsync(int invoiceId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.PurchaseInvoices.AsNoTracking().Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");

        var paid = (await db.VendorPaymentAllocations.AsNoTracking()
            .Where(a => a.PurchaseInvoiceLine.PurchaseInvoiceId == invoiceId && a.VendorPayment.Status == VoucherStatus.Posted)
            .Select(a => a.Amount).ToListAsync()).Sum();
        var debited = (await db.DebitNotes.AsNoTracking().Include(d => d.Lines)
            .Where(d => d.PurchaseInvoiceId == invoiceId && d.Status == VoucherStatus.Posted)
            .ToListAsync()).Sum(d => d.TotalGross);

        return invoice.TotalGross - paid - debited;
    }

    /// <summary>
    /// Per-line paid/balance breakdown for one invoice, from posted vendor payment allocations —
    /// so staff can see which specific charge on a multi-line bill is settled vs still owing.
    /// </summary>
    public async Task<List<PurchaseInvoiceLineBalance>> GetLineBalancesAsync(int invoiceId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        // Route through the company-scoped PurchaseInvoices — PurchaseInvoiceLine has no
        // CompanyId/query filter of its own, so querying db.PurchaseInvoiceLines directly by
        // invoiceId would return another company's lines for a guessed/foreign id.
        var lines = await db.PurchaseInvoices.AsNoTracking()
            .SelectMany(i => i.Lines)
            .Where(l => l.PurchaseInvoiceId == invoiceId)
            .OrderBy(l => l.LineNo)
            .ToListAsync();

        var lineIds = lines.Select(l => l.Id).ToList();
        var allocated = (await db.VendorPaymentAllocations.AsNoTracking()
            .Where(a => lineIds.Contains(a.PurchaseInvoiceLineId) && a.VendorPayment.Status == VoucherStatus.Posted)
            .Select(a => new { a.PurchaseInvoiceLineId, a.Amount })
            .ToListAsync())
            .GroupBy(a => a.PurchaseInvoiceLineId)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));

        return lines.Select(l => new PurchaseInvoiceLineBalance(
            l.Id, l.Description, l.Gross, allocated.GetValueOrDefault(l.Id))).ToList();
    }

    /// <summary>
    /// Looks for an already-recorded, non-void bill from the same vendor carrying the same
    /// vendor-supplied reference number — the same physical bill entered (or scanned) twice is the
    /// most likely accident here, not a coincidence, since a vendor's own invoice numbers don't
    /// repeat. Advisory only: returns the existing invoice's document number so the caller can warn,
    /// never blocks anything itself — a legitimate re-entry after a correction is still possible.
    /// </summary>
    public async Task<string?> FindDuplicateAsync(int vendorId, string? vendorRef)
    {
        if (vendorId == 0 || string.IsNullOrWhiteSpace(vendorRef)) return null;

        await using var db = await _dbf.CreateDbContextAsync();
        var trimmedRef = vendorRef.Trim();
        var existing = await db.PurchaseInvoices.AsNoTracking()
            .Where(i => i.VendorId == vendorId && i.Status != VoucherStatus.Void
                        && i.VendorRef != null && i.VendorRef.ToLower() == trimmedRef.ToLower())
            .OrderByDescending(i => i.Date).ThenByDescending(i => i.Id)
            .Select(i => i.InvoiceNo)
            .FirstOrDefaultAsync();
        return existing;
    }

    /// <summary>
    /// Creates and posts a purchase invoice in one transaction: the invoice, its lines and the
    /// generated GL voucher (Dr expense/asset net per line / Dr VAT input / Cr AP gross) are
    /// persisted atomically. Invoice number and voucher number are the same document number.
    /// </summary>
    public async Task<PurchaseInvoice> CreateAndPostAsync(
        int vendorId, string? vendorRef, DateOnly date, int fiscalPeriodId, string? narration,
        string createdBy, IEnumerable<PurchaseLineInput> lines, DateTime nowUtc, string? lpoNo = null,
        string? notes = null, int? costCenterId = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var vendor = await db.Vendors.FindAsync(vendorId)
            ?? throw new PostingException("Vendor not found.");

        var invoiceNo = await JournalPoster.NextDocNoAsync(db, "PINV", date.Year);

        var invoice = new PurchaseInvoice
        {
            InvoiceNo = invoiceNo,
            VendorRef = string.IsNullOrWhiteSpace(vendorRef) ? null : vendorRef.Trim(),
            LpoNo = string.IsNullOrWhiteSpace(lpoNo) ? null : lpoNo.Trim(),
            VendorId = vendorId,
            CostCenterId = costCenterId,
            Date = date,
            DueDate = date.AddDays(vendor.PaymentTermsDays),
            FiscalPeriodId = fiscalPeriodId,
            Narration = string.IsNullOrWhiteSpace(narration) ? $"Purchase invoice — {vendor.Name}" : narration,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
        };

        var no = 1;
        foreach (var l in lines)
            invoice.Lines.Add(new PurchaseInvoiceLine
            {
                LineNo = no++,
                Description = l.Description,
                ExpenseAccountId = l.ExpenseAccountId,
                // A line's own Cost Center wins when set (standard format); otherwise it falls
                // back to the invoice-level one (PRO Service format), so the same posting path
                // serves both without branching on which format was used to build these inputs.
                CostCenterId = l.CostCenterId ?? costCenterId,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                VatRate = l.VatRate,
                NonTaxableAmount = l.NonTaxableAmount,
                // Every line created from here on uses the corrected per-unit interpretation of
                // NonTaxableAmount (see PurchaseInvoiceLine.NonTaxablePerUnit) — not user-facing,
                // just fixes a bug where a multi-quantity govt fee line understated the total.
                NonTaxablePerUnit = true,
            });

        invoice.Post(nowUtc); // domain validation (positive totals, valid lines, due date)

        // Build the GL voucher: Dr expense per line, Dr VAT input for the total tax, Cr AP for the gross.
        var ap = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.AccountsPayable);
        var voucherLines = new List<VoucherLineInput>();
        voucherLines.AddRange(invoice.Lines.Where(l => l.Net > 0).Select(l =>
            new VoucherLineInput(l.ExpenseAccountId, l.CostCenterId, l.Description, l.Net, 0)));
        if (invoice.TotalVat > 0)
        {
            var vat = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.VatInput);
            voucherLines.Add(new VoucherLineInput(vat.Id, null, $"Input VAT — {invoiceNo}", invoice.TotalVat, 0));
        }
        voucherLines.Add(new VoucherLineInput(ap.Id, null, $"{vendor.Name} — {invoiceNo}", 0, invoice.TotalGross));

        invoice.JournalVoucher = await JournalPoster.PostAsync(
            db, _current.CanPost, VoucherType.PurchaseInvoice, invoiceNo, date, fiscalPeriodId,
            invoice.Narration, invoiceNo, createdBy, voucherLines, nowUtc);

        db.PurchaseInvoices.Add(invoice);
        await JournalPoster.SaveAndCommitAsync(db, tx);
        return invoice;
    }

    /// <summary>
    /// Attaches (or replaces) the invoice's single supporting document (e.g. the vendor's own
    /// bill/receipt). Allowed regardless of posting status — attaching a document doesn't touch
    /// financial data.
    /// </summary>
    public async Task SetAttachmentAsync(int invoiceId, string fileName, string contentType, byte[] data)
    {
        if (data.Length > MaxAttachmentBytes)
            throw new PostingException($"Attachment is too large — the limit is {MaxAttachmentBytes / (1024 * 1024)} MB.");

        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.PurchaseInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");

        invoice.AttachmentFileName = fileName;
        invoice.AttachmentContentType = contentType;
        invoice.AttachmentData = data;
        await db.SaveChangesAsync();
    }

    public async Task RemoveAttachmentAsync(int invoiceId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.PurchaseInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");

        invoice.AttachmentFileName = null;
        invoice.AttachmentContentType = null;
        invoice.AttachmentData = null;
        await db.SaveChangesAsync();
    }

    public async Task<(string FileName, string ContentType, byte[] Data)?> GetAttachmentAsync(int invoiceId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.PurchaseInvoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId);
        if (invoice?.AttachmentData is null || invoice.AttachmentFileName is null) return null;
        return (invoice.AttachmentFileName, invoice.AttachmentContentType ?? "application/octet-stream", invoice.AttachmentData);
    }

    /// <summary>
    /// Brings forward one historical/opening bill from a prior system — see
    /// <see cref="SalesInvoiceService.ImportOpeningInvoiceAsync"/> for the full rationale (same
    /// shape, mirrored to the AP side): a real PurchaseInvoice tied to <paramref name="vendorId"/>
    /// (shows on that vendor's Bills tab, settleable by a normal Vendor Payment going forward),
    /// posted Dr the opening-balance equity account (31010) / Cr Accounts Payable instead of a
    /// real expense account, under <see cref="VoucherType.Opening"/> and dated at the earliest
    /// fiscal period so it never re-expenses something the old system already reported and never
    /// distorts a real period's P&amp;L. <paramref name="amount"/> should be the balance still owed
    /// today, not the bill's original total.
    /// </summary>
    public async Task<PurchaseInvoice> ImportOpeningInvoiceAsync(
        int vendorId, string? reference, DateOnly date, DateOnly? dueDate, decimal amount,
        string? notes, string importedBy, DateTime nowUtc)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        if (amount <= 0) throw new PostingException("Outstanding amount must be positive.");

        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var vendor = await db.Vendors.FindAsync(vendorId)
            ?? throw new PostingException("Vendor not found.");
        var openingPeriod = await db.FiscalPeriods.OrderBy(p => p.StartDate).FirstOrDefaultAsync()
            ?? throw new PostingException("No fiscal period is defined for the opening entry.");
        var equity = await db.Accounts.FirstOrDefaultAsync(a => a.Code == "31010")
            ?? throw new PostingException("Opening-balance equity account 31010 is missing.");

        var invoiceNo = await JournalPoster.NextDocNoAsync(db, "PINV", date.Year);
        var narration = $"Imported opening balance — {vendor.Name}" +
                         (string.IsNullOrWhiteSpace(reference) ? "" : $" (Ref: {reference.Trim()})");

        var invoice = new PurchaseInvoice
        {
            InvoiceNo = invoiceNo,
            VendorRef = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(),
            VendorId = vendorId,
            Date = date,
            DueDate = dueDate ?? date,
            FiscalPeriodId = openingPeriod.Id,
            Narration = narration,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            CreatedBy = importedBy,
            CreatedAtUtc = nowUtc,
        };
        invoice.Lines.Add(new PurchaseInvoiceLine
        {
            LineNo = 1,
            Description = "Opening balance brought forward",
            ExpenseAccountId = equity.Id,
            Quantity = 1,
            UnitPrice = amount,
            VatRate = 0,
        });

        invoice.Post(nowUtc); // domain validation (positive totals, valid lines, due date)

        var ap = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.AccountsPayable);
        var voucherLines = new List<VoucherLineInput>
        {
            new(equity.Id, null, "Opening balance brought forward", invoice.TotalGross, 0),
            new(ap.Id, null, $"{vendor.Name} — {invoiceNo}", 0, invoice.TotalGross),
        };

        // Dated at openingPeriod.StartDate, not the invoice's own (earlier) historical date —
        // same reasoning as the sales-side import and ChartOfAccountsService's opening voucher.
        invoice.JournalVoucher = await JournalPoster.PostAsync(
            db, _current.CanPost, VoucherType.Opening, invoiceNo, openingPeriod.StartDate, openingPeriod.Id,
            narration, invoiceNo, importedBy, voucherLines, nowUtc);

        db.PurchaseInvoices.Add(invoice);
        await JournalPoster.SaveAndCommitAsync(db, tx);
        return invoice;
    }
}
