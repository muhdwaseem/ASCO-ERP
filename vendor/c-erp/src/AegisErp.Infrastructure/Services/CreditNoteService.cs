using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Input line for creating a credit note from the UI.</summary>
public record CreditNoteLineInput(string Description, int RevenueAccountId, int? CostCenterId,
    decimal Quantity, decimal UnitPrice, decimal VatRate);

/// <summary>One posted, on-account credit note that still has an unapplied balance — a row in the
/// "Credits Available" list offered on an invoice's Apply Credit action.</summary>
public record AvailableCredit(int CreditNoteId, string CreditNoteNo, DateOnly Date, decimal TotalGross, decimal Allocated)
{
    public decimal Available => TotalGross - Allocated;
}

public class CreditNoteService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;
    public CreditNoteService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    public async Task<List<CreditNote>> GetRecentAsync(int take = 50)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CreditNotes.AsNoTracking()
            .Include(n => n.Customer).Include(n => n.Lines).Include(n => n.SalesInvoice)
            .Include(n => n.JournalVoucher).Include(n => n.BankAccount)
            .OrderByDescending(n => n.Date).ThenByDescending(n => n.Id)
            .Take(take).ToListAsync();
    }

    /// <summary>Every credit note for one customer, for the customer detail view's Credit Notes tab.</summary>
    public async Task<List<CreditNote>> GetByCustomerAsync(int customerId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CreditNotes.AsNoTracking()
            .Include(n => n.Lines).Include(n => n.SalesInvoice).Include(n => n.Allocations)
            .Where(n => n.CustomerId == customerId)
            .OrderByDescending(n => n.Date).ThenByDescending(n => n.Id)
            .ToListAsync();
    }

    /// <summary>One credit note with full detail, for the credit note detail view.</summary>
    public async Task<CreditNote?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CreditNotes.AsNoTracking()
            .Include(n => n.Customer).Include(n => n.Lines).Include(n => n.SalesInvoice)
            .Include(n => n.JournalVoucher).Include(n => n.BankAccount)
            .Include(n => n.Allocations).ThenInclude(a => a.SalesInvoice)
            .FirstOrDefaultAsync(n => n.Id == id);
    }

    /// <summary>
    /// Creates and posts a credit note in one transaction: the note, its lines and the generated GL
    /// voucher are persisted atomically — the mirror image of a sales invoice (Dr revenue net per
    /// line / Dr VAT payable, then either Cr AR — <see cref="CreditNoteSettlementMethod.ApplyToInvoice"/>
    /// or <see cref="CreditNoteSettlementMethod.CreditOnAccount"/> — or Cr the chosen bank account
    /// for <see cref="CreditNoteSettlementMethod.CashRefund"/>). When applied to an invoice, the
    /// total credited against it — across every credit note, regardless of settlement method —
    /// may never exceed that invoice's original total; <see cref="CreditNoteSettlementMethod.ApplyToInvoice"/>
    /// and <see cref="CreditNoteSettlementMethod.CreditOnAccount"/> are additionally capped by
    /// what's still outstanding (unpaid), since only a <see cref="CreditNoteSettlementMethod.CashRefund"/>
    /// pays out of the bank rather than reducing AR.
    /// </summary>
    public async Task<CreditNote> CreateAndPostAsync(
        int customerId, int? salesInvoiceId, DateOnly date, int fiscalPeriodId, string? reason,
        string? narration, string createdBy, IEnumerable<CreditNoteLineInput> lines, DateTime nowUtc,
        CreditNoteSettlementMethod settlementMethod = CreditNoteSettlementMethod.CreditOnAccount,
        int? bankAccountId = null)
    {
        if (settlementMethod == CreditNoteSettlementMethod.ApplyToInvoice && salesInvoiceId is null)
            throw new PostingException("Applying to an invoice requires an invoice to be selected.");
        if (settlementMethod == CreditNoteSettlementMethod.CashRefund && bankAccountId is null)
            throw new PostingException("A cash refund requires a bank/cash account to be selected.");

        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var customer = await db.Customers.FindAsync(customerId)
            ?? throw new PostingException("Customer not found.");

        var note = new CreditNote
        {
            CreditNoteNo = await JournalPoster.NextDocNoAsync(db, "CN", date.Year),
            CustomerId = customerId,
            SalesInvoiceId = salesInvoiceId,
            Date = date,
            FiscalPeriodId = fiscalPeriodId,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            Narration = string.IsNullOrWhiteSpace(narration) ? $"Credit note — {customer.Name}" : narration,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
            SettlementMethod = settlementMethod,
            BankAccountId = settlementMethod == CreditNoteSettlementMethod.CashRefund ? bankAccountId : null,
        };

        var no = 1;
        foreach (var l in lines)
            note.Lines.Add(new CreditNoteLine
            {
                LineNo = no++,
                Description = l.Description,
                RevenueAccountId = l.RevenueAccountId,
                CostCenterId = l.CostCenterId,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                VatRate = l.VatRate,
            });

        note.Post(nowUtc); // domain validation

        if (salesInvoiceId is int invId)
        {
            await LockInvoiceRowAsync(db, invId);
            var invoice = await db.SalesInvoices.AsNoTracking().Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.Id == invId)
                ?? throw new PostingException("Invoice not found.");
            if (invoice.CustomerId != customerId)
                throw new PostingException("Invoice belongs to a different customer.");
            if (invoice.Status != VoucherStatus.Posted)
                throw new PostingException("Only posted invoices can be credited.");

            var priorCredited = await GetCreditedAgainstInvoiceAsync(db, invId);

            // Hard ceiling regardless of settlement method: an invoice can never be credited, in
            // total across every credit note against it, for more than it was originally billed.
            if (priorCredited + note.TotalGross > invoice.TotalGross)
                throw new PostingException(
                    $"Credit {note.TotalGross:N2} plus {priorCredited:N2} already credited would exceed " +
                    $"{invoice.InvoiceNo}'s total of {invoice.TotalGross:N2}.");

            // A cash refund pays out of the bank, not off the invoice's AR balance, so it isn't
            // additionally capped by what's still outstanding — a fully-paid invoice can still be
            // refunded, as long as the ceiling above holds.
            if (settlementMethod != CreditNoteSettlementMethod.CashRefund)
            {
                var receipts = (await db.CustomerReceipts
                    .Where(r => r.SalesInvoiceId == invId && r.Status == VoucherStatus.Posted)
                    .Select(r => r.Amount).ToListAsync()).Sum();
                var outstanding = invoice.TotalGross - receipts - priorCredited;
                if (note.TotalGross > outstanding)
                    throw new PostingException(
                        $"Credit {note.TotalGross:N2} exceeds the outstanding {outstanding:N2} on {invoice.InvoiceNo}.");
            }
        }

        // Build the GL voucher: Dr revenue per line, Dr VAT payable for the tax, then credit
        // either AR (reduces what the customer owes) or the chosen bank account (cash paid
        // straight back out), depending on the settlement method.
        var voucherLines = new List<VoucherLineInput>();
        voucherLines.AddRange(note.Lines.Where(l => l.Net > 0).Select(l =>
            new VoucherLineInput(l.RevenueAccountId, l.CostCenterId, l.Description, l.Net, 0)));
        if (note.TotalVat > 0)
        {
            var vat = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.VatPayable);
            voucherLines.Add(new VoucherLineInput(vat.Id, null, $"VAT reversal — {note.CreditNoteNo}", note.TotalVat, 0));
        }

        if (settlementMethod == CreditNoteSettlementMethod.CashRefund)
        {
            var bank = await db.Accounts.FindAsync(bankAccountId)
                ?? throw new PostingException("Bank/cash account not found.");
            if (bank.Type != AccountType.Asset)
                throw new PostingException($"{bank.Code} — {bank.Name} is not a bank/cash account.");
            voucherLines.Add(new VoucherLineInput(bank.Id, null, $"Cash refund — {customer.Name} — {note.CreditNoteNo}", 0, note.TotalGross));
        }
        else
        {
            var ar = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.AccountsReceivable);
            voucherLines.Add(new VoucherLineInput(ar.Id, null, $"{customer.Name} — {note.CreditNoteNo}", 0, note.TotalGross));
        }

        note.JournalVoucher = await JournalPoster.PostAsync(
            db, _current.CanPost, VoucherType.CreditNote, note.CreditNoteNo, date, fiscalPeriodId,
            note.Narration, note.CreditNoteNo, createdBy, voucherLines, nowUtc);

        db.CreditNotes.Add(note);
        await JournalPoster.SaveAndCommitAsync(db, tx);
        return note;
    }

    /// <summary>
    /// Locks the invoice's row on Postgres (transaction-scoped, auto-released at commit/rollback)
    /// so two simultaneous credit-note postings/applications against the same invoice serialize
    /// instead of both reading the same prior-credited/outstanding total and both passing (the
    /// "Credit notes ... aren't locked the same way yet" gap noted in the README). No-op on Sqlite
    /// or outside an ambient transaction. NOT yet exercised against a live Postgres server — verify
    /// with a real concurrency test before relying on it in production.
    /// </summary>
    private static async Task LockInvoiceRowAsync(AegisDbContext db, int invoiceId)
    {
        if (!db.Database.IsNpgsql() || db.Database.CurrentTransaction is null) return;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"SalesInvoices\" WHERE \"Id\" = {invoiceId} FOR UPDATE");
    }

    /// <summary>Total credited against one invoice: credit notes created directly against it, plus
    /// any on-account credit note balance since applied to it via <see cref="ApplyToInvoiceAsync"/>.</summary>
    private static async Task<decimal> GetCreditedAgainstInvoiceAsync(AegisDbContext db, int invoiceId)
    {
        var direct = (await db.CreditNotes.Include(n => n.Lines)
            .Where(n => n.SalesInvoiceId == invoiceId && n.Status == VoucherStatus.Posted)
            .ToListAsync()).Sum(n => n.TotalGross);
        var allocated = (await db.CreditNoteAllocations
            .Where(a => a.SalesInvoiceId == invoiceId && a.CreditNote.Status == VoucherStatus.Posted)
            .Select(a => a.Amount).ToListAsync()).Sum();
        return direct + allocated;
    }

    /// <summary>A customer's posted, on-account credit notes that still have an unapplied balance —
    /// the "Credits Available" list offered on an invoice's Apply Credit action.</summary>
    public async Task<List<AvailableCredit>> GetAvailableCreditAsync(int customerId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var notes = await db.CreditNotes.AsNoTracking().Include(n => n.Lines).Include(n => n.Allocations)
            .Where(n => n.CustomerId == customerId && n.Status == VoucherStatus.Posted
                && n.SettlementMethod == CreditNoteSettlementMethod.CreditOnAccount)
            .OrderBy(n => n.Date).ThenBy(n => n.Id)
            .ToListAsync();

        return notes
            .Select(n => new AvailableCredit(n.Id, n.CreditNoteNo, n.Date, n.TotalGross, n.Allocations.Sum(a => a.Amount)))
            .Where(c => c.Available > 0)
            .ToList();
    }

    /// <summary>
    /// Applies one or more on-account credit notes' available balance to a specific invoice — the
    /// "Apply Credit" action. A credit note can be split across several invoices over time (unlike
    /// <see cref="CreditNote.SalesInvoiceId"/>, fixed to one invoice at posting), and this call can
    /// apply several credit notes to one invoice at once. No new GL voucher is posted — the credit
    /// already hit Accounts Receivable when the note itself was posted; this only records the match.
    /// </summary>
    public async Task ApplyToInvoiceAsync(
        int invoiceId, IEnumerable<(int CreditNoteId, decimal Amount)> allocations, string appliedBy, DateTime nowUtc)
    {
        var requested = allocations.Where(a => a.Amount > 0).ToList();
        if (requested.Count == 0)
            throw new PostingException("Enter an amount to apply.");

        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        await LockInvoiceRowAsync(db, invoiceId);
        var invoice = await db.SalesInvoices.AsNoTracking().Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");
        if (invoice.Status != VoucherStatus.Posted)
            throw new PostingException("Only posted invoices can have credit applied.");

        var priorCredited = await GetCreditedAgainstInvoiceAsync(db, invoiceId);
        var receipts = (await db.CustomerReceipts
            .Where(r => r.SalesInvoiceId == invoiceId && r.Status == VoucherStatus.Posted)
            .Select(r => r.Amount).ToListAsync()).Sum();
        var outstanding = invoice.TotalGross - receipts - priorCredited;

        var totalRequested = requested.Sum(a => a.Amount);
        if (totalRequested > outstanding)
            throw new PostingException(
                $"Applying {totalRequested:N2} would exceed the outstanding {outstanding:N2} on {invoice.InvoiceNo}.");

        foreach (var (creditNoteId, amount) in requested)
        {
            var note = await db.CreditNotes.Include(n => n.Lines).Include(n => n.Allocations)
                .FirstOrDefaultAsync(n => n.Id == creditNoteId)
                ?? throw new PostingException("Credit note not found.");
            if (note.CustomerId != invoice.CustomerId)
                throw new PostingException($"{note.CreditNoteNo} belongs to a different customer.");
            if (note.Status != VoucherStatus.Posted)
                throw new PostingException($"{note.CreditNoteNo} is not posted.");
            if (note.SettlementMethod != CreditNoteSettlementMethod.CreditOnAccount)
                throw new PostingException($"{note.CreditNoteNo} is not an on-account credit note.");

            var available = note.TotalGross - note.Allocations.Sum(a => a.Amount);
            if (amount > available)
                throw new PostingException($"{note.CreditNoteNo} only has {available:N2} available.");

            db.CreditNoteAllocations.Add(new CreditNoteAllocation
            {
                CreditNoteId = creditNoteId,
                SalesInvoiceId = invoiceId,
                Amount = amount,
                AllocatedAtUtc = nowUtc,
                AllocatedBy = appliedBy,
            });
        }

        await JournalPoster.SaveAndCommitAsync(db, tx);
    }
}
