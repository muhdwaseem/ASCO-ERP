using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>A generic result table for the "Ask AI" query feature, built via reflection over
/// whatever record type an intent's fixed query returns — so one UI component renders any intent's
/// result without per-intent markup.</summary>
public record QueryTable(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// One "Ask AI" answer. <see cref="Understood"/> false means either classification failed or the
/// question mapped to "unsupported" — <see cref="Clarification"/> carries why, and no query ever ran.
/// <see cref="Intent"/> and <see cref="ParametersJson"/> are surfaced purely for transparency (shown
/// in the UI as "how I understood that") and mirror what lands in <see cref="QueryLog"/>.
/// </summary>
public record AiQueryAnswer(
    bool Understood, string Intent, string? ParametersJson, string? Clarification,
    QueryTable? Table, string? Summary, int TotalRowCount, bool Truncated, int LatencyMs);

/// <summary>
/// Read-only natural-language query feature ("Ask AI"). Gemini's only job here
/// (<see cref="BillScanningService.ClassifyQueryIntentAsync"/>) is to classify a free-text question
/// into one of <see cref="BillScanningService.QueryIntents"/> plus typed parameters — it never sees
/// the database schema and never produces SQL or any other executable text. This service then
/// dispatches to exactly one of a handful of hardcoded, parameterized EF LINQ queries per intent
/// (<see cref="ExecuteAsync"/>'s switch is the complete list) — there is no path from model output to
/// anything executed as a query, so "never run anything but a read" holds by construction rather
/// than by inspecting what the model produced. Every query runs through the same
/// <see cref="CustomerService"/>/<see cref="VendorService"/>/<see cref="SalesInvoiceService"/>/
/// <see cref="LedgerService"/> methods (or the same <see cref="IDbContextFactory{TContext}"/>) the
/// rest of the app uses, so <see cref="AegisDbContext"/>'s existing CompanyId global query filter
/// applies exactly as it does everywhere else — this service never reads or trusts a company id from
/// the model, and read access is the same "signed-in to this company" bar as every other report page
/// (Trial Balance, Aging, etc.), not a new permission. A misclassification or unsupported question
/// just returns "can't answer that yet"; nothing here can corrupt or leak data, only fail to answer.
/// </summary>
public class AiQueryService
{
    /// <summary>Hard cap on rows returned in one answer, regardless of how broad the question is.</summary>
    public const int RowCap = 200;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly BillScanningService _gemini;
    private readonly CustomerService _customers;
    private readonly VendorService _vendors;
    private readonly LedgerService _ledger;
    private readonly SalesInvoiceService _salesInvoices;

    public AiQueryService(IDbContextFactory<AegisDbContext> dbf, BillScanningService gemini,
        CustomerService customers, VendorService vendors, LedgerService ledger, SalesInvoiceService salesInvoices)
    {
        _dbf = dbf;
        _gemini = gemini;
        _customers = customers;
        _vendors = vendors;
        _ledger = ledger;
        _salesInvoices = salesInvoices;
    }

    public async Task<AiQueryAnswer> AskAsync(string question, string askedBy, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // None of the read services below (CustomerService/VendorService/LedgerService/
        // SalesInvoiceService) accept a CancellationToken — they're the same methods every other
        // report page calls, and cancellation was never a concern there. WaitAsync enforces the
        // timeout from the outside instead: the caller gets its answer back on time regardless of
        // whether the awaited call itself observes cancellation.
        QueryIntentResult intent;
        try
        {
            intent = await _gemini.ClassifyQueryIntentAsync(question, DateOnly.FromDateTime(DateTime.UtcNow), ct).WaitAsync(Timeout, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return await FinishAsync(question, "unsupported", null, false,
                "That took too long — try a narrower question.", null, sw, askedBy, null, null);
        }
        catch (BillScanException ex)
        {
            // Distinct from "unsupported" — the model never got a chance to weigh in, so this must
            // never read to the user as "your question doesn't fit" (a 429 during the eval run
            // surfaced exactly that confusion before this fix: a rate-limited call rendered as if
            // Gemini had genuinely rejected the question). The raw exception text — which can
            // include upstream error bodies — goes to the audit log only, never to the user.
            return await FinishAsync(question, "unavailable", null, false,
                "The AI service is temporarily unavailable — try again in a moment.", ex.Message, sw, askedBy, null, null);
        }

        var paramsJson = JsonSerializer.Serialize(intent);

        if (string.Equals(intent.Intent, "unsupported", StringComparison.OrdinalIgnoreCase))
        {
            return await FinishAsync(question, intent.Intent, paramsJson, true,
                intent.Clarification ?? "I can't answer that yet — try one of the aging, invoice/bill, trial balance, or account balance questions.",
                null, sw, askedBy, intent.PromptTokens, intent.CompletionTokens);
        }

        try
        {
            var (table, totalCount, truncated, summary) = await ExecuteAsync(intent).WaitAsync(Timeout, ct);
            sw.Stop();
            await LogAsync(question, intent.Intent, paramsJson, true, null, totalCount, (int)sw.ElapsedMilliseconds,
                intent.PromptTokens, intent.CompletionTokens, askedBy);
            return new AiQueryAnswer(true, intent.Intent, paramsJson, null, table, summary, totalCount, truncated, (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return await FinishAsync(question, intent.Intent, paramsJson, false,
                "That took too long — try a narrower question.", null, sw, askedBy, intent.PromptTokens, intent.CompletionTokens);
        }
        catch (Exception ex)
        {
            return await FinishAsync(question, intent.Intent, paramsJson, false,
                "Something went wrong answering that — try rephrasing it.", ex.Message, sw, askedBy,
                intent.PromptTokens, intent.CompletionTokens);
        }
    }

    private async Task<AiQueryAnswer> FinishAsync(string question, string intentName, string? paramsJson, bool success,
        string clarification, string? errorForLog, Stopwatch sw, string askedBy, int? promptTokens, int? completionTokens)
    {
        sw.Stop();
        await LogAsync(question, intentName, paramsJson, success, errorForLog ?? (success ? null : clarification),
            success ? 0 : null, (int)sw.ElapsedMilliseconds, promptTokens, completionTokens, askedBy);
        return new AiQueryAnswer(false, intentName, paramsJson, clarification, null, null, 0, false, (int)sw.ElapsedMilliseconds);
    }

    /// <summary>The complete dispatch table: one fixed, parameterized query per intent. Nothing here
    /// is model-authored — <paramref name="intent"/> only supplies the pre-typed filter values.</summary>
    private async Task<(QueryTable Table, int TotalCount, bool Truncated, string? Summary)> ExecuteAsync(QueryIntentResult intent)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var asOf = ParseDate(intent.AsOfDate) ?? today;

        switch (intent.Intent.ToLowerInvariant())
        {
            case "overdue_invoices":
            {
                var rows = (await _salesInvoices.GetAllAsync())
                    .Where(r => r.Status == ArStatus.Overdue)
                    .Where(r => intent.MinAmount is not decimal min || r.Balance >= min)
                    .Where(r => intent.MaxAmount is not decimal max || r.Balance <= max)
                    .Where(r => intent.PartyName is not string p || r.Invoice.Customer.Name.Contains(p, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => r.Invoice.DueDate)
                    .Select(r => new
                    {
                        r.Invoice.InvoiceNo, Customer = r.Invoice.Customer.Name, r.Invoice.Date, r.Invoice.DueDate,
                        DaysOverdue = today.DayNumber - r.Invoice.DueDate.DayNumber, Balance = r.Balance,
                    })
                    .ToList();
                return Cap(rows, $"{rows.Count} overdue customer invoice(s) totalling AED {rows.Sum(r => r.Balance):N2}.");
            }

            case "invoices_by_status":
            {
                var status = ParseArStatus(intent.Status);
                var from = ParseDate(intent.FromDate);
                var to = ParseDate(intent.ToDate);
                var rows = (await _salesInvoices.GetAllAsync())
                    .Where(r => status is not ArStatus s || r.Status == s)
                    .Where(r => from is not DateOnly f || r.Invoice.Date >= f)
                    .Where(r => to is not DateOnly t || r.Invoice.Date <= t)
                    .Where(r => intent.MinAmount is not decimal min || r.Invoice.TotalGross >= min)
                    .Where(r => intent.MaxAmount is not decimal max || r.Invoice.TotalGross <= max)
                    .Where(r => intent.PartyName is not string p || r.Invoice.Customer.Name.Contains(p, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => r.Invoice.Date)
                    .Select(r => new
                    {
                        r.Invoice.InvoiceNo, Customer = r.Invoice.Customer.Name, r.Invoice.Date, r.Invoice.DueDate,
                        Status = r.Status.ToString(), Total = r.Invoice.TotalGross, Balance = r.Balance,
                    })
                    .ToList();
                return Cap(rows, $"{rows.Count} customer invoice(s) totalling AED {rows.Sum(r => r.Total):N2}.");
            }

            case "overdue_bills":
            {
                var rows = (await _vendors.GetAllWithStatusAsync())
                    .Where(r => r.Status == "Overdue")
                    .Where(r => intent.MinAmount is not decimal min || r.Balance >= min)
                    .Where(r => intent.MaxAmount is not decimal max || r.Balance <= max)
                    .Where(r => intent.PartyName is not string p || r.VendorName.Contains(p, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => r.DueDate)
                    .Select(r => new
                    {
                        r.InvoiceNo, Vendor = r.VendorName, r.Date, r.DueDate,
                        DaysOverdue = today.DayNumber - r.DueDate.DayNumber, r.Balance,
                    })
                    .ToList();
                return Cap(rows, $"{rows.Count} overdue vendor bill(s) totalling AED {rows.Sum(r => r.Balance):N2}.");
            }

            case "bills_by_status":
            {
                var from = ParseDate(intent.FromDate);
                var to = ParseDate(intent.ToDate);
                var rows = (await _vendors.GetAllWithStatusAsync())
                    .Where(r => intent.Status is not string s || string.Equals(r.Status, s, StringComparison.OrdinalIgnoreCase))
                    .Where(r => from is not DateOnly f || r.Date >= f)
                    .Where(r => to is not DateOnly t || r.Date <= t)
                    .Where(r => intent.MinAmount is not decimal min || r.Gross >= min)
                    .Where(r => intent.MaxAmount is not decimal max || r.Gross <= max)
                    .Where(r => intent.PartyName is not string p || r.VendorName.Contains(p, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => r.Date)
                    .Select(r => new { r.InvoiceNo, Vendor = r.VendorName, r.Date, r.DueDate, r.Status, Total = r.Gross, r.Balance })
                    .ToList();
                return Cap(rows, $"{rows.Count} vendor bill(s) totalling AED {rows.Sum(r => r.Total):N2}.");
            }

            case "trial_balance":
            {
                if (ParseDate(intent.FromDate) is DateOnly fromDate && ParseDate(intent.ToDate) is DateOnly toDate)
                {
                    var tb = await _ledger.GetTrialBalanceAsync(fromDate, toDate);
                    var rows = tb.Rows.Select(r => new { r.Code, r.Name, r.PeriodDebit, r.PeriodCredit }).ToList();
                    return Cap(rows, $"Trial balance for {tb.RangeLabel}: total debit AED {tb.TotalPeriodDebit:N2}, total credit AED {tb.TotalPeriodCredit:N2}.");
                }
                else
                {
                    // No period; build a period id via periods list matching asOf, else use asOf-based overload.
                    var periods = await _ledger.GetPeriodsAsync();
                    var period = periods.FirstOrDefault(p => asOf >= p.StartDate && asOf <= p.EndDate) ?? periods.LastOrDefault();
                    var tb = period is not null ? await _ledger.GetTrialBalanceAsync(period.Id) : null;
                    if (tb is null) return (new QueryTable(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>()), 0, false, "No fiscal periods are set up yet.");
                    var rows = tb.Rows.Select(r => new { r.Code, r.Name, r.Debit, r.Credit }).ToList();
                    return Cap(rows, $"Trial balance as of {tb.Period}: total debit AED {tb.TotalDebit:N2}, total credit AED {tb.TotalCredit:N2}.");
                }
            }

            case "account_balance":
            {
                if (string.IsNullOrWhiteSpace(intent.AccountNameOrCode))
                    return (new QueryTable(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>()), 0, false, "Which account did you mean?");

                var result = await _ledger.GetAccountBalanceAsync(intent.AccountNameOrCode, asOf);
                if (result is null)
                    return (new QueryTable(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>()), 0, false,
                        $"No account matching \"{intent.AccountNameOrCode}\" was found.");

                var (code, name, balance) = result.Value;
                var rows = new[] { new { Code = code, Name = name, AsOf = asOf, Balance = balance } }.ToList();
                return Cap(rows, $"{name} ({code}) balance as of {asOf:dd MMM yyyy}: AED {balance:N2}.");
            }

            case "ar_aging":
            {
                var rows = (await _customers.GetAgingAsync(asOf))
                    .Where(r => intent.PartyName is not string p || r.Name.Contains(p, StringComparison.OrdinalIgnoreCase))
                    .Where(r => r.Total != 0)
                    .OrderByDescending(r => r.Total)
                    .Select(r => new { r.Code, r.Name, r.Current, r.Days1To30, r.Days31To60, r.Days61To90, r.Over90, r.Total })
                    .ToList();
                return Cap(rows, $"AR aging as of {asOf:dd MMM yyyy} for {rows.Count} customer(s): total outstanding AED {rows.Sum(r => r.Total):N2}.");
            }

            case "ap_aging":
            {
                var rows = (await _vendors.GetAgingAsync(asOf))
                    .Where(r => intent.PartyName is not string p || r.Name.Contains(p, StringComparison.OrdinalIgnoreCase))
                    .Where(r => r.Total != 0)
                    .OrderByDescending(r => r.Total)
                    .Select(r => new { r.Code, r.Name, r.Current, r.Days1To30, r.Days31To60, r.Days61To90, r.Over90, r.Total })
                    .ToList();
                return Cap(rows, $"AP aging as of {asOf:dd MMM yyyy} for {rows.Count} vendor(s): total outstanding AED {rows.Sum(r => r.Total):N2}.");
            }

            default:
                // Unreachable: BillScanningService.ClassifyQueryIntentAsync only ever returns a value
                // from BillScanningService.QueryIntents, and "unsupported" is handled before this is
                // called — but fail safe rather than throw if that ever changes.
                return (new QueryTable(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>()), 0, false,
                    "I can't answer that yet.");
        }
    }

    private static ArStatus? ParseArStatus(string? s) =>
        Enum.TryParse<ArStatus>(s, ignoreCase: true, out var v) ? v : null;

    private static DateOnly? ParseDate(string? s) => DateOnly.TryParse(s, out var d) ? d : null;

    /// <summary>Applies the row cap and builds the generic display table via reflection — every
    /// intent above returns an anonymous record shape, so this is the one place that turns any of
    /// them into <see cref="QueryTable"/> without per-intent rendering code.</summary>
    private static (QueryTable Table, int TotalCount, bool Truncated, string? Summary) Cap<T>(List<T> rows, string summary)
    {
        var totalCount = rows.Count;
        var capped = rows.Take(RowCap).ToList();
        return (ToTable(capped), totalCount, totalCount > RowCap, summary);
    }

    private static QueryTable ToTable<T>(List<T> rows)
    {
        if (rows.Count == 0) return new QueryTable(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());

        var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var columns = props.Select(p => p.Name).ToList();
        var tableRows = rows.Select(r => (IReadOnlyList<string>)props.Select(p => FormatValue(p.GetValue(r))).ToList()).ToList();
        return new QueryTable(columns, tableRows);
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "",
        decimal d => d.ToString("N2"),
        DateOnly date => date.ToString("dd MMM yyyy"),
        DateTime dt => dt.ToString("dd MMM yyyy"),
        _ => value.ToString() ?? "",
    };

    private async Task LogAsync(string question, string? intent, string? parametersJson, bool success,
        string? errorMessage, int? rowCount, int latencyMs, int? promptTokens, int? completionTokens, string askedBy)
    {
        try
        {
            await using var db = await _dbf.CreateDbContextAsync();
            db.QueryLogs.Add(new QueryLog
            {
                Question = question,
                Intent = intent,
                ParametersJson = parametersJson,
                Success = success,
                ErrorMessage = errorMessage,
                RowCount = rowCount,
                LatencyMs = latencyMs,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                AskedBy = askedBy,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        catch
        {
            // The audit log is a convenience for the eval/transparency trail, never something the
            // feature depends on — a logging failure must never turn a successful (or already-failed)
            // answer into a worse one for the user.
        }
    }
}
