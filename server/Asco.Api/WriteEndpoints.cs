using System.Security.Claims;
using System.Text;
using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure;
using AegisErp.Infrastructure.Identity;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Asco.Api;

// ── Request bodies. Line items reuse C-ERP's own input records, so the JSON contract is theirs. ──
public record SalesInvoiceRequest(int CustomerId, DateOnly Date, string? Narration, List<InvoiceLineInput> Lines, bool Draft = false,
    string? CustomerPoNo = null, int? PaymentTermsDays = null, string? Subject = null, string? Notes = null, string? Salesperson = null);
public record SettlementRequest(int PartyId, int? InvoiceId, DateOnly Date, int BankAccountId, decimal Amount, string? Narration,
    PaymentMode PaymentMode = PaymentMode.BankTransfer, string? ReferenceNo = null, DateOnly? ChequeDate = null);
public record PurchaseInvoiceRequest(int VendorId, string? VendorRef, DateOnly Date, string? Narration, List<PurchaseLineInput> Lines,
    string? LpoNo = null, string? Notes = null, int? CostCenterId = null);
public record ExpenseRequest(int? VendorId, int? CustomerId, DateOnly Date, int? BankAccountId, string? Reference, string? Narration,
    List<DirectExpenseLineInput> Lines, string? VendorInvoiceNo = null, bool AmountsIncludeVat = false, bool PayLater = false);
public record CreditNoteRequest(int CustomerId, int? SalesInvoiceId, DateOnly Date, string? Reason, string? Narration, List<CreditNoteLineInput> Lines,
    CreditNoteSettlementMethod SettlementMethod = CreditNoteSettlementMethod.CreditOnAccount, int? BankAccountId = null);
public record DebitNoteRequest(int VendorId, int? PurchaseInvoiceId, DateOnly Date, string? Reason, string? Narration, List<DebitNoteLineInput> Lines);
public record VoucherRequest(DateOnly Date, string? Narration, string? Reference, List<VoucherLineInput> Lines, bool Draft = false);
public record RejectRequest(string? Note);
public record CodeNameRequest(string Code, string Name);
public record CurrencyRequest(string Code, string Name, decimal RateToBase);
public record RateRequest(decimal RateToBase);
public record GenerateYearRequest(DateOnly YearStart);
public record ConvertEstimateRequest(DateOnly? Date);
public record EstimateRequest(int CustomerId, DateOnly Date, DateOnly ValidUntil, string? Narration, List<EstimateLineInput> Lines);
public record EstimateStatusRequest(DocumentStatus Status);

internal static class WriteEndpoints
{
    public static string Actor(ClaimsPrincipal u) =>
        u.FindFirstValue(AppUserClaimsPrincipalFactory.DisplayNameClaimType) ?? u.Identity?.Name ?? "unknown";

    /// <summary>Strict period lookup: the period containing the date, open. (C-ERP's GetDefaultPeriodAsync
    /// falls back to the last period, which is fine for a report default but wrong for posting.)</summary>
    public static async Task<int> OpenPeriodFor(LedgerService ledger, DateOnly date)
    {
        var p = (await ledger.GetPeriodsAsync()).FirstOrDefault(p => date >= p.StartDate && date <= p.EndDate)
            ?? throw new PostingException($"No fiscal period covers {date:yyyy-MM-dd}. Create it under Settings → Fiscal Periods.");
        if (p.IsClosed) throw new PostingException($"Fiscal period {p.Name} is closed for posting.");
        return p.Id;
    }

