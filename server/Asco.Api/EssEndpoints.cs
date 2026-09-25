using System.Security.Claims;
using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure;
using AegisErp.Infrastructure.Services;

namespace Asco.Api;

public record LeaveRequestInput(LeaveType Type, DateOnly StartDate, DateOnly EndDate, string? Reason);
public record LeaveDecision(bool Approved);
public record PortalAccessRequest(string Email, string Password);

/// <summary>
/// Employee Self-Service — mirrors C-ERP's EmployeePortalSession: the signed-in user is resolved to
/// their Employee record, the request is scoped to that employee's company, and every posting /
/// admin flag is forced off. An employee only ever sees their own payslips, leave and advances.
/// </summary>
internal sealed class EssScopeFilter(CurrentCompany current, EmployeeService employees) : IEndpointFilter
{
    public const string Key = "asco.employee";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var userId = ctx.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
        var employee = await employees.GetByUserIdAsync(userId);
        if (employee is null) return Results.Problem("This login is not linked to an employee record.", statusCode: StatusCodes.Status403Forbidden);
        current.CompanyId = employee.CompanyId;
        current.CanPost = false;
        current.CanAdminister = false;
        current.IsFirmAdmin = false;
        ctx.HttpContext.Items[Key] = employee;
        return await next(ctx);
    }

    public static Employee Me(HttpContext ctx) => (Employee)ctx.Items[Key]!;
}

internal static class EssEndpoints
{
    public static void MapEssEndpoints(this WebApplication app)
    {
        var ess = app.MapGroup("/api/ess").RequireAuthorization().AddEndpointFilter<EssScopeFilter>().AddEndpointFilter(WriteEndpoints.TranslateErrors);

        ess.MapGet("/profile", (HttpContext ctx) =>
        {
            var e = EssScopeFilter.Me(ctx);
            return new
            {
                e.EmployeeCode, e.FullName, e.Designation, e.JoiningDate, e.Email, e.Mobile, e.BankName, e.Iban,
                e.BasicSalary, e.HousingAllowance, e.TransportAllowance, e.OtherAllowance, e.GrossSalary,
                e.VisaExpiryDate, e.EmiratesIdExpiryDate, e.PassportExpiryDate, e.LabourCardExpiryDate,
            };
        });

        ess.MapGet("/payslips", async (HttpContext ctx, PayrollService payroll) =>
            (await payroll.GetPayslipLinesForEmployeeAsync(EssScopeFilter.Me(ctx).Id)).Select(l => new
            {
                runId = l.PayrollRunId, period = l.PayrollRun.FiscalPeriod.Name, l.PayrollRun.RunDate, l.PayrollRun.IsPaid,
                l.BasicSalary, l.HousingAllowance, l.TransportAllowance, l.OtherAllowance, l.Deductions, l.SalaryAdvanceDeduction, l.GrossPay, l.NetPay,
            }));

        ess.MapGet("/leave", async (HttpContext ctx, LeaveService leave) =>
        {
            var id = EssScopeFilter.Me(ctx).Id;
            return new
            {
                balance = await leave.GetBalanceAsync(id, DateOnly.FromDateTime(DateTime.Today)),
                requests = (await leave.GetAllAsync(id)).Select(r => new { r.Id, r.Type, r.StartDate, r.EndDate, r.Days, r.Reason, r.Status, r.DecisionBy }),
            };
        });

        ess.MapPost("/leave", async (HttpContext ctx, LeaveRequestInput r, LeaveService leave) =>
        {
            var e = EssScopeFilter.Me(ctx);
            var req = await leave.CreateRequestAsync(e.Id, r.Type, r.StartDate, r.EndDate, r.Reason, e.FullName, DateTime.UtcNow);
            return Results.Created($"/api/ess/leave/{req.Id}", new { req.Id, req.Days, req.Status });
        });

        ess.MapGet("/advances", async (HttpContext ctx, SalaryAdvanceService adv) =>
            (await adv.GetForEmployeeAsync(EssScopeFilter.Me(ctx).Id)).Select(a => new { a.Id, a.IssueDate, a.Amount, a.MonthlyDeductionAmount, a.RemainingBalance, a.Status, a.Reason }));

        // ── Staff side: leave approvals, portal access, remaining settings reads ──
        var scoped = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter<CompanyScopeFilter>().AddEndpointFilter(WriteEndpoints.TranslateErrors);

        scoped.MapGet("/leave-requests", async (LeaveService leave) =>
            (await leave.GetAllAsync()).Select(r => new { r.Id, employee = r.Employee?.FullName, r.Type, r.StartDate, r.EndDate, r.Days, r.Reason, r.Status, r.DecisionBy }))
            .RequirePayroll();

        scoped.MapPost("/leave-requests/{id:int}/decide", async (int id, LeaveDecision d, LeaveService leave, ClaimsPrincipal u) =>
        {
            await leave.DecideAsync(id, d.Approved, WriteEndpoints.Actor(u), DateTime.UtcNow);
            return Results.NoContent();
        }).RequirePayroll();

        scoped.MapPost("/employees/{id:int}/portal-access", async (int id, PortalAccessRequest r, EmployeeService employees) =>
        {
            var res = await employees.GrantPortalAccessAsync(id, r.Email, r.Password);
            return Results.Ok(res);
        }).RequirePayroll().RequireAdminister();

        scoped.MapGet("/commission-config", async (CommissionConfigService cfg) => new
        {
            categoryRates = await cfg.GetCategoryRatesAsync(),
            profitSlabs = await cfg.GetProfitSlabsAsync(),
            positionRates = await cfg.GetPositionRatesAsync(),
        });

        scoped.MapGet("/custom-fields", async (CustomFieldService fields) =>
        {
            var all = new List<object>();
            foreach (var module in new[] { "Customer", "Vendor" })
                all.AddRange((await fields.GetDefinitionsAsync(module)).Select(f => (object)new { module, f.Id, f.Label, f.FieldType, f.DropdownOptionsCsv, f.IsRequired, f.IsActive }));
            return all;
        });
    }
}
