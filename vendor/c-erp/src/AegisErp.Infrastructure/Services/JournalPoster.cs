using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace AegisErp.Infrastructure.Services;

/// <summary>
/// Shared posting engine. Builds and validates a GL voucher on the caller's DbContext
/// WITHOUT saving or committing — the caller owns the transaction, so a document and its
/// voucher (e.g. invoice + GL entry) are persisted atomically.
/// </summary>
internal static class JournalPoster
{
    public static readonly IReadOnlyDictionary<VoucherType, string> Prefixes = new Dictionary<VoucherType, string>
    {
        [VoucherType.Journal] = "JV",
        [VoucherType.Receipt] = "RV",
        [VoucherType.Payment] = "PV",
        [VoucherType.SalesInvoice] = "INV",
        [VoucherType.PurchaseInvoice] = "PINV",
        [VoucherType.Opening] = "OB",
        [VoucherType.CreditNote] = "CN",
        [VoucherType.DebitNote] = "DN",
        [VoucherType.Expense] = "EXP",
    };

    /// <summary>
    /// Next document number for a prefix within a year, e.g. "INV-2026-0143".
    /// Uses max existing suffix + 1 so explicit seed numbers don't cause collisions.
    /// </summary>
    public static async Task<string> NextDocNoAsync(AegisDbContext db, string prefix, int year)
    {
        var stem = $"{prefix}-{year}-";
        await AcquireDocNoLockAsync(db, stem);
        var existing = await db.JournalVouchers
            .Where(v => v.VoucherNo.StartsWith(stem))
            .Select(v => v.VoucherNo)
            .ToListAsync();
        return NextDocNo(existing, prefix, year);
    }