    /// <summary>C-ERP business-rule failures (PostingException) become 400s with their own message.</summary>
    public static async ValueTask<object?> TranslateErrors(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        try { return await next(ctx); }
        catch (PostingException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Not posted"); }
        catch (DbUpdateConcurrencyException) { return Results.Problem("Someone else changed this record — reload and try again.", statusCode: StatusCodes.Status409Conflict); }
    }

    private static RouteGroupBuilder Gate(this RouteGroupBuilder g, Func<CompanyAccess, bool> allowed, string message) =>
        g.AddEndpointFilter(async (ctx, next) => allowed(CompanyAccess.From(ctx.HttpContext)) ? await next(ctx)
            : Results.Problem(message, statusCode: StatusCodes.Status403Forbidden));

    public static void MapWriteEndpoints(this WebApplication app)
    {
        var scoped = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter<CompanyScopeFilter>().AddEndpointFilter(TranslateErrors);

        // ── Lookups for entry forms (read, any role) ─────────────────────────
        scoped.MapGet("/lookups", async (IDbContextFactory<AegisDbContext> dbf, LedgerService ledger) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var accounts = await db.Accounts.AsNoTracking().Where(a => a.IsPostable && a.IsActive).OrderBy(a => a.Code)
                .Select(a => new { a.Id, a.Code, a.Name, a.Type, a.Category }).ToListAsync();
            var cash = await ledger.GetCashBalancesAsync();
            return new
            {
                accounts,
                bankAccounts = cash.Select(c => new { id = c.AccountId, c.Code, c.Name, c.Balance }),
                costCenters = await db.CostCenters.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Code).Select(c => new { c.Id, c.Code, c.Name }).ToListAsync(),
                customers = await db.Customers.AsNoTracking().OrderBy(c => c.Name).Select(c => new { c.Id, c.Code, c.Name, c.PaymentTermsDays }).ToListAsync(),
                vendors = await db.Vendors.AsNoTracking().Where(v => v.IsActive).OrderBy(v => v.Name).Select(v => new { v.Id, v.Code, v.Name }).ToListAsync(),
                items = await db.Items.AsNoTracking().Where(i => i.IsActive).OrderBy(i => i.Code)
                    .Select(i => new { i.Id, i.Code, i.Name, i.Kind, i.Unit, i.SellingPrice, i.CostPrice, i.SalesAccountId, i.PurchaseAccountId, VatRate = i.TaxCode == null ? (decimal?)null : i.TaxCode.Rate }).ToListAsync(),
                taxCodes = await db.TaxCodes.AsNoTracking().Where(t => t.IsActive).Select(t => new { t.Id, t.Code, t.Description, t.Rate }).ToListAsync(),
                openPeriods = await db.FiscalPeriods.AsNoTracking().Where(p => !p.IsClosed).OrderBy(p => p.StartDate).Select(p => new { p.Id, p.Name, p.StartDate, p.EndDate }).ToListAsync(),
            };
        });

        // Everything below changes the books: needs CanPost in the active company.
        var post = scoped.MapGroup("").Gate(a => a.CanPost, "You can view this company's books but not post to them.");

        // ── Sales ─────────────────────────────────────────────────────────────
        post.MapPost("/sales-invoices", async (SalesInvoiceRequest r, SalesInvoiceService svc, LedgerService ledger, ClaimsPrincipal u) =>
        {
            var period = await OpenPeriodFor(ledger, r.Date);
            var inv = r.Draft
                ? await svc.CreateDraftAsync(r.CustomerId, r.Date, period, r.Narration, Actor(u), r.Lines, DateTime.UtcNow, customerPoNo: r.CustomerPoNo, notes: r.Notes, paymentTermsDays: r.PaymentTermsDays, subject: r.Subject, salesperson: r.Salesperson)
                : await svc.CreateAndPostAsync(r.CustomerId, r.Date, period, r.Narration, Actor(u), r.Lines, DateTime.UtcNow, customerPoNo: r.CustomerPoNo, notes: r.Notes, paymentTermsDays: r.PaymentTermsDays, subject: r.Subject, salesperson: r.Salesperson);
            return Results.Created($"/api/sales-invoices/{inv.Id}", new { inv.Id, number = inv.InvoiceNo, inv.Status });
        });
        post.MapPost("/sales-invoices/{id:int}/submit", async (int id, SalesInvoiceService svc, ClaimsPrincipal u) => { await svc.SubmitForApprovalAsync(id, Actor(u), DateTime.UtcNow); return Results.NoContent(); });
        post.MapPost("/sales-invoices/{id:int}/approve", async (int id, SalesInvoiceService svc, ClaimsPrincipal u) => { await svc.ApproveAsync(id, Actor(u), DateTime.UtcNow); return Results.NoContent(); });
        post.MapPost("/sales-invoices/{id:int}/reject", async (int id, RejectRequest r, SalesInvoiceService svc, ClaimsPrincipal u) => { await svc.RejectAsync(id, Actor(u), DateTime.UtcNow, r.Note); return Results.NoContent(); });
        post.MapPost("/sales-invoices/{id:int}/post", async (int id, SalesInvoiceService svc, ClaimsPrincipal u) => { var i = await svc.PostDraftAsync(id, Actor(u), DateTime.UtcNow); return Results.Ok(new { i.Id, number = i.InvoiceNo, i.Status }); });
        post.MapPost("/sales-invoices/{id:int}/void", async (int id, SalesInvoiceService svc, ClaimsPrincipal u) => { await svc.VoidDraftAsync(id, Actor(u), DateTime.UtcNow); return Results.NoContent(); });
        post.MapPost("/sales-invoices/{id:int}/remind", async (int id, SalesInvoiceService svc, ClaimsPrincipal u) => { await svc.SendReminderAsync(id, Actor(u), DateTime.UtcNow, isAutomated: false); return Results.NoContent(); });

