using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

public class JournalService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;
    public JournalService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    /// <summary>
    /// Recent postings to the GL. Every posted document creates a row here — invoices, receipts,
    /// payments, credit/debit notes and journal entries alike — so pass <paramref name="type"/>
    /// to narrow to just one kind (e.g. the Journal Voucher page's own entries).
    /// </summary>
    public async Task<List<JournalVoucher>> GetRecentAsync(int take = 50, VoucherType? type = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.JournalVouchers.AsNoTracking().Include(v => v.Lines).AsQueryable();
        if (type is VoucherType t) q = q.Where(v => v.Type == t);
        return await q.OrderByDescending(v => v.Date).ThenByDescending(v => v.Id)
            .Take(take).ToListAsync();
    }

    /// <summary>One voucher with full detail (lines, their accounts and cost centers) — for the
    /// "view voucher" popup opened from a General Ledger row's voucher link.</summary>
    public async Task<JournalVoucher?> GetByVoucherNoAsync(string voucherNo)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.JournalVouchers.AsNoTracking()
            .Include(v => v.Lines).ThenInclude(l => l.Account)
            .Include(v => v.Lines).ThenInclude(l => l.CostCenter)
            .FirstOrDefaultAsync(v => v.VoucherNo == voucherNo);
    }

    /// <summary>One voucher with full detail, by id — for the Journal Voucher page's detail panel.</summary>
    public async Task<JournalVoucher?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.JournalVouchers.AsNoTracking()
            .Include(v => v.Lines).ThenInclude(l => l.Account)
            .Include(v => v.Lines).ThenInclude(l => l.CostCenter)
            .FirstOrDefaultAsync(v => v.Id == id);
    }

    /// <summary>Next document number for a type within a year, e.g. "JV-2026-0007".</summary>
    public async Task<string> PeekNextVoucherNoAsync(VoucherType type, int year)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await JournalPoster.NextDocNoAsync(db, JournalPoster.Prefixes[type], year);
    }

    /// <summary>
    /// Creates a voucher and posts it in a single transaction. Validation happens in the
    /// domain (<see cref="JournalVoucher.Post"/>); the transaction guarantees all-or-nothing.
    /// </summary>
    public async Task<JournalVoucher> CreateAndPostAsync(
        VoucherType type, DateOnly date, int fiscalPeriodId,
        string? narration, string? reference, string createdBy,
        IEnumerable<VoucherLineInput> lines, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var voucher = await JournalPoster.PostAsync(
            db, _current.CanPost, type, explicitNo: null, date, fiscalPeriodId,
            narration, reference, createdBy, lines, nowUtc);

        await JournalPoster.SaveAndCommitAsync(db, tx);
        return voucher;
    }

    /// <summary>
    /// Saves a voucher as a Draft — a document number is assigned now, but it doesn't hit the
    /// ledger until <see cref="PostDraftAsync"/>. A draft is allowed to be incomplete (that's the
    /// point of a draft): each line just needs an account picked; balance/line-count/amount rules
    /// are only enforced at post time (see <see cref="JournalVoucher.Post"/>).
    /// </summary>
    public async Task<JournalVoucher> CreateDraftAsync(
        VoucherType type, DateOnly date, int fiscalPeriodId,
        string? narration, string? reference, string createdBy,
        IEnumerable<VoucherLineInput> lines, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();

        if (await db.FiscalPeriods.FindAsync(fiscalPeriodId) is null)
            throw new PostingException("Fiscal period not found.");

        var voucher = new JournalVoucher
        {
            VoucherNo = await JournalPoster.NextDocNoAsync(db, JournalPoster.Prefixes[type], date.Year),
            Type = type,
            Date = date,
            FiscalPeriodId = fiscalPeriodId,
            Narration = narration,
            Reference = reference,
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? "System Admin" : createdBy,
            CreatedAtUtc = nowUtc,
            Status = VoucherStatus.Draft,
        };
        AddDraftLines(voucher, lines);

        db.JournalVouchers.Add(voucher);
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return voucher;
    }

    /// <summary>
    /// Replaces a draft's header fields and lines in place — the voucher number and Draft status
    /// are untouched. Only ever valid while still Draft; once posted, a voucher is immutable and
    /// must be reversed with another journal entry instead of edited.
    /// </summary>
    public async Task<JournalVoucher> UpdateDraftAsync(
        int voucherId, VoucherType type, DateOnly date, int fiscalPeriodId,
        string? narration, string? reference, IEnumerable<VoucherLineInput> lines)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var voucher = await db.JournalVouchers.Include(v => v.Lines).FirstOrDefaultAsync(v => v.Id == voucherId)
            ?? throw new PostingException("Voucher not found.");
        if (voucher.Status != VoucherStatus.Draft)
            throw new PostingException("Only draft vouchers can be edited — a posted voucher already hit the ledger; reverse it with another journal entry instead.");
        if (await db.FiscalPeriods.FindAsync(fiscalPeriodId) is null)
            throw new PostingException("Fiscal period not found.");

        voucher.Type = type;
        voucher.Date = date;
        voucher.FiscalPeriodId = fiscalPeriodId;
        voucher.Narration = narration;
        voucher.Reference = reference;

        voucher.Lines.Clear();
        AddDraftLines(voucher, lines);

        await JournalPoster.SaveChangesTranslatedAsync(db);
        return voucher;
    }

    /// <summary>
    /// A draft is allowed to be incomplete in most respects, but AccountId is a required foreign
    /// key at the schema level — leaving it unset (the form's "— select —" placeholder maps to 0)
    /// would otherwise reach SaveChanges as a raw FK violation instead of this clearer message.
    /// </summary>
    private static void AddDraftLines(JournalVoucher voucher, IEnumerable<VoucherLineInput> lines)
    {
        var no = 1;
        foreach (var l in lines)
        {
            if (l.AccountId == 0)
                throw new PostingException($"Line {no}: pick an account before saving (even as a draft).");
            voucher.Lines.Add(new JournalLine
            {
                LineNo = no++,
                AccountId = l.AccountId,
                CostCenterId = l.CostCenterId,
                Description = l.Description,
                Debit = l.Debit,
                Credit = l.Credit,
            });
        }
        if (voucher.Lines.Count == 0)
            throw new PostingException("Voucher needs at least one line.");
    }

    /// <summary>Posts a previously-saved draft under the same voucher number.</summary>
    public async Task<JournalVoucher> PostDraftAsync(int voucherId, string postedBy, DateTime nowUtc)
    {
        if (!_current.CanPost)
            throw new PostingException("You don't have permission to post transactions.");

        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var voucher = await db.JournalVouchers.Include(v => v.Lines)
            .FirstOrDefaultAsync(v => v.Id == voucherId)
            ?? throw new PostingException("Voucher not found.");

        var period = await db.FiscalPeriods.FindAsync(voucher.FiscalPeriodId)
            ?? throw new PostingException("Fiscal period not found.");
        if (period.IsClosed)
            throw new PostingException($"Period {period.Name} is closed.");
        if (voucher.Date < period.StartDate || voucher.Date > period.EndDate)
            throw new PostingException($"Date {voucher.Date:yyyy-MM-dd} falls outside period {period.Name}.");

        voucher.Post(postedBy, nowUtc); // domain double-entry rules + approval gate

        await JournalPoster.SaveAndCommitAsync(db, tx);
        return voucher;
    }

    /// <summary>Voids a draft voucher. Posted vouchers already hit the ledger — reverse those with another journal entry instead.</summary>
    public async Task VoidDraftAsync(int voucherId, string voidedBy, DateTime nowUtc)
    {
        if (!_current.CanPost)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var voucher = await db.JournalVouchers.FirstOrDefaultAsync(v => v.Id == voucherId)
            ?? throw new PostingException("Voucher not found.");

        voucher.Void(voidedBy, nowUtc);
        await db.SaveChangesAsync();
    }

    /// <summary>Submits a draft voucher for approval. Posting is blocked while pending.</summary>
    public async Task SubmitForApprovalAsync(int voucherId, string submittedBy, DateTime nowUtc)
    {
        if (!_current.CanPost)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var voucher = await db.JournalVouchers.FirstOrDefaultAsync(v => v.Id == voucherId)
            ?? throw new PostingException("Voucher not found.");

        voucher.SubmitForApproval(submittedBy, nowUtc);
        await db.SaveChangesAsync();
    }

    /// <summary>Approves a pending voucher, clearing the block on posting. Requires CanAdminister,
    /// not just CanPost — the UI only shows Approve/Reject to an Admin (see
    /// JournalVoucherDetailPanel's <c>_canApprove</c>), so an Accountant who can post their own
    /// drafts shouldn't be able to approve them server-side either.</summary>
    public async Task ApproveAsync(int voucherId, string approvedBy, DateTime nowUtc)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var voucher = await db.JournalVouchers.FirstOrDefaultAsync(v => v.Id == voucherId)
            ?? throw new PostingException("Voucher not found.");

        voucher.Approve(approvedBy, nowUtc);
        await db.SaveChangesAsync();
    }

    /// <summary>Rejects a pending voucher. It stays a Draft, editable and re-submittable. Same
    /// CanAdminister requirement as <see cref="ApproveAsync"/> — same UI gate, same decision.</summary>
    public async Task RejectAsync(int voucherId, string rejectedBy, DateTime nowUtc, string? note)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var voucher = await db.JournalVouchers.FirstOrDefaultAsync(v => v.Id == voucherId)
            ?? throw new PostingException("Voucher not found.");

        voucher.Reject(rejectedBy, nowUtc, note);
        await db.SaveChangesAsync();
    }
}
