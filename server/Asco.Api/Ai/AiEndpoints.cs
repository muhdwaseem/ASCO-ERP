using System.Security.Claims;
using System.Text.Json;
using AegisErp.Domain;
using AegisErp.Infrastructure;
using AegisErp.Infrastructure.Services;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Asco.Api.Modules;
using Microsoft.EntityFrameworkCore;
using NonBeta = Anthropic.Models.Messages;

namespace Asco.Api.Ai;

public record AskTurn(string Role, string Text);
public record AskRequest(string Question, List<AskTurn>? History);
public record AskResult(string Answer, string[] ToolsUsed, string Model, long InputTokens, long OutputTokens);

/// <summary>
/// "Ask your books": Claude with read-only tools over the active company's ledger.
/// Guardrails: tools can only read (AiTools), every question is logged (asco_ai_logs), and the
/// assistant never posts — it can suggest a journal, a human with CanPost enters it.
/// </summary>
public sealed class AiAssistant(AiTools tools, IConfiguration config)
{
    public const string Model = "claude-opus-5";
    private const int MaxToolRounds = 8;

    private string? ApiKey => config["Anthropic:ApiKey"] is { Length: > 0 } k ? k : null;
    public bool IsConfigured => ApiKey is not null
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN"));

    private static readonly List<BetaToolUnion> ToolDefs = AiTools.Specs.Select(s => (BetaToolUnion)new BetaTool
    {
        Name = s.Name,
        Description = s.Description,
        InputSchema = new()
        {
            Properties = s.Properties.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value)),
            Required = s.Required,
        },
    }).ToList();

    public async Task<AskResult> AskAsync(AskRequest req, string companyName, string currency, CancellationToken ct)
    {
        var client = ApiKey is null ? new AnthropicClient() : new AnthropicClient { ApiKey = ApiKey };
        var system =
            $"You are the finance assistant inside ASCO, an accounting system, answering for the company \"{companyName}\" " +
            $"(base currency {currency}). Today is {DateTime.Today:yyyy-MM-dd}. " +
            "Answer only from the tools' data — call a tool whenever a figure is needed and never estimate or invent numbers. " +
            "Say which report each figure came from and the date or range it covers. " +
            "You can read the books but not change them: if the user wants an entry made, describe the journal lines and tell them to post it from the Journal Voucher sheet. " +
            "Keep answers short, use the base currency, and format amounts with thousands separators.";

        var messages = new List<BetaMessageParam>();
        foreach (var turn in req.History ?? [])
            if (!string.IsNullOrWhiteSpace(turn.Text))
                messages.Add(new BetaMessageParam { Role = turn.Role == "assistant" ? Role.Assistant : Role.User, Content = turn.Text });
        messages.Add(new BetaMessageParam { Role = Role.User, Content = req.Question });

        var used = new List<string>();
        long inTok = 0, outTok = 0;
        string modelUsed = Model;

        for (var round = 0; round < MaxToolRounds; round++)
        {
            BetaMessage resp = await client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = Model,
                MaxTokens = 16000,
                System = system,
                Tools = ToolDefs,
                Messages = messages,
                // Server-side refusal fallback: a declined request is re-served by Opus 4.8 in the same call.
                Betas = ["server-side-fallback-2026-06-01"],
                Fallbacks = new List<BetaFallbackParam> { new(NonBeta.Model.ClaudeOpus4_8) },
            }, ct);
            inTok += resp.Usage.InputTokens;
            outTok += resp.Usage.OutputTokens;
            modelUsed = resp.Model.ToString() ?? Model;

            if (resp.StopReason == "refusal")
                return new AskResult("I can't help with that request.", used.ToArray(), modelUsed, inTok, outTok);

            // Echo rules after a mid-output fallback: before the last fallback block keep only text;
            // everything after it echoes normally (thinking with its signature, tool_use).
            var blocks = resp.Content.ToList();
            var boundary = blocks.FindLastIndex(b => b.TryPickFallback(out _));
            var echo = new List<BetaContentBlockParam>();
            var results = new List<BetaContentBlockParam>();
            var text = new List<string>();
            for (var i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                if (b.TryPickText(out var t)) { echo.Add(new BetaTextBlockParam { Text = t.Text }); if (i > boundary) text.Add(t.Text); continue; }
                if (i <= boundary) continue;
                if (b.TryPickThinking(out var th)) echo.Add(new BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
                else if (b.TryPickRedactedThinking(out var rt)) echo.Add(new BetaRedactedThinkingBlockParam { Data = rt.Data });
                else if (b.TryPickToolUse(out var tu))
                {
                    echo.Add(new BetaToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                    used.Add(tu.Name);
                    var (result, isError) = await tools.ExecuteAsync(tu.Name, JsonSerializer.SerializeToElement(tu.Input));
                    results.Add(new BetaToolResultBlockParam { ToolUseID = tu.ID, Content = result, IsError = isError });
                }
            }

            if (results.Count == 0)
                return new AskResult(string.Join("\n\n", text).Trim() is { Length: > 0 } a ? a : "(no answer)", used.Distinct().ToArray(), modelUsed, inTok, outTok);

            messages.Add(new BetaMessageParam { Role = Role.Assistant, Content = echo });
            messages.Add(new BetaMessageParam { Role = Role.User, Content = results });
        }
        return new AskResult("I needed too many lookups to answer that — try a narrower question.", used.Distinct().ToArray(), modelUsed, inTok, outTok);
    }
}