        post.MapPost("/receipts", async (SettlementRequest r, ReceiptService svc, LedgerService ledger, ClaimsPrincipal u) =>
        {
            var rv = await svc.CreateAndPostAsync(r.PartyId, r.InvoiceId, r.Date, await OpenPeriodFor(ledger, r.Date), r.BankAccountId, r.Amount, r.Narration, Actor(u), DateTime.UtcNow,
                paymentMode: r.PaymentMode, referenceNo: r.ReferenceNo, chequeDate: r.ChequeDate);
            return Results.Created($"/api/receipts/{rv.Id}", new { rv.Id, number = rv.ReceiptNo });
        });

        post.MapPost("/credit-notes", async (CreditNoteRequest r, CreditNoteService svc, LedgerService ledger, ClaimsPrincipal u) =>
        {
            var cn = await svc.CreateAndPostAsync(r.CustomerId, r.SalesInvoiceId, r.Date, await OpenPeriodFor(ledger, r.Date), r.Reason, r.Narration, Actor(u), r.Lines, DateTime.UtcNow, r.SettlementMethod, r.BankAccountId);
            return Results.Created($"/api/credit-notes/{cn.Id}", new { cn.Id, number = cn.CreditNoteNo });
        });

        // Quotations don't touch the GL, so no fiscal-period check — only conversion to an invoice posts.
        post.MapPost("/estimates", async (EstimateRequest r, EstimateService svc, ClaimsPrincipal u) =>
        {
            var e = await svc.CreateAsync(r.CustomerId, r.Date, r.ValidUntil, r.Narration, Actor(u), r.Lines, DateTime.UtcNow);
            return Results.Created($"/api/estimates/{e.Id}", new { e.Id, number = e.EstimateNo, e.Status });
        });
        post.MapPost("/estimates/{id:int}/status", async (int id, EstimateStatusRequest r, EstimateService svc) =>
        {
            if (r.Status == DocumentStatus.Converted) throw new PostingException("Use Convert to Invoice to convert a quotation.");
            await svc.SetStatusAsync(id, r.Status);
            return Results.NoContent();
        });

        post.MapPost("/estimates/{id:int}/convert", async (int id, ConvertEstimateRequest r, EstimateService svc, LedgerService ledger, ClaimsPrincipal u) =>
        {
            // C-ERP dates the invoice with the quotation's own date, so the period must match that date.
            var est = await svc.GetByIdAsync(id) ?? throw new PostingException("Quotation not found.");
            var inv = await svc.ConvertToInvoiceAsync(id, await OpenPeriodFor(ledger, est.Date), Actor(u), DateTime.UtcNow);
            return Results.Ok(new { inv.Id, number = inv.InvoiceNo, inv.Status });
        });

