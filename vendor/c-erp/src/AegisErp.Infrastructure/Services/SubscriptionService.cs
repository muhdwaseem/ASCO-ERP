using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>
/// Firm-side billing management for client companies — enabling/disabling enforcement per company,
/// recording payments, and reading payment history. This is bookkeeping only: the firm
/// administrator collects payment however the client actually pays (bank transfer, cheque, cash)
/// and records the outcome here; nothing in this service moves money.
///
/// Enforcement of the resulting status (blocking a suspended company's own staff, while a firm
/// administrator always still gets in) lives in <c>CompanySession</c> on the Web project, which
/// derives the same status from the same three <see cref="CompanySetup"/> fields via
/// <see cref="SubscriptionStatusCalculator"/> — so there is exactly one place the due/grace/suspended
/// math is computed.
/// </summary>
public class SubscriptionService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public SubscriptionService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    /// <summary>
    /// Turns billing enforcement on/off for a company and sets its per-cycle amount and grace
    /// period. Disabling immediately restores full access regardless of payment history — see
    /// <see cref="SubscriptionStatusCalculator.Compute"/>.
    /// </summary>
    public async Task SetPlanAsync(int companyId, bool enabled, decimal? amount, int graceDays)
    {
        if (graceDays < 0) throw new PostingException("Grace period cannot be negative.");
        if (amount is < 0) throw new PostingException("Subscription amount cannot be negative.");

        await using var db = await _dbf.CreateDbContextAsync();
        var company = await db.CompanySetups.FindAsync(companyId)
            ?? throw new PostingException("Company not found.");

        company.SubscriptionEnabled = enabled;
        company.SubscriptionAmount = amount;
        company.SubscriptionGraceDays = graceDays;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Records a payment and advances the company's paid-through date. <paramref name="coversThrough"/>
    /// must move the paid-through date forward — a payment can't backdate coverage that was already
    /// recorded, which would silently un-suspend history that a firm administrator didn't intend to
    /// touch.
    /// </summary>
    public async Task<CompanySubscriptionPayment> RecordPaymentAsync(
        int companyId, DateOnly paymentDate, decimal amount, DateOnly coversThrough, string? notes,
        string recordedBy, DateTime nowUtc)
    {
        if (amount <= 0) throw new PostingException("Payment amount must be greater than zero.");

        await using var db = await _dbf.CreateDbContextAsync();
        var company = await db.CompanySetups.FindAsync(companyId)
            ?? throw new PostingException("Company not found.");

        var currentFloor = company.SubscriptionPaidThroughDate ?? paymentDate.AddDays(-1);
        if (coversThrough <= currentFloor)
            throw new PostingException("This payment's covered-through date must be after the company's current paid-through date.");

        var payment = new CompanySubscriptionPayment
        {
            CompanySetupId = companyId,
            PaymentDate = paymentDate,
            Amount = amount,
            CoversThrough = coversThrough,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            RecordedBy = recordedBy,
            RecordedAtUtc = nowUtc,
        };
        db.CompanySubscriptionPayments.Add(payment);
        company.SubscriptionPaidThroughDate = coversThrough;

        await db.SaveChangesAsync();
        return payment;
    }

    public async Task<List<CompanySubscriptionPayment>> GetPaymentHistoryAsync(int companyId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CompanySubscriptionPayments.AsNoTracking()
            .Where(p => p.CompanySetupId == companyId)
            .OrderByDescending(p => p.PaymentDate).ThenByDescending(p => p.Id)
            .ToListAsync();
    }
}