    /// <summary>
    /// Serializes concurrent number allocation for one (prefix, year) stem on Postgres so two
    /// simultaneous posts can't both compute the same "next" number (the "Concurrent document
    /// numbering" gap noted in the README). <c>hashtext</c> runs server-side so every connection —
    /// including other app instances — derives the same lock key for the same stem. No-op on
    /// Sqlite (a single writer at a time already serializes this) or outside an ambient
    /// transaction (e.g. a non-allocating number preview): <c>pg_advisory_xact_lock</c> requires
    /// one and auto-releases at commit/rollback, so there is never an explicit unlock to forget.
    /// NOT yet exercised against a live Postgres server — verify with a real concurrency test
    /// before relying on it in production.
    /// </summary>
    private static async Task AcquireDocNoLockAsync(AegisDbContext db, string stem)
    {
        if (!db.Database.IsNpgsql() || db.Database.CurrentTransaction is null) return;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({stem})::bigint)");
    }

    /// <summary>
    /// Next document number for a prefix within a year, computed from an already-materialised set of
    /// existing numbers. Used by non-posting documents (estimates, delivery notes) that number
    /// from their own table rather than the voucher sequence.
    /// </summary>
    public static string NextDocNo(IEnumerable<string> existing, string prefix, int year)
    {
        var stem = $"{prefix}-{year}-";
        var max = 0;
        foreach (var no in existing)
            if (no.StartsWith(stem) && int.TryParse(no.AsSpan(stem.Length), out var n) && n > max)
                max = n;
        return $"{stem}{max + 1:0000}";
    }

    /// <summary>
    /// Creates a voucher, validates it against the fiscal period, runs the domain posting
    /// rules and adds it to the context. Throws <see cref="PostingException"/> on any violation.
    /// <paramref name="canPost"/> enforces the same permission the UI already hides posting
    /// buttons behind (<c>CompanySession.CanPost</c>) — checked here too since this is the one
    /// place every posting flow in the app routes through, so a Viewer-role account can't post a
    /// document just by finding a path around a hidden button.
    /// </summary>
    public static async Task<JournalVoucher> PostAsync(
        AegisDbContext db, bool canPost, VoucherType type, string? explicitNo,
        DateOnly date, int fiscalPeriodId, string? narration, string? reference,
        string createdBy, IEnumerable<VoucherLineInput> lines, DateTime nowUtc)
    {
        if (!canPost)
            throw new PostingException("You don't have permission to post transactions.");

        var period = await db.FiscalPeriods.FindAsync(fiscalPeriodId)
            ?? throw new PostingException("Fiscal period not found.");
        if (period.IsClosed)
            throw new PostingException($"Period {period.Name} is closed.");
        if (date < period.StartDate || date > period.EndDate)
            throw new PostingException($"Date {date:yyyy-MM-dd} falls outside period {period.Name}.");

        var voucher = new JournalVoucher
        {
            VoucherNo = explicitNo ?? await NextDocNoAsync(db, Prefixes[type], date.Year),
            Type = type,
            Date = date,
            FiscalPeriodId = fiscalPeriodId,
            Narration = narration,
            Reference = reference,
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? "System Admin" : createdBy,
            CreatedAtUtc = nowUtc,
        };

        var no = 1;
        foreach (var l in lines)
            voucher.Lines.Add(new JournalLine
            {
                LineNo = no++,
                AccountId = l.AccountId,
                CostCenterId = l.CostCenterId,
                Description = l.Description,
                Debit = l.Debit,
                Credit = l.Credit,
            });

        voucher.Post(voucher.CreatedBy, nowUtc); // domain double-entry rules; instant-post flows have one identity for both

        db.JournalVouchers.Add(voucher);
        return voucher;
    }

    /// <summary>Resolves a control account by its well-known code or fails with a clear message.</summary>
    public static async Task<Account> RequireAccountAsync(AegisDbContext db, string code)
        => await db.Accounts.FirstOrDefaultAsync(a => a.Code == code)
           ?? throw new PostingException($"Control account {code} is missing from the chart of accounts.");

    /// <summary>
    /// Saves and commits, translating a unique-constraint violation (e.g. two users posting at the
    /// same instant and computing the same document number) or a transient lock/busy error into a
    /// recoverable <see cref="PostingException"/> instead of letting a raw DbUpdateException crash
    /// the Blazor circuit.
    /// NOTE: this makes the losing writer fail cleanly; it does not by itself prevent the race. On
    /// PostgreSQL, allocate document numbers under a row lock / advisory lock or a Serializable
    /// transaction with retry (see README production-hardening notes).
    /// </summary>
    public static async Task SaveAndCommitAsync(AegisDbContext db, IDbContextTransaction tx)
    {
        try
        {
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (DbUpdateException ex)
        {
            throw Translate(ex);
        }
    }

    /// <summary>
    /// Saves a non-transactional (single-statement) change set, translating the same recoverable
    /// error classes as <see cref="SaveAndCommitAsync"/>. For services (estimates, delivery notes)
    /// that don't open their own <see cref="IDbContextTransaction"/>.
    /// </summary>
    public static async Task SaveChangesTranslatedAsync(AegisDbContext db)
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            throw Translate(ex);
        }
    }

    private static PostingException Translate(DbUpdateException ex)
    {
        // DbUpdateConcurrencyException (a DbUpdateException subtype) means a concurrency-token
        // check found the row already changed since it was loaded — e.g. two users editing the
        // same Account or CompanySetup. Check this before the message-sniffing below since its
        // message doesn't mention "UNIQUE"/"locked"/etc.
        if (ex is DbUpdateConcurrencyException)
        {
            return new PostingException(
                "This record was changed by another user since you opened it. Please reload and try again.", ex);
        }

        // On Postgres, prefer the structured SQLSTATE over string-sniffing — it's stable across
        // locales/versions and gives us the actual column/constraint name instead of a guess.
        if (ex.InnerException is PostgresException pg)
        {
            switch (pg.SqlState)
            {
                case PostgresErrorCodes.UniqueViolation:
                    return new PostingException(
                        "This document conflicts with one just posted by another user (duplicate number). Please try again.", ex);
                case PostgresErrorCodes.ForeignKeyViolation:
                    return new PostingException(
                        $"This refers to a{(pg.ColumnName is null ? "n" : $" {pg.ColumnName}")} record that doesn't exist " +
                        "(it may have been deleted, or belongs to a different company) — check your selections and try again.", ex);
                case PostgresErrorCodes.NotNullViolation:
                    return new PostingException(
                        $"{pg.ColumnName ?? "A required field"} is required and can't be left blank.", ex);
                case PostgresErrorCodes.CheckViolation:
                    return new PostingException(
                        $"That value isn't valid for {pg.ColumnName ?? "one of these fields"} — please check it and try again.", ex);
                case PostgresErrorCodes.SerializationFailure:
                case PostgresErrorCodes.DeadlockDetected:
                case PostgresErrorCodes.LockNotAvailable:
                    return new PostingException("The database was busy with another user's request. Please try again.", ex);
            }
        }

        var msg = ex.InnerException?.Message ?? ex.Message;
        // SQLite: "UNIQUE constraint failed"; PostgreSQL: "duplicate key value violates unique constraint".
        if (msg.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
        {
            return new PostingException(
                "This document conflicts with one just posted by another user (duplicate number). Please try again.",
                ex);
        }

        // SQLite: "database is locked" / "SQLITE_BUSY"; PostgreSQL: "could not serialize access" /
        // "deadlock detected" under stricter isolation levels.
        if (msg.Contains("locked", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("busy", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("serialize", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("deadlock", StringComparison.OrdinalIgnoreCase))
        {
            return new PostingException(
                "The database was busy with another user's request. Please try again.", ex);
        }

        return new PostingException("This document could not be saved. Please try again.", ex);
    }
}