        // ── Purchases ─────────────────────────────────────────────────────────
        post.MapPost("/purchase-invoices", async (PurchaseInvoiceRequest r, PurchaseInvoiceService svc, LedgerService ledger, ClaimsPrincipal u) =>
        {
            var pi = await svc.CreateAndPostAsync(r.VendorId, r.VendorRef, r.Date, await OpenPeriodFor(ledger, r.Date), r.Narration, Actor(u), r.Lines, DateTime.UtcNow, r.LpoNo, r.Notes, r.CostCenterId);
            return Results.Created($"/api/purchase-invoices/{pi.Id}", new { pi.Id, number = pi.InvoiceNo });
        });

        post.MapPost("/vendor-payments", async (SettlementRequest r, VendorPaymentService svc, LedgerService ledger, ClaimsPrincipal u) =>
        {
            var pv = await svc.CreateAndPostAsync(r.PartyId, r.InvoiceId, r.Date, await OpenPeriodFor(ledger, r.Date), r.BankAccountId, r.Amount, r.Narration, Actor(u), DateTime.UtcNow,
                paymentMode: r.PaymentMode, referenceNo: r.ReferenceNo, chequeDate: r.ChequeDate);
            return Results.Created($"/api/vendor-payments/{pv.Id}", new { pv.Id, number = pv.PaymentNo });
        });

        post.MapPost("/expenses", async (ExpenseRequest r, DirectExpenseService svc, LedgerService ledger, ClaimsPrincipal u) =>
        {
            var ex = await svc.CreateAndPostAsync(r.VendorId, r.CustomerId, r.Date, await OpenPeriodFor(ledger, r.Date), r.BankAccountId, r.Reference, r.Narration, Actor(u), DateTime.UtcNow,
                r.Lines, r.VendorInvoiceNo, r.AmountsIncludeVat, r.PayLater);
            return Results.Created($"/api/expenses/{ex.Id}", new { ex.Id, number = ex.ExpenseNo });
        });

        post.MapPost("/debit-notes", async (DebitNoteRequest r, DebitNoteService svc, LedgerService ledger, ClaimsPrincipal u) =>
        {
            var dn = await svc.CreateAndPostAsync(r.VendorId, r.PurchaseInvoiceId, r.Date, await OpenPeriodFor(ledger, r.Date), r.Reason, r.Narration, Actor(u), r.Lines, DateTime.UtcNow);
            return Results.Created($"/api/debit-notes/{dn.Id}", new { dn.Id, number = dn.DebitNoteNo });
        });

        // ── Journal vouchers (with C-ERP's Draft → Submit → Approve → Post workflow) ──
        post.MapPost("/vouchers", async (VoucherRequest r, JournalService svc, LedgerService ledger, ClaimsPrincipal u) =>
        {
            var period = await OpenPeriodFor(ledger, r.Date);
            var v = r.Draft
                ? await svc.CreateDraftAsync(VoucherType.Journal, r.Date, period, r.Narration, r.Reference, Actor(u), r.Lines, DateTime.UtcNow)
                : await svc.CreateAndPostAsync(VoucherType.Journal, r.Date, period, r.Narration, r.Reference, Actor(u), r.Lines, DateTime.UtcNow);
            return Results.Created($"/api/vouchers/{v.Id}", new { v.Id, number = v.VoucherNo, v.Status });
        });
        post.MapPost("/vouchers/{id:int}/submit", async (int id, JournalService svc, ClaimsPrincipal u) => { await svc.SubmitForApprovalAsync(id, Actor(u), DateTime.UtcNow); return Results.NoContent(); });
        post.MapPost("/vouchers/{id:int}/approve", async (int id, JournalService svc, ClaimsPrincipal u) => { await svc.ApproveAsync(id, Actor(u), DateTime.UtcNow); return Results.NoContent(); });
        post.MapPost("/vouchers/{id:int}/reject", async (int id, RejectRequest r, JournalService svc, ClaimsPrincipal u) => { await svc.RejectAsync(id, Actor(u), DateTime.UtcNow, r.Note); return Results.NoContent(); });
        post.MapPost("/vouchers/{id:int}/post", async (int id, JournalService svc, ClaimsPrincipal u) => { var v = await svc.PostDraftAsync(id, Actor(u), DateTime.UtcNow); return Results.Ok(new { v.Id, number = v.VoucherNo, v.Status }); });
        post.MapPost("/vouchers/{id:int}/void", async (int id, JournalService svc, ClaimsPrincipal u) => { await svc.VoidDraftAsync(id, Actor(u), DateTime.UtcNow); return Results.NoContent(); });

