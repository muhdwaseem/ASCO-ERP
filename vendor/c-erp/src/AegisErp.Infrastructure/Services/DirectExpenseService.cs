using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>One expense line: an amount charged to a specific expense account, optionally picked from the Items catalog.</summary>
public record DirectExpenseLineInput(int ExpenseAccountId, int? CostCenterId, string? Description, decimal Amount, int? ItemId = null, decimal VatRate = 0m);

public class DirectExpenseService
{
    public const int MaxAttachmentBytes = 5 * 1024 * 1024;

    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;
    public DirectExpenseService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    public async Task<List<DirectExpense>> GetRecentAsync(int take = 50)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.DirectExpenses.AsNoTracking()
            .Include(x => x.Vendor).Include(x => x.Customer).Include(x => x.BankAccount)
            .Include(x => x.Lines).ThenInclude(l => l.ExpenseAccount)
            .OrderByDescending(x => x.Date).ThenByDescending(x => x.Id)
            .Take(take).ToListAsync();
    }

    /// <summary>One direct expense with full detail, for the expense detail view.</summary>
    public async Task<DirectExpense?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.DirectExpenses.AsNoTracking()
            .Include(x => x.Vendor).Include(x => x.Customer).Include(x => x.BankAccount)
            .Include(x => x.Lines).ThenInclude(l => l.ExpenseAccount)
            .Include(x => x.JournalVoucher)
            .FirstOrDefaultAsync(x => x.Id == id);
    }

    /// <summary>
    /// Creates and posts a direct expense in one transaction, generating the GL voucher
    /// (Dr each line's expense account for its amount / Cr the "paid through" bank account for
    /// the total). No vendor invoice or Accounts Payable is involved — Vendor and Customer are
    /// both optional (Vendor just records who was paid; Customer is a "billable to" tag only).
    /// </summary>
    public async Task<DirectExpense> CreateAndPostAsync(
        int? vendorId, int? customerId, DateOnly date, int fiscalPeriodId, int? bankAccountId,
        string? reference, string? narration, string createdBy, DateTime nowUtc,
        IEnumerable<DirectExpenseLineInput> lines, string? vendorInvoiceNo = null, bool amountsIncludeVat = false,
        bool payLater = false)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        if (vendorId is int vId && await db.Vendors.FindAsync(vId) is null)
            throw new PostingException("Vendor not found.");
        if (customerId is int cId && await db.Customers.FindAsync(cId) is null)
            throw new PostingException("Customer not found.");

        var expenseNo = await JournalPoster.NextDocNoAsync(db, "EXP", date.Year);

        var expense = new DirectExpense
        {
            ExpenseNo = expenseNo,
            VendorId = vendorId,
            CustomerId = customerId,
            Date = date,
            FiscalPeriodId = fiscalPeriodId,
            BankAccountId = bankAccountId,
            Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(),
            VendorInvoiceNo = string.IsNullOrWhiteSpace(vendorInvoiceNo) ? null : vendorInvoiceNo.Trim(),
            Narration = narration,
            AmountsIncludeVat = amountsIncludeVat,
            IsPayLater = payLater,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
        };

        var no = 1;
        foreach (var l in lines)
            expense.Lines.Add(new DirectExpenseLine
            {
                LineNo = no++,
                ItemId = l.ItemId,
                ExpenseAccountId = l.ExpenseAccountId,
                CostCenterId = l.CostCenterId,
                Description = l.Description,
                Amount = l.Amount,
                VatRate = l.VatRate,
            });

        expense.Post(nowUtc); // domain validation (lines exist, accounts/amounts valid)

        // Credit totals are sums of each line's own Net/VAT split — never independently
        // recomputed — so the voucher can never be a cent off balanced (same discipline as
        // PurchaseInvoiceService).
        var voucherLines = expense.Lines
            .Select(l =>
            {
                var (net, _, _) = l.Split(amountsIncludeVat);
                return new VoucherLineInput(l.ExpenseAccountId, l.CostCenterId, l.Description, net, 0);
            })
            .ToList();

        if (expense.TotalVat > 0)
        {
            var vatInput = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.VatInput);
            voucherLines.Add(new VoucherLineInput(vatInput.Id, null, $"Input VAT — {expenseNo}", expense.TotalVat, 0));
        }

        if (payLater)
        {
            var payable = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.ExpensesPayable);
            voucherLines.Add(new VoucherLineInput(payable.Id, null, expense.Narration, 0, expense.TotalAmount));
        }
        else
        {
            voucherLines.Add(new VoucherLineInput(bankAccountId!.Value, null, expense.Narration, 0, expense.TotalAmount));
        }

        expense.JournalVoucher = await JournalPoster.PostAsync(
            db, _current.CanPost, VoucherType.Expense, expenseNo, date, fiscalPeriodId,
            expense.Narration, reference ?? expenseNo, createdBy, voucherLines, nowUtc);

        db.DirectExpenses.Add(expense);
        await JournalPoster.SaveAndCommitAsync(db, tx);
        return expense;
    }

    public async Task SetAttachmentAsync(int expenseId, string fileName, string contentType, byte[] data)
    {
        if (data.Length > MaxAttachmentBytes)
            throw new PostingException($"Attachment is too large — the limit is {MaxAttachmentBytes / (1024 * 1024)} MB.");

        await using var db = await _dbf.CreateDbContextAsync();
        var expense = await db.DirectExpenses.FirstOrDefaultAsync(x => x.Id == expenseId)
            ?? throw new PostingException("Expense not found.");

        expense.AttachmentFileName = fileName;
        expense.AttachmentContentType = contentType;
        expense.AttachmentData = data;
        await db.SaveChangesAsync();
    }

    public async Task<(string FileName, string ContentType, byte[] Data)?> GetAttachmentAsync(int expenseId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var expense = await db.DirectExpenses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == expenseId);
        if (expense?.AttachmentData is null || expense.AttachmentFileName is null) return null;
        return (expense.AttachmentFileName, expense.AttachmentContentType ?? "application/octet-stream", expense.AttachmentData);
    }
}
