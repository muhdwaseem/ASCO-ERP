using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AegisErp.Infrastructure;

/// <summary>
/// Periodically (1) generates due Draft invoices from active Recurring Invoice profiles, and
/// (2) sends automated payment reminders per each company's configured day-offsets (Invoice
/// Preferences). Runs as a singleton, so — unlike every request-scoped service in this app — it
/// cannot simply inject <see cref="IDbContextFactory{AegisDbContext}"/> (that's registered Scoped
/// specifically so it can pick up the request-scoped <see cref="ICurrentCompany"/>). Instead it
/// opens a fresh <see cref="IServiceScope"/> per company per tick and sets that scope's
/// <see cref="CurrentCompany"/> explicitly, so every scoped service resolved from it behaves
/// exactly as it would for a real user working in that company.
/// </summary>
public class InvoiceAutomationHostedService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InvoiceAutomationHostedService> _logger;

    public InvoiceAutomationHostedService(IServiceScopeFactory scopeFactory, ILogger<InvoiceAutomationHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        do
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Invoice automation tick failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        List<int> companyIds;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AegisDbContext>>();
            await using var db = await dbf.CreateDbContextAsync(ct);
            companyIds = await db.CompanySetups.Select(c => c.Id).ToListAsync(ct);
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var companyId in companyIds)
        {
            // One company's failure (e.g. SMTP not configured, a bad recurring profile) must never
            // stop the rest — each is fully isolated.
            try { await RunForCompanyAsync(companyId, today, ct); }
            catch (Exception ex) { _logger.LogError(ex, "Invoice automation failed for company {CompanyId}.", companyId); }
        }
    }

    private async Task RunForCompanyAsync(int companyId, DateOnly today, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var current = scope.ServiceProvider.GetRequiredService<CurrentCompany>();
        current.CompanyId = companyId;

        var recurringSvc = scope.ServiceProvider.GetRequiredService<RecurringInvoiceService>();
        try { await recurringSvc.GenerateDueInvoicesAsync(today, "System (Recurring)", DateTime.UtcNow); }
        catch (Exception ex) { _logger.LogError(ex, "Recurring invoice generation failed for company {CompanyId}.", companyId); }

        await SendDueRemindersAsync(scope.ServiceProvider, today, ct);
    }

    private async Task SendDueRemindersAsync(IServiceProvider services, DateOnly today, CancellationToken ct)
    {
        var companySvc = services.GetRequiredService<CompanyService>();
        CompanySetup company;
        try { company = await companySvc.GetAsync(); }
        catch (PostingException) { return; }

        var afterDueOffsets = (company.ReminderDaysAfterDue ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : (int?)null)
            .Where(n => n is not null)
            .Select(n => n!.Value)
            .ToHashSet();

        var dbf = services.GetRequiredService<IDbContextFactory<AegisDbContext>>();
        await using var db = await dbf.CreateDbContextAsync(ct);
        var candidates = await db.SalesInvoices.AsNoTracking()
            .Where(i => i.Status == VoucherStatus.Posted)
            .Select(i => new { i.Id, i.DueDate })
            .ToListAsync(ct);

        var invoiceSvc = services.GetRequiredService<SalesInvoiceService>();
        foreach (var invoice in candidates)
        {
            var daysUntilDue = invoice.DueDate.DayNumber - today.DayNumber; // positive = still before due
            var isDueForReminder =
                (company.ReminderDaysBeforeDue > 0 && daysUntilDue == company.ReminderDaysBeforeDue) ||
                (company.ReminderOnDueDate && daysUntilDue == 0) ||
                (daysUntilDue < 0 && afterDueOffsets.Contains(-daysUntilDue));
            if (!isDueForReminder) continue;

            var lastSent = await invoiceSvc.GetLastReminderSentAtAsync(invoice.Id);
            if (lastSent is DateTime last && DateOnly.FromDateTime(last) == today) continue; // already reminded today

            try { await invoiceSvc.SendReminderAsync(invoice.Id, "System (Automated)", DateTime.UtcNow, isAutomated: true); }
            catch (PostingException) { /* not eligible right now (fully paid since, no email on file, etc.) — skip quietly */ }
            catch (Exception ex) { _logger.LogWarning(ex, "Automated reminder failed for invoice {InvoiceId}.", invoice.Id); }
        }
    }
}