        // ── Masters ───────────────────────────────────────────────────────────
        post.MapPost("/customers", async (NewCustomerInput r, CustomerService svc, ClaimsPrincipal u) =>
        {
            var c = await svc.CreateAsync(r, changedBy: Actor(u), nowUtc: DateTime.UtcNow);
            return Results.Created($"/api/customers/{c.Id}", new { c.Id, c.Code, c.Name });
        });
        post.MapPost("/vendors", async (NewVendorInput r, VendorService svc) =>
        {
            var v = await svc.CreateAsync(r);
            return Results.Created($"/api/vendors/{v.Id}", new { v.Id, v.Code, v.Name });
        });
        post.MapPost("/items", async (NewItemInput r, ItemService svc) =>
        {
            var i = await svc.CreateAsync(r);
            return Results.Created($"/api/items/{i.Id}", new { i.Id, i.Code, i.Name });
        });
        post.MapPost("/leads", async (LeadInput r, LeadService svc, ClaimsPrincipal u) =>
        {
            var l = await svc.CreateAsync(r, Actor(u), DateTime.UtcNow);
            return Results.Created($"/api/leads/{l.Id}", new { l.Id, l.Name });
        });
        post.MapPost("/leads/{id:int}/convert", async (int id, LeadService svc, ClaimsPrincipal u) =>
        {
            var c = await svc.ConvertToCustomerAsync(id, Actor(u), DateTime.UtcNow);
            return Results.Ok(new { c.Id, c.Code, c.Name });
        });
        post.MapPost("/fixed-assets", async (FixedAssetInput r, FixedAssetService svc, ClaimsPrincipal u) =>
        {
            var a = await svc.CreateAsync(r, Actor(u), DateTime.UtcNow);
            return Results.Created($"/api/fixed-assets/{a.Id}", new { a.Id, a.AssetCode, a.Name });
        });

