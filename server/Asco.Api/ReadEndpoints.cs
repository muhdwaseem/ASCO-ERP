using AegisErp.Infrastructure;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Asco.Api;

/// <summary>
/// Phase 1 — read endpoints for every ASCO sheet. Two patterns:
///  • plain lists: EF projections straight off C-ERP's DbContext (company-filtered by its query filters);
///  • computed views (status, outstanding, aging, TB, P&amp;L, BS, cash flow…): C-ERP's own services,
///    so ASCO shows exactly the numbers C-ERP shows.
/// Everything is mapped to flat DTOs — never raw entity graphs.
/// </summary>
internal static class ReadEndpoints
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
    private static DateOnly YearStart => new(Today.Year, 1, 1);
    private const int DefaultTake = 500;

    public static void MapReadEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter<CompanyScopeFilter>();

        // ── Dashboard ─────────────────────────────────────────────────────────
        api.MapGet("/dashboard", async (LedgerService ledger) =>
        {
            var period = await ledger.GetDefaultPeriodAsync(Today);
            if (period is null) return Results.Problem("No fiscal period covers today — create fiscal periods first.", statusCode: 409);
            return Results.Ok(new { kpis = await ledger.GetDashboardAsync(period.Id), cash = await ledger.GetCashBalancesAsync() });
        });

        // ── Finance ───────────────────────────────────────────────────────────
        api.MapGet("/accounts", async (IDbContextFactory<AegisDbContext> dbf, LedgerService ledger, DateOnly? asOf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var accounts = await db.Accounts.AsNoTracking()
                .Select(a => new { a.Id, a.Code, a.Name, a.Type, a.IsPostable, ParentCode = a.Parent == null ? null : a.Parent.Code, a.Category, a.PnlSection, a.Currency, a.IsActive })
                .ToListAsync();
            var tb = await ledger.GetTrialBalanceAsync(DateOnly.MinValue, asOf ?? Today);
            var bal = tb.Rows.ToDictionary(r => r.AccountId, r => r.ClosingDebit - r.ClosingCredit);
            return accounts.OrderBy(a => a.Code, StringComparer.Ordinal)
                .Select(a => new { a.Id, a.Code, a.Name, a.Type, a.IsPostable, a.ParentCode, a.Category, a.PnlSection, a.Currency, a.IsActive, Balance = bal.GetValueOrDefault(a.Id) });
        });

        api.MapGet("/opening-balances", (ChartOfAccountsService coa) => coa.GetOpeningBalancesAsync());

        api.MapGet("/general-ledger", (LedgerService ledger, DateOnly? from, DateOnly? to, int? accountId) =>
            ledger.GetGeneralLedgerAsync(from ?? YearStart, to ?? Today, accountId));

        api.MapGet("/vouchers", async (IDbContextFactory<AegisDbContext> dbf, int? take) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var list = await db.JournalVouchers.AsNoTracking().Include(v => v.Lines)
                .OrderByDescending(v => v.Date).ThenByDescending(v => v.Id).Take(take ?? DefaultTake).ToListAsync();
            return list.Select(v => new { v.Id, v.VoucherNo, v.Type, v.Status, v.ApprovalStatus, v.Date, v.Narration, v.Reference, Lines = v.Lines.Count, Amount = v.TotalDebit, v.CreatedBy, v.PostedBy });
        });

        // ── Receivables ──────────────────────────────────────────────────────
        api.MapGet("/customers", async (IDbContextFactory<AegisDbContext> dbf, CustomerService customers) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var rows = await db.Customers.AsNoTracking().OrderBy(c => c.Code)
                .Select(c => new { c.Id, c.Code, c.Name, c.Trn, c.Email, c.Mobile, c.Group, c.Currency, c.CreditLimit, c.PaymentTermsDays, c.Salesperson })
                .ToListAsync();
            var sums = (await customers.GetSummariesAsync()).ToDictionary(s => s.Id);
            return rows.Select(c =>
            {
                var s = sums.GetValueOrDefault(c.Id);
                return new { c.Id, c.Code, c.Name, c.Trn, c.Email, c.Mobile, c.Group, c.Currency, c.CreditLimit, c.PaymentTermsDays, c.Salesperson, Invoiced = s?.Invoiced ?? 0, Received = s?.Received ?? 0, Outstanding = s?.Outstanding ?? 0 };
            });
        });

        api.MapGet("/customers/{id:int}/statement", (int id, CustomerService customers) => customers.GetStatementAsync(id));

        api.MapGet("/agents", async (IDbContextFactory<AegisDbContext> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            return await db.Agents.AsNoTracking().OrderBy(a => a.AgentCode)
                .Select(a => new { a.Id, a.AgentCode, a.Name, a.Phone, a.Email, a.Status }).ToListAsync();
        });

        api.MapGet("/sales-invoices", async (SalesInvoiceService invoices) =>
            (await invoices.GetAllAsync()).Select(r => new
            {
                r.Invoice.Id, r.Invoice.InvoiceNo, r.Invoice.Date, r.Invoice.DueDate,
                CustomerCode = r.Invoice.Customer.Code, CustomerName = r.Invoice.Customer.Name,
                Net = r.Invoice.TotalNet, Vat = r.Invoice.TotalVat, Gross = r.Invoice.TotalGross,
                r.Balance, r.Status, r.Invoice.Salesperson, r.Invoice.ApprovalStatus,
            }));

        api.MapGet("/estimates", async (IDbContextFactory<AegisDbContext> dbf, int? take) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var list = await db.Estimates.AsNoTracking().Include(e => e.Customer).Include(e => e.Lines)
                .OrderByDescending(e => e.Date).ThenByDescending(e => e.Id).Take(take ?? DefaultTake).ToListAsync();
            return list.Select(e => new { e.Id, e.EstimateNo, e.Date, e.ValidUntil, CustomerName = e.Customer.Name, e.Status, Net = e.TotalNet, Vat = e.TotalVat, Gross = e.TotalGross, Converted = e.ConvertedInvoiceId != null });
        });

        api.MapGet("/delivery-notes", async (IDbContextFactory<AegisDbContext> dbf, int? take) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var list = await db.DeliveryNotes.AsNoTracking().Include(d => d.Customer).Include(d => d.SalesInvoice).Include(d => d.Lines)
                .OrderByDescending(d => d.Date).ThenByDescending(d => d.Id).Take(take ?? DefaultTake).ToListAsync();
            return list.Select(d => new { d.Id, d.DeliveryNoteNo, d.Date, CustomerName = d.Customer.Name, InvoiceNo = d.SalesInvoice == null ? null : d.SalesInvoice.InvoiceNo, d.DeliveryAddress, d.Status, Quantity = d.TotalQuantity });
        });

        api.MapGet("/recurring-invoices", async (IDbContextFactory<AegisDbContext> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var list = await db.RecurringInvoiceProfiles.AsNoTracking().Include(p => p.Customer).Include(p => p.Lines).ToListAsync();
            return list.OrderBy(p => p.NextGenerationDate).Select(p => new
            {
                p.Id, CustomerName = p.Customer.Name, p.Frequency, p.RepeatEvery, p.StartDate, p.EndDate, p.NextGenerationDate, p.IsActive, p.Narration,
                Net = p.Lines.Sum(l => l.Quantity * l.UnitPrice),
            });
        });

        api.MapGet("/receipts", async (IDbContextFactory<AegisDbContext> dbf, int? take) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var list = await db.CustomerReceipts.AsNoTracking().Include(r => r.Customer).Include(r => r.BankAccount).Include(r => r.SalesInvoice)
                .OrderByDescending(r => r.Date).ThenByDescending(r => r.Id).Take(take ?? DefaultTake).ToListAsync();
            return list.Select(r => new { r.Id, r.ReceiptNo, r.Date, CustomerName = r.Customer.Name, InvoiceNo = r.SalesInvoice == null ? null : r.SalesInvoice.InvoiceNo, r.PaymentMode, r.ReferenceNo, BankAccount = r.BankAccount.Name, r.Amount, r.Status });
        });

        api.MapGet("/credit-notes", async (IDbContextFactory<AegisDbContext> dbf, int? take) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var list = await db.CreditNotes.AsNoTracking().Include(c => c.Customer).Include(c => c.SalesInvoice).Include(c => c.Lines)
                .OrderByDescending(c => c.Date).ThenByDescending(c => c.Id).Take(take ?? DefaultTake).ToListAsync();
            return list.Select(c => new { c.Id, c.CreditNoteNo, c.Date, CustomerName = c.Customer.Name, InvoiceNo = c.SalesInvoice == null ? null : c.SalesInvoice.InvoiceNo, c.Reason, c.SettlementMethod, Net = c.TotalNet, Vat = c.TotalVat, Gross = c.TotalGross, c.Status });
        });

        api.MapGet("/ar-aging", (CustomerService customers, DateOnly? asOf) => customers.GetAgingAsync(asOf ?? Today));
        api.MapGet("/outstanding-invoices", (CustomerService customers, DateOnly? asOf) => customers.GetOutstandingInvoicesAsync(asOf ?? Today));
        api.MapGet("/transactions", (TransactionService tx) => tx.GetAllAsync());

        // ── Payables ─────────────────────────────────────────────────────────
        api.MapGet("/vendors", async (IDbContextFactory<AegisDbContext> dbf, VendorService vendors) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var rows = await db.Vendors.AsNoTracking().OrderBy(v => v.Code)
                .Select(v => new { v.Id, v.Code, v.Name, v.Trn, v.Email, v.Mobile, v.Group, v.Currency, v.PaymentTermsDays, v.IsActive })
                .ToListAsync();
            var sums = (await vendors.GetSummariesAsync()).ToDictionary(s => s.Id);
            return rows.Select(v =>
            {
                var s = sums.GetValueOrDefault(v.Id);
                return new { v.Id, v.Code, v.Name, v.Trn, v.Email, v.Mobile, v.Group, v.Currency, v.PaymentTermsDays, v.IsActive, Billed = s?.Billed ?? 0, Paid = s?.Paid ?? 0, Outstanding = s?.Outstanding ?? 0 };
            });
        });

        api.MapGet("/vendors/{id:int}/statement", (int id, VendorService vendors) => vendors.GetStatementAsync(id));
        api.MapGet("/purchase-invoices", (VendorService vendors) => vendors.GetAllWithStatusAsync());

        api.MapGet("/vendor-payments", async (IDbContextFactory<AegisDbContext> dbf, int? take) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var list = await db.VendorPayments.AsNoTracking().Include(p => p.Vendor).Include(p => p.BankAccount).Include(p => p.PurchaseInvoice)
                .OrderByDescending(p => p.Date).ThenByDescending(p => p.Id).Take(take ?? DefaultTake).ToListAsync();
            return list.Select(p => new { p.Id, p.PaymentNo, p.Date, VendorName = p.Vendor.Name, InvoiceNo = p.PurchaseInvoice == null ? null : p.PurchaseInvoice.InvoiceNo, p.PaymentMode, p.ReferenceNo, BankAccount = p.BankAccount.Name, p.Amount, p.Status });
        });

        api.MapGet("/debit-notes", async (IDbContextFactory<AegisDbContext> dbf, int? take) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var list = await db.DebitNotes.AsNoTracking().Include(d => d.Vendor).Include(d => d.PurchaseInvoice).Include(d => d.Lines)
                .OrderByDescending(d => d.Date).ThenByDescending(d => d.Id).Take(take ?? DefaultTake).ToListAsync();
            return list.Select(d => new { d.Id, d.DebitNoteNo, d.Date, VendorName = d.Vendor.Name, InvoiceNo = d.PurchaseInvoice == null ? null : d.PurchaseInvoice.InvoiceNo, d.Reason, Net = d.TotalNet, Vat = d.TotalVat, Gross = d.TotalGross, d.Status });
        });

        api.MapGet("/expenses", async (IDbContextFactory<AegisDbContext> dbf, int? take) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var list = await db.DirectExpenses.AsNoTracking().Include(e => e.Vendor).Include(e => e.BankAccount).Include(e => e.Lines)
                .OrderByDescending(e => e.Date).ThenByDescending(e => e.Id).Take(take ?? DefaultTake).ToListAsync();
            return list.Select(e => new { e.Id, e.ExpenseNo, e.Date, VendorName = e.Vendor == null ? null : e.Vendor.Name, e.Narration, PaidFrom = e.BankAccount == null ? null : e.BankAccount.Name, e.IsPayLater, Net = e.TotalNet, Vat = e.TotalVat, Gross = e.TotalAmount, e.Status });
        });

        api.MapGet("/ap-aging", (VendorService vendors, DateOnly? asOf) => vendors.GetAgingAsync(asOf ?? Today));
        api.MapGet("/expense-transactions", (ExpenseTransactionService tx) => tx.GetAllAsync());

        // ── Items ────────────────────────────────────────────────────────────
        api.MapGet("/items", async (IDbContextFactory<AegisDbContext> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            return await db.Items.AsNoTracking().OrderBy(i => i.Code).Select(i => new
            {
                i.Id, i.Code, i.Name, i.Kind, i.Unit, Category = i.Category == null ? null : i.Category.Name, i.SellingPrice, i.CostPrice,
                SalesAccount = i.SalesAccount == null ? null : i.SalesAccount.Code + " " + i.SalesAccount.Name,
                TaxCode = i.TaxCode == null ? null : i.TaxCode.Code, i.IsActive,
            }).ToListAsync();
        });

        api.MapGet("/units", async (IDbContextFactory<AegisDbContext> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            return await db.UnitsOfMeasure.AsNoTracking().OrderBy(u => u.Name).Select(u => new { u.Id, u.Name, u.IsActive }).ToListAsync();
        });

        api.MapGet("/item-categories", async (IDbContextFactory<AegisDbContext> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            return await db.ItemCategories.AsNoTracking().OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name, c.IsActive, Items = db.Items.Count(i => i.CategoryId == c.Id) }).ToListAsync();
        });

        api.MapGet("/item-kits", async (IDbContextFactory<AegisDbContext> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var kits = await db.ItemKits.AsNoTracking().Include(k => k.Lines).ThenInclude(l => l.Item).OrderBy(k => k.Code).ToListAsync();
            return kits.Select(k => new
            {
                k.Id, k.Code, k.Name, k.IsActive,
                Lines = k.Lines.OrderBy(l => l.SortOrder).Select(l => new { ItemCode = l.Item.Code, ItemName = l.Item.Name, l.Quantity, l.Item.SellingPrice, Amount = l.Quantity * l.Item.SellingPrice }),
            });
        });

        // ── CRM & Fixed assets ───────────────────────────────────────────────
        api.MapGet("/leads", async (IDbContextFactory<AegisDbContext> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            return await db.Leads.AsNoTracking().OrderByDescending(l => l.CreatedAtUtc)
                .Select(l => new { l.Id, l.Name, l.CompanyName, l.Mobile, l.Email, l.Source, l.Stage, l.EstimatedValue, l.AssignedTo, l.LastActivityAtUtc, Converted = l.ConvertedCustomerId != null })
                .ToListAsync();
        });

        api.MapGet("/fixed-assets", async (IDbContextFactory<AegisDbContext> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var list = await db.FixedAssets.AsNoTracking().Include(a => a.DepreciationEntries).OrderBy(a => a.AssetCode).ToListAsync();
            return list.Select(a => new { a.Id, a.AssetCode, a.Name, a.Category, a.PurchaseDate, a.PurchaseCost, a.SalvageValue, a.UsefulLifeMonths, a.AccumulatedDepreciation, a.NetBookValue, a.Status, a.DisposalDate });
        });

        // ── HR & Payroll (payroll grant required) ────────────────────────────
        api.MapGet("/employees", async (IDbContextFactory<AegisDbContext> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var list = await db.Employees.AsNoTracking().Include(e => e.CostCenter).OrderBy(e => e.EmployeeCode).ToListAsync();
            return list.Select(e => new
            {
                e.Id, e.EmployeeCode, e.FullName, e.Designation, CostCenter = e.CostCenter == null ? null : e.CostCenter.Code, e.JoiningDate, e.Status,
                e.BasicSalary, Allowances = e.HousingAllowance + e.TransportAllowance + e.OtherAllowance, e.GrossSalary,
                e.VisaExpiryDate, e.EmiratesIdExpiryDate, e.PassportExpiryDate, e.GratuityEligible,
            });
        }).RequirePayroll();

        api.MapGet("/payroll-runs", async (IDbContextFactory<AegisDbContext> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var list = await db.PayrollRuns.AsNoTracking().Include(r => r.FiscalPeriod).Include(r => r.Lines).OrderByDescending(r => r.RunDate).ToListAsync();
            return list.Select(r => new { r.Id, Period = r.FiscalPeriod.Name, r.RunDate, r.Status, Employees = r.Lines.Count, r.TotalGross, r.TotalDeductions, r.TotalNet, r.IsPaid, r.PaidDate });
        }).RequirePayroll();

        api.MapGet("/expiring-documents", (EmployeeService employees, int? withinDays) => employees.GetExpiringDocumentsAsync(withinDays ?? 90)).RequirePayroll();

        // ── Reports ──────────────────────────────────────────────────────────
        var reports = api.MapGroup("/reports");
        reports.MapGet("/trial-balance", (LedgerService ledger, DateOnly? from, DateOnly? to) => ledger.GetTrialBalanceAsync(from ?? YearStart, to ?? Today));
        reports.MapGet("/profit-and-loss", (LedgerService ledger, DateOnly? from, DateOnly? to) => ledger.GetProfitAndLossAsync(from ?? YearStart, to ?? Today));
        reports.MapGet("/balance-sheet", (LedgerService ledger, DateOnly? asOf) => ledger.GetBalanceSheetAsync(asOf ?? Today));
        reports.MapGet("/cash-flow", async (LedgerService ledger, ReportsService rpt, int? periodId) =>
        {
            var id = periodId ?? (await ledger.GetDefaultPeriodAsync(Today))?.Id;
            return id is int p ? Results.Ok(await rpt.GetCashFlowAsync(p)) : Results.Problem("No fiscal period covers today.", statusCode: 409);
        });
        reports.MapGet("/segments", (ReportsService rpt, DateOnly? from, DateOnly? to) => rpt.GetSegmentPnlAsync(from ?? YearStart, to ?? Today));
        reports.MapGet("/customer-revenue", (ReportsService rpt, DateOnly? from, DateOnly? to) => rpt.GetCustomerRevenueAsync(from ?? YearStart, to ?? Today));
        reports.MapGet("/vendor-spend", (ReportsService rpt, DateOnly? from, DateOnly? to) => rpt.GetVendorSpendAsync(from ?? YearStart, to ?? Today));
        reports.MapGet("/commission", (CommissionService c, DateOnly? from, DateOnly? to) => c.GetCommissionReportAsync(from ?? YearStart, to ?? Today));
        reports.MapGet("/reassignment-audit", (CommissionService c) => c.GetReassignmentAuditLogAsync());
        reports.MapGet("/vat-control", (TaxCodeService tax) => tax.GetControlAccountStatusAsync());

        // ── Settings ─────────────────────────────────────────────────────────
        api.MapGet("/fiscal-periods", async (IDbContextFactory<AegisDbContext> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            return await db.FiscalPeriods.AsNoTracking().OrderBy(p => p.Year).ThenBy(p => p.PeriodNo)
                .Select(p => new { p.Id, p.Name, p.Year, p.PeriodNo, p.StartDate, p.EndDate, p.IsClosed }).ToListAsync();
        });

        api.MapGet("/cost-centers", async (IDbContextFactory<AegisDbContext> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            return await db.CostCenters.AsNoTracking().OrderBy(c => c.Code).Select(c => new { c.Id, c.Code, c.Name, c.IsActive }).ToListAsync();
        });

        api.MapGet("/currencies", async (IDbContextFactory<AegisDbContext> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            return await db.Currencies.AsNoTracking().OrderBy(c => c.Code).Select(c => new { c.Id, c.Code, c.Name, c.RateToBase, c.IsActive, c.UpdatedAtUtc }).ToListAsync();
        });

        api.MapGet("/tax-codes", async (IDbContextFactory<AegisDbContext> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            return await db.TaxCodes.AsNoTracking().OrderBy(t => t.Code).Select(t => new
            {
                t.Id, t.Code, t.Description, t.Rate, t.TaxType, t.EffectiveFrom, t.IsActive,
                OutputAccount = t.OutputAccount == null ? null : t.OutputAccount.Code + " " + t.OutputAccount.Name,
                InputAccount = t.InputAccount == null ? null : t.InputAccount.Code + " " + t.InputAccount.Name,
            }).ToListAsync();
        });

        api.MapGet("/service-kits", async (IDbContextFactory<AegisDbContext> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var kits = await db.ServiceKits.AsNoTracking().Include(k => k.Lines).OrderBy(k => k.Name).ToListAsync();
            return kits.Select(k => new { k.Id, k.Name, k.IsActive, Lines = k.Lines.OrderBy(l => l.SortOrder).Select(l => new { l.Description, l.GovtFee, l.CenterFee, l.BankCharge, l.VatRate }) });
        });

        api.MapGet("/team", (HttpContext ctx, CompanyAccessService access) =>
            access.GetMembersAsync(CompanyAccess.From(ctx).Row.CompanyId)).RequireAdminister();
    }
}
