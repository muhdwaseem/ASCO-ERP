using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>
/// Backs the cross-invoice Expense Transactions view — the AP mirror of <see cref="TransactionService"/>.
/// Every Purchase Invoice line, company-wide, flattened into one list with fulfillment (Completed)
/// and payment ("Paid To") attribution. "Pay Later" Direct Expense lines are included too, with
/// "Paid To" attribution from <see cref="DirectExpensePaymentAllocation"/>. "Pay Now" Direct
/// Expenses remain excluded: they're settled in full the moment they're posted, so there's nothing
/// to track.
/// </summary>
public class ExpenseTransactionService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;
    public ExpenseTransactionService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    public async Task<List<ExpenseTransactionRow>> GetAllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();

        // Start from PurchaseInvoices (company-scoped) and flatten in memory — PurchaseInvoiceLine
        // has no CompanyId/query filter of its own, so querying db.PurchaseInvoiceLines directly
        // would leak every company's lines.
        var invoices = await db.PurchaseInvoices.AsNoTracking()
            .Include(i => i.Vendor)
            .Include(i => i.Lines)
            .Where(i => i.Status != VoucherStatus.Void)
            .OrderByDescending(i => i.Date).ThenByDescending(i => i.Id)
            .ToListAsync();

        var lineIds = invoices.SelectMany(i => i.Lines).Select(l => l.Id).ToList();
        var invoiceAllocations = await db.VendorPaymentAllocations.AsNoTracking()
            .Where(a => lineIds.Contains(a.PurchaseInvoiceLineId) && a.VendorPayment.Status == VoucherStatus.Posted)
            .Select(a => new { a.PurchaseInvoiceLineId, a.Amount, a.VendorPayment.Date, a.VendorPaymentId, AccountName = a.VendorPayment.BankAccount.Name })
            .ToListAsync();
        var paidTo = invoiceAllocations.GroupBy(a => a.PurchaseInvoiceLineId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.Date).ThenByDescending(a => a.VendorPaymentId).First().AccountName);
        var invoiceAllocated = invoiceAllocations.GroupBy(a => a.PurchaseInvoiceLineId)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));

        var rows = new List<ExpenseTransactionRow>();
        foreach (var inv in invoices)
            foreach (var l in inv.Lines.OrderBy(l => l.LineNo))
                rows.Add(new ExpenseTransactionRow(
                    l.Id, $"{inv.InvoiceNo}-L{l.LineNo}", inv.Id, inv.InvoiceNo, inv.Date, inv.Status,
                    inv.Vendor.Name, inv.VendorRef, inv.LpoNo, l.Description,
                    l.Quantity, l.Gross, l.IsCompleted, l.CompletedAtUtc, l.CompletedBy,
                    paidTo.GetValueOrDefault(l.Id), IsDirectExpense: false,
                    l.Net - l.NonTaxableTotal, l.NonTaxableTotal, l.Vat, l.Gross - invoiceAllocated.GetValueOrDefault(l.Id)));

        // "Pay Later" Direct Expenses, company-wide — same reasoning as above: DirectExpenseLine
        // has no CompanyId/query filter of its own, so this is routed through the company-scoped
        // DirectExpenses. "Pay Now" expenses are excluded — settled in full at posting already.
        var payLaterExpenses = await db.DirectExpenses.AsNoTracking()
            .Include(e => e.Vendor).Include(e => e.Lines)
            .Where(e => e.IsPayLater && e.Status == VoucherStatus.Posted)
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.Id)
            .ToListAsync();

        var expenseLineIds = payLaterExpenses.SelectMany(e => e.Lines).Select(l => l.Id).ToList();
        var expenseAllocations = await db.DirectExpensePaymentAllocations.AsNoTracking()
            .Where(a => expenseLineIds.Contains(a.DirectExpenseLineId))
            .Select(a => new { a.DirectExpenseLineId, a.Amount, a.DirectExpensePayment.Date, a.DirectExpensePaymentId, AccountName = a.DirectExpensePayment.BankAccount.Name })
            .ToListAsync();
        var expensePaidTo = expenseAllocations.GroupBy(a => a.DirectExpenseLineId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.Date).ThenByDescending(a => a.DirectExpensePaymentId).First().AccountName);
        var expenseAllocated = expenseAllocations.GroupBy(a => a.DirectExpenseLineId)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));

        foreach (var exp in payLaterExpenses)
            foreach (var l in exp.Lines.OrderBy(l => l.LineNo))
            {
                var split = l.Split(exp.AmountsIncludeVat);
                rows.Add(new ExpenseTransactionRow(
                    l.Id, $"{exp.ExpenseNo}-L{l.LineNo}", exp.Id, exp.ExpenseNo, exp.Date, exp.Status,
                    exp.Vendor?.Name ?? "—", null, null, l.Description ?? "(no description)",
                    1, split.Gross, false, null, null,
                    expensePaidTo.GetValueOrDefault(l.Id), IsDirectExpense: true,
                    split.Net, 0, split.Vat, split.Gross - expenseAllocated.GetValueOrDefault(l.Id)));
            }

        return rows.OrderByDescending(r => r.InvoiceDate).ThenByDescending(r => r.InvoiceId).ToList();
    }

    /// <summary>Marks one line complete/incomplete. Scoped through PurchaseInvoices so a lineId
    /// from another company can never be reached, even though PurchaseInvoiceLine has no filter
    /// of its own.</summary>
    public async Task SetCompletionAsync(int lineId, bool isCompleted, string changedBy, DateTime nowUtc)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        await using var db = await _dbf.CreateDbContextAsync();
        var line = await db.PurchaseInvoices.SelectMany(i => i.Lines).FirstOrDefaultAsync(l => l.Id == lineId)
            ?? throw new PostingException("Transaction line not found.");

        line.IsCompleted = isCompleted;
        line.CompletedAtUtc = isCompleted ? nowUtc : null;
        line.CompletedBy = isCompleted ? changedBy : null;
        await db.SaveChangesAsync();
    }
}

/// <summary>
/// <see cref="CenterFee"/>/<see cref="GovtCost"/> mirror the same PRO Service Mode split used on
/// the Transactions (sales) view — here they're what was actually paid, not what was billed. No
/// <c>Profit</c> here: this page is cost-only, with no revenue line to compare against.
/// </summary>
public record ExpenseTransactionRow(
    int LineId, string TranRef, int InvoiceId, string InvoiceNo, DateOnly InvoiceDate, VoucherStatus InvoiceStatus,
    string VendorName, string? VendorRef, string? LpoNo, string Description,
    decimal Quantity, decimal Amount, bool IsCompleted, DateTime? CompletedAtUtc, string? CompletedBy,
    string? PaidToAccountName, bool IsDirectExpense,
    decimal CenterFee, decimal GovtCost, decimal Vat,
    // Outstanding amount still owed on this line, from posted payment allocations (VendorPayment
    // for a Purchase Invoice row, DirectExpensePayment for a "Pay Later" Direct Expense row).
    decimal Balance);