internal static class AiEndpoints
{
    public static IServiceCollection AddAscoAi(this IServiceCollection services)
    {
        services.AddScoped<AiTools>();
        services.AddScoped<AiAssistant>();
        return services;
    }

    public static void MapAiEndpoints(this WebApplication app)
    {
        var ai = app.MapGroup("/api/ai").RequireAuthorization().AddEndpointFilter<CompanyScopeFilter>().AddEndpointFilter(WriteEndpoints.TranslateErrors);

        ai.MapGet("/status", (AiAssistant a) => new { askConfigured = a.IsConfigured, model = AiAssistant.Model });

        ai.MapPost("/ask", async (AskRequest req, AiAssistant assistant, ModulesDbContext db, HttpContext ctx, ClaimsPrincipal u, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Question)) return Results.Problem("Ask a question.", statusCode: 400);
            if (req.Question.Length > 2000) return Results.Problem("Keep the question under 2,000 characters.", statusCode: 400);
            if (!assistant.IsConfigured)
                return Results.Problem("Ask AI isn't configured on this server — set Anthropic:ApiKey (or ANTHROPIC_API_KEY).", statusCode: StatusCodes.Status503ServiceUnavailable);
            var access = CompanyAccess.From(ctx);
            var log = new AiLog { User = WriteEndpoints.Actor(u), Question = req.Question, CreatedAtUtc = DateTime.UtcNow };
            try
            {
                var r = await assistant.AskAsync(req, access.Row.Name, "AED", ct);
                log.Answer = r.Answer; log.ToolsUsed = string.Join(',', r.ToolsUsed); log.InputTokens = (int)r.InputTokens; log.OutputTokens = (int)r.OutputTokens; log.Succeeded = true;
                return Results.Ok(r);
            }
            catch (Anthropic.Exceptions.AnthropicApiException ex)
            {
                log.Answer = ex.Message;
                return Results.Problem($"The AI service returned an error: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
            finally
            {
                db.AiLogs.Add(log);
                await db.SaveChangesAsync(CancellationToken.None);
            }
        });

        ai.MapGet("/log", async (ModulesDbContext db, HttpContext ctx) =>
            CompanyAccess.From(ctx).CanAdminister
                ? Results.Ok(await db.AiLogs.OrderByDescending(l => l.Id).Take(200).Select(l => new { l.Id, l.CreatedAtUtc, l.User, l.Question, l.ToolsUsed, l.InputTokens, l.OutputTokens, l.Succeeded }).ToListAsync())
                : Results.Problem("Company administrator access is required.", statusCode: 403));

        // Bill scanning: C-ERP's BillScanningService extracts the bill; ASCO matches the vendor and
        // returns a pre-filled purchase-invoice draft. Nothing posts until a user saves the form.
        ai.MapPost("/scan-bill", async (IFormFile file, BillScanningService scanner, IDbContextFactory<AegisDbContext> erp, CancellationToken ct) =>
        {
            if (file.Length == 0 || file.Length > 10 * 1024 * 1024) return Results.Problem("Upload a PDF or image up to 10 MB.", statusCode: 400);
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            ExtractedBillData bill;
            try { bill = await scanner.ExtractAsync(ms.ToArray(), file.ContentType, ct); }
            catch (BillScanException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity); }
            await using var db = await erp.CreateDbContextAsync(ct);
            var vendors = await db.Vendors.AsNoTracking().Select(v => new { v.Id, v.Code, v.Name, v.Trn }).ToListAsync(ct);
            var match = vendors.FirstOrDefault(v => !string.IsNullOrEmpty(bill.VendorTrn) && v.Trn == bill.VendorTrn)
                ?? vendors.FirstOrDefault(v => !string.IsNullOrEmpty(bill.VendorName) && v.Name.Contains(bill.VendorName, StringComparison.OrdinalIgnoreCase));
            return Results.Ok(new { extracted = bill, vendor = match, note = match is null ? "No matching vendor — create one or pick it on the form." : null });
        }).DisableAntiforgery();

