using System.Text.Json;
using AegisErp.Domain;
using AegisErp.Infrastructure;
using AegisErp.Infrastructure.Services;
using Asco.Api.Modules;
using Microsoft.EntityFrameworkCore;

namespace Asco.Api.Ai;

public record AiToolSpec(string Name, string Description, Dictionary<string, object> Properties, string[] Required);

/// <summary>
/// The read-only tools the assistant may call. Each one runs a C-ERP service (or ASCO module query)
/// under the request's CurrentCompany, so the model can only ever see the active company's books,
/// and nothing here can post, edit or delete. Results are JSON; large lists are capped and say so.
/// </summary>
public sealed class AiTools(LedgerService ledger, CustomerService customers, VendorService vendors,
    IDbContextFactory<AegisDbContext> erp, ModulesDbContext modules)
{
    private const int MaxRows = 150;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    private static Dictionary<string, object> Date(string d) => new() { ["type"] = "string", ["description"] = d + " (YYYY-MM-DD)" };

    public static readonly AiToolSpec[] Specs =
    [
        new("get_trial_balance", "Trial balance (opening, period and closing debit/credit per account) for a date range. Use for balances of any account.",
            new() { ["from"] = Date("Start date, default start of this year"), ["to"] = Date("End date, default today") }, []),
        new("get_profit_and_loss", "Profit & loss statement (income, cost of sales, expenses, net profit) for a date range.",
            new() { ["from"] = Date("Start date, default start of this year"), ["to"] = Date("End date, default today") }, []),
        new("get_balance_sheet", "Balance sheet (assets, liabilities, equity) as of a date.", new() { ["as_of"] = Date("As-of date, default today") }, []),
        new("get_cash_balances", "Current balance of every cash and bank account.", new(), []),
        new("get_receivables_aging", "Customer receivables aging buckets (current, 1-30, 31-60, 61-90, 90+ days) as of a date.", new() { ["as_of"] = Date("As-of date, default today") }, []),
        new("get_payables_aging", "Vendor payables aging buckets as of a date.", new() { ["as_of"] = Date("As-of date, default today") }, []),
        new("get_outstanding_invoices", "Unpaid sales invoices with amount due and days overdue.", new() { ["as_of"] = Date("As-of date, default today") }, []),
        new("get_customer_summary", "Every customer's invoiced, received and outstanding totals, plus credit limit.", new(), []),
        new("get_vendor_summary", "Every vendor's billed, paid and outstanding totals.", new(), []),
        new("search_general_ledger", "Posted general-ledger lines in a date range, optionally filtered by account code and/or text in the narration.",
            new()
            {
                ["from"] = Date("Start date"), ["to"] = Date("End date"),
                ["account_code"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Exact account code, optional" },
                ["text"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Case-insensitive text to find in narration or account name, optional" },
            }, ["from", "to"]),
        new("get_inventory_valuation", "Stock quantity, average cost and value per item (only if the Inventory module is enabled).", new(), []),
        new("get_job_profitability", "Jobs/shipments/projects with revenue, cost, margin and budget use (only if the Jobs module is enabled).", new(), []),
    ];

    private static DateOnly D(JsonElement input, string key, DateOnly fallback) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && DateOnly.TryParse(v.GetString(), out var d) ? d : fallback;
    private static string? S(JsonElement input, string key) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static string Out(object value) => JsonSerializer.Serialize(value, Json);
    private static string Capped<T>(IReadOnlyCollection<T> rows, string what) =>
        Out(rows.Count <= MaxRows ? new { rows, count = rows.Count }
            : new { rows = rows.Take(MaxRows), count = rows.Count, note = $"Showing first {MaxRows} of {rows.Count} {what}; narrow the date range or filter for the rest." });

    /// <summary>Runs a tool. Unknown tools and failures come back as an error string the model can read.</summary>
    public async Task<(string Result, bool IsError)> ExecuteAsync(string name, JsonElement input)
    {
        try
        {
            var yearStart = new DateOnly(Today.Year, 1, 1);
            switch (name)
            {
                case "get_trial_balance":
                    return (Out(await ledger.GetTrialBalanceAsync(D(input, "from", yearStart), D(input, "to", Today))), false);
                case "get_profit_and_loss":
                    return (Out(await ledger.GetProfitAndLossAsync(D(input, "from", yearStart), D(input, "to", Today))), false);
                case "get_balance_sheet":
                    return (Out(await ledger.GetBalanceSheetAsync(D(input, "as_of", Today))), false);
                case "get_cash_balances":
                    return (Out(await ledger.GetCashBalancesAsync()), false);
                case "get_receivables_aging":
                    return (Capped(await customers.GetAgingAsync(D(input, "as_of", Today)), "customers"), false);
                case "get_payables_aging":
                    return (Capped(await vendors.GetAgingAsync(D(input, "as_of", Today)), "vendors"), false);
                case "get_outstanding_invoices":
                    return (Capped(await customers.GetOutstandingInvoicesAsync(D(input, "as_of", Today)), "invoices"), false);
                case "get_customer_summary":
                {
                    await using var db = await erp.CreateDbContextAsync();
                    var limits = await db.Customers.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.CreditLimit);
                    var rows = (await customers.GetSummariesAsync()).Select(s => new { s.Code, s.Name, s.Invoiced, s.Received, s.Outstanding, CreditLimit = limits.GetValueOrDefault(s.Id), s.Salesperson }).ToList();
                    return (Capped(rows, "customers"), false);
                }
                case "get_vendor_summary":
                    return (Capped(await vendors.GetSummariesAsync(), "vendors"), false);
                case "search_general_ledger":
                {
                    var from = D(input, "from", yearStart);
                    var to = D(input, "to", Today);
                    var gl = await ledger.GetGeneralLedgerAsync(from, to);
                    var code = S(input, "account_code");
                    var text = S(input, "text");
                    var rows = gl.Rows.Where(r => (code is null || r.AccountCode == code)
                        && (text is null || (r.Narration?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) || r.AccountName.Contains(text, StringComparison.OrdinalIgnoreCase)))
                        .Select(r => new { r.Date, r.VoucherNo, r.Type, r.AccountCode, r.AccountName, r.Narration, r.Debit, r.Credit }).ToList();
                    return (Capped(rows, "ledger lines"), false);
                }
                case "get_inventory_valuation":
                {
                    if (!(await modules.ProfileAsync()).ModuleSet().Contains(ModuleKeys.Inventory)) return ("The Inventory module is not enabled for this company.", false);
                    var lines = await modules.StockMoveLines.Select(l => new { l.ItemId, l.Quantity, l.Value }).ToListAsync();
                    await using var db = await erp.CreateDbContextAsync();
                    var ids = lines.Select(l => l.ItemId).Distinct().ToList();
                    var items = await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => $"{i.Code} {i.Name}");
                    var rows = lines.GroupBy(l => l.ItemId).Select(g => new { item = items.GetValueOrDefault(g.Key), quantity = g.Sum(x => x.Quantity), value = g.Sum(x => x.Value) }).ToList();
                    return (Capped(rows, "items"), false);
                }
                case "get_job_profitability":
                {
                    if (!(await modules.ProfileAsync()).ModuleSet().Contains(ModuleKeys.Jobs)) return ("The Jobs module is not enabled for this company.", false);
                    var jobs = await modules.Jobs.ToListAsync();
                    var ccIds = jobs.Where(j => j.CostCenterId != null).Select(j => j.CostCenterId!.Value).ToList();
                    await using var db = await erp.CreateDbContextAsync();
                    var gl = await db.JournalLines.AsNoTracking().Where(l => l.CostCenterId != null && ccIds.Contains(l.CostCenterId.Value) && l.JournalVoucher.Status == VoucherStatus.Posted)
                        .Select(l => new { cc = l.CostCenterId!.Value, l.Account.Type, l.Debit, l.Credit }).ToListAsync();
                    var rows = jobs.Select(j =>
                    {
                        var mine = gl.Where(x => x.cc == j.CostCenterId).ToList();
                        var rev = mine.Where(x => x.Type == AccountType.Income).Sum(x => x.Credit - x.Debit);
                        var cost = mine.Where(x => x.Type == AccountType.Expense).Sum(x => x.Debit - x.Credit);
                        return new { j.JobNo, j.Title, type = j.Type.ToString(), status = j.Status.ToString(), j.Budget, revenue = rev, cost, margin = rev - cost };
                    }).ToList();
                    return (Capped(rows, "jobs"), false);
                }
                default:
                    return ($"Unknown tool '{name}'.", true);
            }
        }
        catch (PostingException ex) { return (ex.Message, true); }
        catch (Exception ex) { return ($"Tool failed: {ex.Message}", true); }
    }
}