        // ── Settings (company administrators) ─────────────────────────────────
        var admin = scoped.MapGroup("").Gate(a => a.CanAdminister, "Company administrator access is required.");
        admin.MapPost("/accounts", async (NewAccountInput r, ChartOfAccountsService svc, ClaimsPrincipal u) =>
        {
            var a = await svc.CreateAsync(r, Actor(u));
            return Results.Created($"/api/accounts/{a.Id}", new { a.Id, a.Code, a.Name });
        });
        admin.MapPost("/cost-centers", async (CodeNameRequest r, ChartOfAccountsService svc) =>
        {
            var c = await svc.CreateCostCenterAsync(r.Code, r.Name);
            return Results.Created($"/api/cost-centers/{c.Id}", new { c.Id, c.Code, c.Name });
        });
        admin.MapPost("/currencies", async (CurrencyRequest r, CurrencyService svc) =>
        {
            var c = await svc.AddAsync(r.Code, r.Name, r.RateToBase);
            return Results.Created($"/api/currencies/{c.Id}", new { c.Id, c.Code });
        });
        admin.MapPut("/currencies/{id:int}/rate", async (int id, RateRequest r, CurrencyService svc) => { await svc.UpdateRateAsync(id, r.RateToBase); return Results.NoContent(); });
        admin.MapPost("/tax-codes", async (NewTaxCodeInput r, TaxCodeService svc) =>
        {
            var t = await svc.AddAsync(r);
            return Results.Created($"/api/tax-codes/{t.Id}", new { t.Id, t.Code });
        });
        // Adds consecutive monthly periods after the latest one until today is covered (keeps the
        // company's own financial-year numbering, e.g. a July-start year). No periods yet -> calendar year.
        admin.MapPost("/fiscal-periods/extend", async (FiscalPeriodService svc) =>
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var all = await svc.GetAllAsync();
            if (all.Count == 0) return Results.Ok((await svc.GenerateMonthlyYearAsync(new DateOnly(today.Year, 1, 1))).Select(p => p.Name));
            var last = all.OrderBy(p => p.EndDate).Last();
            var created = new List<string>();
            int year = last.Year, no = last.PeriodNo;
            for (var start = last.EndDate.AddDays(1); start <= today; start = start.AddMonths(1))
            {
                if (++no > 12) { no = 1; year++; }
                var end = start.AddMonths(1).AddDays(-1);
                created.Add((await svc.CreateAsync(start.ToString("MMM yyyy"), year, no, start, end)).Name);
            }
            return Results.Ok(created);
        });

        admin.MapPost("/fiscal-periods/generate-year", async (GenerateYearRequest r, FiscalPeriodService svc) =>
            (await svc.GenerateMonthlyYearAsync(r.YearStart)).Select(p => new { p.Id, p.Name }));
        admin.MapPost("/fiscal-periods/{id:int}/close", async (int id, FiscalPeriodService svc) => { await svc.SetClosedAsync(id, true); return Results.NoContent(); });
        admin.MapPost("/fiscal-periods/{id:int}/reopen", async (int id, FiscalPeriodService svc) => { await svc.SetClosedAsync(id, false); return Results.NoContent(); });

        // ── Payroll & fixed-asset runs (Phase 3) ──────────────────────────────
        var payroll = post.MapGroup("").Gate(a => a.CanAccessPayroll, "Payroll access is required.");
        payroll.MapPost("/employees", async (EmployeeInput r, EmployeeService svc, ClaimsPrincipal u) =>
        {
            var e = await svc.CreateAsync(r, Actor(u), DateTime.UtcNow);
            return Results.Created($"/api/employees/{e.Id}", new { e.Id, e.EmployeeCode, e.FullName });
        });
        payroll.MapPost("/payroll-runs", async (PayrollRunRequest r, PayrollService svc, LedgerService ledger, ClaimsPrincipal u) =>
        {
            var run = await svc.CreateDraftRunAsync(await OpenPeriodFor(ledger, r.RunDate), r.RunDate, Actor(u), DateTime.UtcNow);
            return Results.Created($"/api/payroll-runs/{run.Id}", new { run.Id, run.Status, employees = run.Lines.Count });
        });
        payroll.MapPost("/payroll-runs/{id:int}/post", async (int id, PostPayrollRequest r, PayrollService svc, ClaimsPrincipal u) =>
        {
            var v = await svc.PostRunAsync(id, r.DeductionsAccountId, Actor(u), DateTime.UtcNow);
            return Results.Ok(new { voucher = v.VoucherNo });
        });
        payroll.MapPost("/payroll-runs/{id:int}/pay", async (int id, PayPayrollRequest r, PayrollService svc, ClaimsPrincipal u) =>
        {
            var v = await svc.MarkPaidAsync(id, r.BankAccountId, r.PaidDate, Actor(u), DateTime.UtcNow);
            return Results.Ok(new { voucher = v.VoucherNo });
        });
        payroll.MapGet("/payroll-runs/{id:int}/wps", async (int id, WpsFileService svc) =>
        {
            var f = await svc.GenerateSifAsync(id, DateTime.UtcNow);
            return Results.File(Encoding.UTF8.GetBytes(f.Content), "text/plain", f.FileName);
        });

        post.MapPost("/fixed-assets/depreciation", async (DepreciationRequest r, FixedAssetService svc, LedgerService ledger, ClaimsPrincipal u) =>
        {
            var periodId = r.FiscalPeriodId ?? await OpenPeriodFor(ledger, DateOnly.FromDateTime(DateTime.Today));
            var v = await svc.RunDepreciationAsync(periodId, Actor(u), DateTime.UtcNow);
            return Results.Ok(new { voucher = v?.VoucherNo, message = v is null ? "Nothing to depreciate for this period." : null });
        });
    }
}

public record PayrollRunRequest(DateOnly RunDate);
public record PostPayrollRequest(int? DeductionsAccountId);
public record PayPayrollRequest(int BankAccountId, DateOnly PaidDate);
public record DepreciationRequest(int? FiscalPeriodId);