        // Rule-based insights over live data — the anomaly feed the Insights sheet shows.
        ai.MapGet("/insights", async (CustomerService customers, VendorService vendors, EmployeeService employees, LedgerService ledger,
            IDbContextFactory<AegisDbContext> erp, ModulesDbContext modules) =>
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var list = new List<object>();
            void Add(string sev, string area, string finding, string action) => list.Add(new { severity = sev, area, finding, action });

            foreach (var o in (await customers.GetOutstandingInvoicesAsync(today)).Where(o => o.DaysOverdue > 30).OrderByDescending(o => o.AmountDue).Take(15))
                Add(o.DaysOverdue > 90 ? "High" : "Medium", "Receivables", $"{o.InvoiceNo} — {o.CustomerName} owes {o.AmountDue:N2}, {o.DaysOverdue} days overdue", "Send a reminder / statement");

            await using (var db = await erp.CreateDbContextAsync())
            {
                var limits = await db.Customers.AsNoTracking().Where(c => c.CreditLimit > 0).ToDictionaryAsync(c => c.Id, c => c.CreditLimit);
                foreach (var s in await customers.GetSummariesAsync())
                    if (limits.TryGetValue(s.Id, out var lim) && s.Outstanding > lim)
                        Add("Medium", "Credit control", $"{s.Name} owes {s.Outstanding:N2}, over its {lim:N2} credit limit", "Hold new invoices until paid down");
                var drafts = await db.JournalVouchers.CountAsync(v => v.Status == VoucherStatus.Draft);
                if (drafts > 0) Add("Low", "Approvals", $"{drafts} journal voucher(s) are still drafts", "Review, approve and post from the Voucher Register");
                var unpaid = await db.PayrollRuns.CountAsync(r => r.Status == VoucherStatus.Posted && !r.IsPaid);
                if (unpaid > 0) Add("Medium", "Payroll", $"{unpaid} posted payroll run(s) not marked paid", "Pay via WPS, then Mark Paid");
            }

            foreach (var v in (await vendors.GetAgingAsync(today)).Where(v => v.Days61To90 + v.Over90 > 0))
                Add("Medium", "Payables", $"{v.Name}: {v.Days61To90 + v.Over90:N2} more than 60 days past due", "Schedule payment to protect supplier terms");

            foreach (var d in (await employees.GetExpiringDocumentsAsync(60)).Take(20))
                Add("Medium", "HR compliance", $"Expiring document: {d}", "Start renewal with the PRO team");

            var prof = await modules.ProfileAsync();
            var mods = prof.ModuleSet();
            if (mods.Contains(ModuleKeys.Inventory))
            {
                var settings = await modules.StockItemSettings.Where(s => s.ReorderLevel > 0).ToListAsync();
                var qty = (await modules.StockMoveLines.Select(l => new { l.ItemId, l.Quantity }).ToListAsync()).GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
                foreach (var s in settings.Where(s => qty.GetValueOrDefault(s.ItemId) < s.ReorderLevel))
                    Add("Medium", "Inventory", $"Item #{s.ItemId} is at {qty.GetValueOrDefault(s.ItemId):0.##}, below its reorder level {s.ReorderLevel:0.##}", "Raise a purchase order");
            }
            if (mods.Contains(ModuleKeys.Jobs))
            {
                var jobs = await modules.Jobs.Where(j => j.Budget > 0 && j.CostCenterId != null && j.Status != JobStatus.Cancelled).ToListAsync();
                if (jobs.Count > 0)
                {
                    var ids = jobs.Select(j => j.CostCenterId!.Value).ToList();
                    await using var db = await erp.CreateDbContextAsync();
                    var costs = (await db.JournalLines.AsNoTracking().Where(l => l.CostCenterId != null && ids.Contains(l.CostCenterId.Value) && l.Account.Type == AccountType.Expense && l.JournalVoucher.Status == VoucherStatus.Posted)
                        .Select(l => new { cc = l.CostCenterId!.Value, l.Debit, l.Credit }).ToListAsync()).GroupBy(x => x.cc).ToDictionary(g => g.Key, g => g.Sum(x => x.Debit - x.Credit));
                    foreach (var j in jobs.Where(j => costs.GetValueOrDefault(j.CostCenterId!.Value) > j.Budget))
                        Add("High", "Jobs", $"{j.JobNo} {j.Title}: cost {costs[j.CostCenterId!.Value]:N2} is over its {j.Budget:N2} budget", "Review job costs / bill the variation");
                }
            }
            if (list.Count == 0) Add("OK", "—", "No issues found in this company's books", "");
            return list;
        });
    }
}
