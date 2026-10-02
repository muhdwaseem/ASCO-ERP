using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>
/// Backs the cross-invoice Transactions view — every Sales Invoice line, company-wide, flattened
/// into one list with fulfillment (Completed/Supplier) and payment (Paid From) attribution.
/// </summary>
public class TransactionService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly DirectExpenseService _directExpenses;
    private readonly PurchaseInvoiceService _purchaseInvoices;
    private readonly VendorPaymentService _vendorPayments;
    private readonly LedgerService _ledger;
    private readonly SalesInvoiceService _salesInvoices;
    private readonly ICurrentCompany _current;

    public TransactionService(
        IDbContextFactory<AegisDbContext> dbf, DirectExpenseService directExpenses,
        PurchaseInvoiceService purchaseInvoices, VendorPaymentService vendorPayments, LedgerService ledger,
        SalesInvoiceService salesInvoices, ICurrentCompany current)
    {
        _dbf = dbf;
        _directExpenses = directExpenses;
        _purchaseInvoices = purchaseInvoices;
        _vendorPayments = vendorPayments;
        _ledger = ledger;
        _salesInvoices = salesInvoices;
        _current = current;
    }

    public async Task<List<TransactionRow>> GetAllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();

        // Start from SalesInvoices (company-scoped) and flatten in memory — SalesInvoiceLine has
        // no CompanyId/query filter of its own, so querying db.SalesInvoiceLines directly would
        // leak every company's lines.
        var invoices = await db.SalesInvoices.AsNoTracking()
            .Include(i => i.Customer).ThenInclude(c => c.AssignedAgent)
            .Include(i => i.Lines).ThenInclude(l => l.Item)
            .Include(i => i.Lines).ThenInclude(l => l.Supplier)
            .Include(i => i.Lines).ThenInclude(l => l.AssignedToEmployee)
            .Where(i => i.Status != VoucherStatus.Void)
            .OrderByDescending(i => i.Date).ThenByDescending(i => i.Id)
            .ToListAsync();

        var lineIds = invoices.SelectMany(i => i.Lines).Select(l => l.Id).ToList();
        var paidFrom = (await db.ReceiptAllocations.AsNoTracking()
            .Where(a => lineIds.Contains(a.SalesInvoiceLineId) && a.CustomerReceipt.Status == VoucherStatus.Posted)
            .OrderByDescending(a => a.CustomerReceipt.Date).ThenByDescending(a => a.CustomerReceiptId)
            .Select(a => new { a.SalesInvoiceLineId, AccountName = a.CustomerReceipt.BankAccount.Name })
            .ToListAsync())
            .GroupBy(a => a.SalesInvoiceLineId)
            .ToDictionary(g => g.Key, g => g.First().AccountName); // most recently-dated receipt's account

        // A line completed via "Direct" (CompleteDirectAsync) or "Supplier" (CompleteSupplierAsync)
        // has a real posted document linked back to it — surfaced here so the UI can block Reopen.
        var linkedExpenses = await db.DirectExpenses.AsNoTracking()
            .Where(e => e.SourceSalesInvoiceLineId != null && lineIds.Contains(e.SourceSalesInvoiceLineId.Value)
                        && e.Status == VoucherStatus.Posted)
            .Select(e => new { LineId = e.SourceSalesInvoiceLineId!.Value, e.Id, DocNo = e.ExpenseNo })
            .ToListAsync();
        var linkedBills = await db.PurchaseInvoices.AsNoTracking()
            .Where(i => i.SourceSalesInvoiceLineId != null && lineIds.Contains(i.SourceSalesInvoiceLineId.Value)
                        && i.Status == VoucherStatus.Posted)
            .Select(i => new { LineId = i.SourceSalesInvoiceLineId!.Value, i.Id, DocNo = i.InvoiceNo })
            .ToListAsync();
        var linked = linkedExpenses.Select(x => (x.LineId, x.Id, x.DocNo))
            .Concat(linkedBills.Select(x => (x.LineId, x.Id, x.DocNo)))
            .ToDictionary(x => x.LineId, x => (x.Id, x.DocNo));

        var rows = new List<TransactionRow>();
        foreach (var inv in invoices)
            foreach (var l in inv.Lines.OrderBy(l => l.LineNo))
            {
                var le = linked.GetValueOrDefault(l.Id);
                rows.Add(new TransactionRow(
                    l.Id, $"{inv.InvoiceNo}-L{l.LineNo}", inv.Id, inv.InvoiceNo, inv.Date, inv.Status,
                    inv.Customer.Name, inv.SalesOrderRef, l.Description, l.Item?.Name, l.Item?.Kind,
                    l.Quantity, l.Gross, l.IsCompleted, l.CompletedAtUtc, l.CompletedBy,
                    l.SupplierId, l.Supplier?.Name, paidFrom.GetValueOrDefault(l.Id),
                    l.TaxableNet, l.Quantity * l.GovtFee, l.Vat, l.Quantity * l.BankCharge,
                    l.ApplicantReference, le.Id == 0 ? null : le.Id, le.DocNo,
                    l.AssignedToEmployeeId, l.AssignedToEmployee?.FullName,
                    inv.Customer.Salesperson, inv.Customer.AssignedAgent?.Name,
                    l.ExpiryDate, l.AttachmentFileName is not null));
            }
        return rows;
    }

    /// <summary>Marks one line complete/incomplete. Scoped through SalesInvoices so a lineId from
    /// another company can never be reached, even though SalesInvoiceLine has no filter of its own.
    /// Reopening a line that was completed via <see cref="CompleteDirectAsync"/> or
    /// <see cref="CompleteSupplierAsync"/> is blocked — both paths leave a real posted document
    /// linked to it, and there is no "void a posted document" capability anywhere in this app to
    /// unwind that safely; undoing it needs a correcting Journal Voucher instead.</summary>
    public async Task SetCompletionAsync(int lineId, bool isCompleted, string changedBy, DateTime nowUtc)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        await using var db = await _dbf.CreateDbContextAsync();
        var line = await db.SalesInvoices.SelectMany(i => i.Lines).FirstOrDefaultAsync(l => l.Id == lineId)
            ?? throw new PostingException("Transaction line not found.");

        if (!isCompleted)
        {
            var hasLinkedDoc = await db.DirectExpenses.AnyAsync(
                    e => e.SourceSalesInvoiceLineId == lineId && e.Status == VoucherStatus.Posted)
                || await db.PurchaseInvoices.AnyAsync(
                    i => i.SourceSalesInvoiceLineId == lineId && i.Status == VoucherStatus.Posted);
            if (hasLinkedDoc)
                throw new PostingException(
                    "This line was completed with a linked posted document — reopening it needs a correcting Journal Voucher, not a simple toggle.");
        }

        line.IsCompleted = isCompleted;
        line.CompletedAtUtc = isCompleted ? nowUtc : null;
        line.CompletedBy = isCompleted ? changedBy : null;
        await db.SaveChangesAsync();
    }

    /// <summary>Assigns (or clears) the vendor/subcontractor fulfilling one line.</summary>
    public async Task SetSupplierAsync(int lineId, int? supplierId)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        await using var db = await _dbf.CreateDbContextAsync();
        var line = await db.SalesInvoices.SelectMany(i => i.Lines).FirstOrDefaultAsync(l => l.Id == lineId)
            ?? throw new PostingException("Transaction line not found.");

        if (supplierId is int sid && !await db.Vendors.AnyAsync(v => v.Id == sid))
            throw new PostingException("Supplier not found.");

        line.SupplierId = supplierId;
        await db.SaveChangesAsync();
    }

    /// <summary>Assigns (or clears) the Employee tasked with delivering one PRO-service line —
    /// the manager-facing side of Task assignment. Affects commission attribution on this line's
    /// already-calculated <see cref="SalesCommissionRecord"/> only if the invoice is re-posted; a
    /// posted invoice's commission is not retroactively recalculated by reassigning the line.</summary>
    public async Task SetAssignedEmployeeAsync(int lineId, int? employeeId, string changedBy)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        await using var db = await _dbf.CreateDbContextAsync();
        var line = await db.SalesInvoices.SelectMany(i => i.Lines).FirstOrDefaultAsync(l => l.Id == lineId)
            ?? throw new PostingException("Transaction line not found.");

        if (employeeId is int eid && !await db.Employees.AnyAsync(e => e.Id == eid))
            throw new PostingException("Employee not found.");

        line.AssignedToEmployeeId = employeeId;
        await db.SaveChangesAsync();
    }

    /// <summary>Records the expiry date of the government document a completed PRO-service line
    /// actually produced (e.g. a trade license or visa) — surfaced on the Transactions view so
    /// upcoming renewals can be tracked. Can be set independently of completion status.</summary>
    public async Task SetLineExpiryDateAsync(int lineId, DateOnly? expiryDate)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        await using var db = await _dbf.CreateDbContextAsync();
        var line = await db.SalesInvoices.SelectMany(i => i.Lines).FirstOrDefaultAsync(l => l.Id == lineId)
            ?? throw new PostingException("Transaction line not found.");

        line.ExpiryDate = expiryDate;
        await db.SaveChangesAsync();
    }

    /// <summary>Attaches the completed document (e.g. a scanned license/visa copy) for one
    /// transaction line — thin pass-through to <see cref="SalesInvoiceService.SetLineAttachmentAsync"/>
    /// so the Transactions page doesn't need its own copy of the size/company-scoping logic. Guarded
    /// by CanPost here (the shared method itself isn't) to match every other write on this page.</summary>
    public Task SetLineAttachmentAsync(int lineId, string fileName, string contentType, byte[] data)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        return _salesInvoices.SetLineAttachmentAsync(lineId, fileName, contentType, data);
    }

    public Task<(string FileName, string ContentType, byte[] Data)?> GetLineAttachmentAsync(int lineId)
        => _salesInvoices.GetLineAttachmentAsync(lineId);

    public Task RemoveLineAttachmentAsync(int lineId)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        return _salesInvoices.RemoveLineAttachmentAsync(lineId);
    }

    /// <summary>An employee's own assigned lines — backs the ESS "My Tasks" page. Deliberately takes
    /// no <see cref="ICurrentCompany.CanPost"/> check: an Employee Self-Service login always has
    /// CanPost false by design (see <see cref="AegisErp.Web.EmployeePortalSession"/>), so ownership of
    /// the requested <paramref name="employeeId"/> is the only gate — company scoping still comes from
    /// the portal session's own <see cref="ICurrentCompany.CompanyId"/> on the SalesInvoices query.</summary>
    public async Task<List<TransactionRow>> GetForEmployeeAsync(int employeeId)
        => (await GetAllAsync()).Where(r => r.AssignedToEmployeeId == employeeId).ToList();

    /// <summary>Self-service complete/reopen for the employee a line is actually assigned to — the
    /// ESS counterpart of <see cref="SetCompletionAsync"/>, which requires CanPost and is therefore
    /// unreachable from a portal login. Ownership (<paramref name="employeeId"/> must match the line's
    /// own <see cref="SalesInvoiceLine.AssignedToEmployeeId"/>) stands in for the CanPost check.</summary>
    public async Task SetOwnCompletionAsync(int lineId, int employeeId, bool isCompleted, string changedBy, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var line = await db.SalesInvoices.SelectMany(i => i.Lines).FirstOrDefaultAsync(l => l.Id == lineId)
            ?? throw new PostingException("Transaction line not found.");
        if (line.AssignedToEmployeeId != employeeId)
            throw new PostingException("This task isn't assigned to you.");

        if (!isCompleted)
        {
            var hasLinkedDoc = await db.DirectExpenses.AnyAsync(
                    e => e.SourceSalesInvoiceLineId == lineId && e.Status == VoucherStatus.Posted)
                || await db.PurchaseInvoices.AnyAsync(
                    i => i.SourceSalesInvoiceLineId == lineId && i.Status == VoucherStatus.Posted);
            if (hasLinkedDoc)
                throw new PostingException("This line was completed with a linked posted document and can't be reopened here.");
        }

        line.IsCompleted = isCompleted;
        line.CompletedAtUtc = isCompleted ? nowUtc : null;
        line.CompletedBy = isCompleted ? changedBy : null;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Completes a PRO-service line by recording the government fee actually paid — creates and
    /// posts a real Direct Expense (Dr <see cref="WellKnownAccounts.GovtFeesExpense"/> / Cr the
    /// chosen bank account, VAT-exempt — UAE government fees sit outside VAT scope), links it back
    /// to this line, and marks the line complete. The amount is capped at what was billed for this
    /// line's Govt Fee (never allowed to exceed it — see <see cref="WellKnownAccounts.GovtFeesExpense"/>'s
    /// doc comment for the accepted v1 limitation when the actual amount paid is less than billed).
    /// A line can only go through this once — completing it a second time is rejected below, which
    /// is also what keeps the cap check meaningful without needing to sum prior postings.
    /// </summary>
    public async Task<DirectExpense> CompleteDirectAsync(
        int lineId, decimal govtFeeAmount, int govtPaymentAccountId, string? transactionId,
        DateOnly completionDate, string completedBy, DateTime nowUtc,
        (string FileName, string ContentType, byte[] Data)? attachment = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.SalesInvoices.Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.Lines.Any(l => l.Id == lineId))
            ?? throw new PostingException("Transaction line not found.");
        var line = invoice.Lines.Single(l => l.Id == lineId);

        if (invoice.Status != VoucherStatus.Posted)
            throw new PostingException("Only a posted invoice's line can be completed.");
        if (line.IsCompleted)
            throw new PostingException("This line is already completed.");
        if (completionDate < invoice.Date)
            throw new PostingException("Completion date cannot be before the invoice date.");

        var billed = Math.Round(line.Quantity * line.GovtFee, 2, MidpointRounding.AwayFromZero);
        if (govtFeeAmount <= 0)
            throw new PostingException("Enter the amount actually paid.");
        if (govtFeeAmount > billed)
            throw new PostingException($"Govt fee paid cannot exceed the billed amount ({billed:N2}).");

        var period = await _ledger.GetDefaultPeriodAsync(completionDate)
            ?? throw new PostingException("No fiscal period covers the completion date.");
        var expenseAccount = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.GovtFeesExpense);

        var expense = await _directExpenses.CreateAndPostAsync(
            vendorId: null, customerId: invoice.CustomerId, completionDate, period.Id, govtPaymentAccountId,
            reference: transactionId,
            narration: $"Government fee — {line.Description} — {invoice.InvoiceNo}-L{line.LineNo}",
            createdBy: completedBy, nowUtc,
            lines: new[] { new DirectExpenseLineInput(expenseAccount.Id, line.CostCenterId, line.Description, govtFeeAmount, ItemId: null, VatRate: 0m) },
            vendorInvoiceNo: null, amountsIncludeVat: false, payLater: false);

        if (attachment is { } att)
            await _directExpenses.SetAttachmentAsync(expense.Id, att.FileName, att.ContentType, att.Data);

        // Link the new expense back to the line, and mark it complete, in one save on the context
        // that's held `line` tracked since the top of this method.
        var trackedExpense = await db.DirectExpenses.FirstAsync(e => e.Id == expense.Id);
        trackedExpense.SourceSalesInvoiceLineId = lineId;
        line.IsCompleted = true;
        line.CompletedAtUtc = nowUtc;
        line.CompletedBy = completedBy;
        await db.SaveChangesAsync();

        expense.SourceSalesInvoiceLineId = lineId;
        return expense;
    }

    /// <summary>
    /// Completes a PRO-service line by recording the outsourced supplier's bill — creates and posts
    /// a real Purchase Invoice, links it back to this line, and marks the line complete. The bill
    /// can carry two components on separate lines (Bookkeeper &amp; Controller-reviewed — kept apart
    /// so <see cref="WellKnownAccounts.GovtFeesExpense"/> stays a clean at-cost mirror of what was
    /// billed to the customer): the government fee passed through this supplier (capped at what was
    /// billed, same rule as <see cref="CompleteDirectAsync"/>, but unlike that method 0 is allowed
    /// here — a supplier may charge purely their own handling fee with no separate govt component),
    /// and the supplier's own taxable handling/service charge
    /// (<see cref="WellKnownAccounts.SubcontractedProServicesExpense"/>) — a genuine new cost with
    /// no cap, since it has nothing to do with what the customer was billed. When <paramref name="payNow"/>
    /// is true, also creates and posts a Vendor Payment fully allocated to the new bill (Dr AP / Cr
    /// the chosen bank account) in the same call; otherwise the bill is left open on Accounts
    /// Payable, payable later through the ordinary Vendor Payment flow like any other bill.
    /// </summary>
    public async Task<PurchaseInvoice> CompleteSupplierAsync(
        int lineId, int supplierId, string? supplierInvoiceNo, decimal govtFeeAmount, decimal centerFeeAmount,
        bool applyVat, DateOnly completionDate, string completedBy, DateTime nowUtc,
        bool payNow, int? payFromAccountId, PaymentMode paymentMode, string? paymentReference,
        (string FileName, string ContentType, byte[] Data)? attachment = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.SalesInvoices.Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.Lines.Any(l => l.Id == lineId))
            ?? throw new PostingException("Transaction line not found.");
        var line = invoice.Lines.Single(l => l.Id == lineId);

        if (invoice.Status != VoucherStatus.Posted)
            throw new PostingException("Only a posted invoice's line can be completed.");
        if (line.IsCompleted)
            throw new PostingException("This line is already completed.");
        if (completionDate < invoice.Date)
            throw new PostingException("Completion date cannot be before the invoice date.");
        if (!await db.Vendors.AnyAsync(v => v.Id == supplierId))
            throw new PostingException("Supplier not found.");

        var billed = Math.Round(line.Quantity * line.GovtFee, 2, MidpointRounding.AwayFromZero);
        if (govtFeeAmount < 0)
            throw new PostingException("Govt fee cannot be negative.");
        if (govtFeeAmount > billed)
            throw new PostingException($"Govt fee cannot exceed the billed amount ({billed:N2}).");
        if (centerFeeAmount < 0)
            throw new PostingException("Center fee cannot be negative.");
        if (govtFeeAmount + centerFeeAmount <= 0)
            throw new PostingException("Enter a govt fee and/or a center fee for this supplier's bill.");
        if (payNow && payFromAccountId is null)
            throw new PostingException("Pick a bank/cash account to pay from.");

        var period = await _ledger.GetDefaultPeriodAsync(completionDate)
            ?? throw new PostingException("No fiscal period covers the completion date.");

        // Two lines, not one blended line — Govt Fee stays a pure pass-through mirror of the
        // billed amount, the supplier's own Center Fee is a distinct subcontracted cost. Either
        // can be zero (but not both, guarded above).
        var lines = new List<PurchaseLineInput>();
        if (govtFeeAmount > 0)
        {
            var govtAccount = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.GovtFeesExpense);
            lines.Add(new PurchaseLineInput(line.Description, govtAccount.Id, line.CostCenterId, 1, 0, 0m, NonTaxableAmount: govtFeeAmount));
        }
        if (centerFeeAmount > 0)
        {
            var centerAccount = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.SubcontractedProServicesExpense);
            lines.Add(new PurchaseLineInput(line.Description, centerAccount.Id, line.CostCenterId, 1, centerFeeAmount, applyVat ? 0.05m : 0m));
        }

        var narration = $"Outsourced — {line.Description} — {invoice.InvoiceNo}-L{line.LineNo}";
        var bill = await _purchaseInvoices.CreateAndPostAsync(
            supplierId, vendorRef: supplierInvoiceNo, completionDate, period.Id, narration,
            createdBy: completedBy, lines, nowUtc);

        if (attachment is { } att)
            await _purchaseInvoices.SetAttachmentAsync(bill.Id, att.FileName, att.ContentType, att.Data);

        if (payNow)
        {
            var allocations = bill.Lines.Select(l => new PaymentLineAllocationInput(l.Id, l.Gross)).ToList();
            await _vendorPayments.CreateAndPostAsync(
                supplierId, bill.Id, completionDate, period.Id, payFromAccountId!.Value, bill.TotalGross,
                narration, completedBy, nowUtc, allocations, paymentMode, referenceNo: paymentReference);
        }

        // Same pattern as CompleteDirectAsync: link the bill back to the line, and mark it
        // complete, in one save on the context that's held `line` tracked since the top of this
        // method. The bill's own lines are also marked fulfilled here — completing via this path
        // already means the job was done (that's what "Supplier — Outsourced" is recording), so
        // there's no separate real-world event left for someone to mark on Expense Transactions;
        // leaving them pending there would just be a second click with nothing new to report.
        var trackedBill = await db.PurchaseInvoices.Include(i => i.Lines).FirstAsync(i => i.Id == bill.Id);
        trackedBill.SourceSalesInvoiceLineId = lineId;
        foreach (var billLine in trackedBill.Lines)
        {
            billLine.IsCompleted = true;
            billLine.CompletedAtUtc = nowUtc;
            billLine.CompletedBy = completedBy;
        }
        line.SupplierId = supplierId;
        line.IsCompleted = true;
        line.CompletedAtUtc = nowUtc;
        line.CompletedBy = completedBy;
        await db.SaveChangesAsync();

        bill.SourceSalesInvoiceLineId = lineId;
        foreach (var billLine in bill.Lines)
        {
            billLine.IsCompleted = true;
            billLine.CompletedAtUtc = nowUtc;
            billLine.CompletedBy = completedBy;
        }
        return bill;
    }
}
