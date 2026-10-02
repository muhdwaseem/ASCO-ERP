using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>How much of a payment is applied to one specific "Pay Later" expense line/charge.</summary>
public record ExpenseLineAllocationInput(int DirectExpenseLineId, decimal Amount);

/// <summary>
/// Settles a "Pay Later" <see cref="DirectExpense"/> — always exactly one expense per payment
/// (unlike <see cref="VendorPaymentService"/>, which can span several invoices), optionally split
/// across that expense's own lines. Posts immediately: there is no Draft stage, mirroring
/// <see cref="DirectExpenseService.CreateAndPostAsync"/>'s own one-step posting.
/// </summary>
public class DirectExpensePaymentService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;
    public DirectExpensePaymentService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    /// <summary>
    /// Per-line paid/balance breakdown for one "Pay Later" expense, from posted payment
    /// allocations — so staff can see which specific charge is settled vs still owing.
    /// </summary>
    public async Task<List<DirectExpenseLineBalance>> GetLineBalancesAsync(int directExpenseId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var expense = await db.DirectExpenses.AsNoTracking().Include(e => e.Lines)
            .FirstOrDefaultAsync(e => e.Id == directExpenseId)
            ?? throw new PostingException("Expense not found.");

        var lineIds = expense.Lines.Select(l => l.Id).ToList();
        var allocated = (await db.DirectExpensePaymentAllocations.AsNoTracking()
            .Where(a => lineIds.Contains(a.DirectExpenseLineId))
            .Select(a => new { a.DirectExpenseLineId, a.Amount })
            .ToListAsync())
            .GroupBy(a => a.DirectExpenseLineId)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));

        return expense.Lines.OrderBy(l => l.LineNo).Select(l =>
        {
            var gross = l.Split(expense.AmountsIncludeVat).Gross;
            return new DirectExpenseLineBalance(l.Id, l.Description, gross, allocated.GetValueOrDefault(l.Id));
        }).ToList();
    }

    /// <summary>
    /// Creates and posts a payment against one "Pay Later" expense, generating the GL voucher
    /// (Dr Expenses Payable / Cr bank) — never re-touching the expense's own accounts or VAT Input,
    /// both already fully recognized at the original Pay-Later posting. When the expense has more
    /// than one line, <paramref name="allocations"/> is required and must sum to exactly
    /// <paramref name="amount"/>; a single-line expense auto-allocates the whole amount to that
    /// line if no allocations are given.
    /// </summary>
    public async Task<DirectExpensePayment> CreateAndPostAsync(
        int directExpenseId, DateOnly date, int fiscalPeriodId, int bankAccountId, decimal amount,
        string? narration, string createdBy, DateTime nowUtc,
        IEnumerable<ExpenseLineAllocationInput>? allocations = null, PaymentMode paymentMode = PaymentMode.Cash,
        string? referenceNo = null, DateOnly? chequeDate = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        await LockExpenseRowAsync(db, directExpenseId);

        var expense = await db.DirectExpenses.AsNoTracking().Include(e => e.Lines)
            .FirstOrDefaultAsync(e => e.Id == directExpenseId)
            ?? throw new PostingException("Expense not found.");
        if (!expense.IsPayLater)
            throw new PostingException("Only a \"Pay Later\" expense can be settled with a payment.");
        if (expense.Status != VoucherStatus.Posted)
            throw new PostingException("Only a posted expense can be settled.");
        if (amount <= 0)
            throw new PostingException("Payment amount must be positive.");

        await CheckExpenseOutstandingAsync(db, expense, amount);

        var inputs = allocations?.Where(a => a.Amount > 0).ToList() ?? new List<ExpenseLineAllocationInput>();
        var lineAllocations = await ValidateAllocationsAsync(db, expense, amount, inputs);

        var paymentNo = await NextPaymentNoAsync(db, date.Year);

        var payment = new DirectExpensePayment
        {
            PaymentNo = paymentNo,
            DirectExpenseId = directExpenseId,
            Date = date,
            FiscalPeriodId = fiscalPeriodId,
            BankAccountId = bankAccountId,
            Amount = amount,
            PaymentMode = paymentMode,
            ReferenceNo = string.IsNullOrWhiteSpace(referenceNo) ? null : referenceNo.Trim(),
            ChequeDate = chequeDate,
            Narration = string.IsNullOrWhiteSpace(narration) ? $"Expense payment — {expense.ExpenseNo}" : narration,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
            Allocations = lineAllocations,
        };

        var payable = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.ExpensesPayable);
        var voucherLines = new List<VoucherLineInput>
        {
            new(payable.Id, null, $"{expense.ExpenseNo} — {paymentNo}", amount, 0),
            new(bankAccountId, null, payment.Narration, 0, amount),
        };

        payment.JournalVoucher = await JournalPoster.PostAsync(
            db, _current.CanPost, VoucherType.Payment, paymentNo, date, fiscalPeriodId,
            payment.Narration, paymentNo, createdBy, voucherLines, nowUtc);

        db.DirectExpensePayments.Add(payment);
        await JournalPoster.SaveAndCommitAsync(db, tx);
        return payment;
    }

    /// <summary>One payment with full detail — for the expense detail view's payment history.</summary>
    public async Task<List<DirectExpensePayment>> GetByExpenseAsync(int directExpenseId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.DirectExpensePayments.AsNoTracking()
            .Include(p => p.BankAccount)
            .Where(p => p.DirectExpenseId == directExpenseId)
            .OrderByDescending(p => p.Date).ThenByDescending(p => p.Id)
            .ToListAsync();
    }

    /// <summary>Requires a per-line split when the expense has more than one line; auto-allocates a single line.</summary>
    private static async Task<List<DirectExpensePaymentAllocation>> ValidateAllocationsAsync(
        AegisDbContext db, DirectExpense expense, decimal amount, List<ExpenseLineAllocationInput> inputs)
    {
        if (expense.Lines.Count > 1)
        {
            if (inputs.Count == 0)
                throw new PostingException(
                    $"{expense.ExpenseNo} has multiple lines — specify how this payment is allocated across them.");
            if (inputs.Sum(a => a.Amount) != amount)
                throw new PostingException(
                    $"Line allocations ({inputs.Sum(a => a.Amount):N2}) must add up to the payment amount ({amount:N2}).");
            return await ValidateLineCapsAsync(db, expense, inputs);
        }
        if (expense.Lines.Count == 1)
            return new List<DirectExpensePaymentAllocation> { new() { DirectExpenseLineId = expense.Lines[0].Id, Amount = amount } };
        return new List<DirectExpensePaymentAllocation>();
    }

    /// <summary>
    /// Locks the expense's row on Postgres (transaction-scoped, auto-released at commit/rollback)
    /// so two simultaneous payments against the same expense serialize instead of both reading the
    /// same outstanding balance and both passing. No-op on Sqlite or outside an ambient transaction.
    /// Mirrors <c>VendorPaymentService.LockInvoiceRowAsync</c> — NOT yet exercised against a live
    /// Postgres server; verify with a real concurrency test before relying on it in production.
    /// </summary>
    private static async Task LockExpenseRowAsync(AegisDbContext db, int directExpenseId)
    {
        if (!db.Database.IsNpgsql() || db.Database.CurrentTransaction is null) return;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"DirectExpenses\" WHERE \"Id\" = {directExpenseId} FOR UPDATE");
    }

    /// <summary>This expense's own outstanding must not be exceeded by <paramref name="amountBeingApplied"/>.</summary>
    private static async Task CheckExpenseOutstandingAsync(AegisDbContext db, DirectExpense expense, decimal amountBeingApplied)
    {
        var lineIds = expense.Lines.Select(l => l.Id).ToList();
        var allocated = (await db.DirectExpensePaymentAllocations.AsNoTracking()
            .Where(a => lineIds.Contains(a.DirectExpenseLineId))
            .Select(a => a.Amount)
            .ToListAsync()).Sum();
        var outstanding = expense.TotalAmount - allocated;
        if (amountBeingApplied > outstanding)
            throw new PostingException(
                $"Amount {amountBeingApplied:N2} exceeds the outstanding {outstanding:N2} on {expense.ExpenseNo}.");
    }

    /// <summary>Each input must belong to one of the expense's lines and not push that line's cumulative allocations past its own Gross.</summary>
    private static async Task<List<DirectExpensePaymentAllocation>> ValidateLineCapsAsync(
        AegisDbContext db, DirectExpense expense, List<ExpenseLineAllocationInput> inputs)
    {
        var lineIds = expense.Lines.Select(l => l.Id).ToHashSet();
        if (inputs.Any(a => !lineIds.Contains(a.DirectExpenseLineId)))
            throw new PostingException("An allocation refers to a line that is not part of this expense.");

        var idsList = expense.Lines.Select(l => l.Id).ToList();
        var existingByLine = (await db.DirectExpensePaymentAllocations
            .Where(a => idsList.Contains(a.DirectExpenseLineId))
            .Select(a => new { a.DirectExpenseLineId, a.Amount })
            .ToListAsync())
            .GroupBy(a => a.DirectExpenseLineId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

        var result = new List<DirectExpensePaymentAllocation>();
        foreach (var a in inputs)
        {
            var line = expense.Lines.First(l => l.Id == a.DirectExpenseLineId);
            var gross = line.Split(expense.AmountsIncludeVat).Gross;
            var already = existingByLine.GetValueOrDefault(a.DirectExpenseLineId);
            if (already + a.Amount > gross)
                throw new PostingException(
                    $"Allocating {a.Amount:N2} to '{line.Description}' would exceed its remaining balance ({gross - already:N2}).");
            result.Add(new DirectExpensePaymentAllocation { DirectExpenseLineId = a.DirectExpenseLineId, Amount = a.Amount });
        }
        return result;
    }

    /// <summary>Next payment number, scoped to the DirectExpensePayments table itself — a distinct
    /// "EXP-PMT-" series so it can never collide with a Direct Expense's own "EXP-" number or a
    /// Vendor Payment's "PV-" number.</summary>
    private static async Task<string> NextPaymentNoAsync(AegisDbContext db, int year)
    {
        var existing = await db.DirectExpensePayments.Select(p => p.PaymentNo).ToListAsync();
        return JournalPoster.NextDocNo(existing, "EXP-PMT", year);
    }
}
