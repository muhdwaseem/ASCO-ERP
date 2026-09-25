using System.Security.Claims;
using AegisErp.Domain;
using AegisErp.Infrastructure;
using AegisErp.Infrastructure.Identity;
using AegisErp.Infrastructure.Services;

namespace Asco.Api;

/// <summary>What the caller may do in the company named by this request's X-Company-Id.</summary>
public sealed record CompanyAccess(CompanyAccessRow Row, bool IsFirmAdmin)
{
    public bool CanPost => IsFirmAdmin || Row.Role is AppRoles.Admin or AppRoles.Accountant;
    public bool CanAdminister => IsFirmAdmin || Row.Role == AppRoles.Admin;
    public bool CanAccessPayroll => IsFirmAdmin || Row.Role == AppRoles.Admin || (Row.Role == AppRoles.Accountant && Row.CanAccessPayroll);

    public static CompanyAccess From(HttpContext ctx) => (CompanyAccess)ctx.Items[Key]!;
    internal const string Key = "asco.access";
}

/// <summary>
/// Stateless replacement for C-ERP's per-circuit CompanySession: every request names its company
/// (X-Company-Id), the grant is re-checked against UserCompanyAccess, and C-ERP's scoped
/// <see cref="CurrentCompany"/> is set — which is what its DbContext global query filters read.
/// So isolation is enforced by the same code as C-ERP, and no server holds per-user state.
/// </summary>
public sealed class CompanyScopeFilter(CurrentCompany current, CompanyAccessService access) : IEndpointFilter
{
    public const string Header = "X-Company-Id";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
        if (!int.TryParse(http.Request.Headers[Header], out var companyId))
            return Results.Problem($"{Header} header is required.", statusCode: StatusCodes.Status400BadRequest);

        var isFirmAdmin = http.User.IsInRole(AppRoles.FirmAdmin);
        var row = (await access.GetCompaniesForUserAsync(userId, isFirmAdmin)).FirstOrDefault(c => c.CompanyId == companyId);
        if (row is null) return Results.Problem("You do not have access to this company.", statusCode: StatusCodes.Status403Forbidden);

        var status = SubscriptionStatusCalculator.Compute(row.SubscriptionEnabled, row.SubscriptionPaidThroughDate,
            row.SubscriptionGraceDays, DateOnly.FromDateTime(DateTime.Today));
        if (!isFirmAdmin && status == SubscriptionStatus.Suspended)
            return Results.Problem("This company's subscription is suspended. Contact your firm administrator.", statusCode: StatusCodes.Status403Forbidden);

        var grant = new CompanyAccess(row, isFirmAdmin);
        current.CompanyId = companyId;
        current.IsFirmAdmin = isFirmAdmin;
        current.CanPost = grant.CanPost;
        current.CanAdminister = grant.CanAdminister;
        http.Items[CompanyAccess.Key] = grant;
        return await next(ctx);
    }
}

public static class ScopeExtensions
{
    /// <summary>HR &amp; payroll data needs the separate payroll grant (same rule as C-ERP).</summary>
    public static RouteHandlerBuilder RequirePayroll(this RouteHandlerBuilder b) =>
        b.AddEndpointFilter(async (ctx, next) =>
            CompanyAccess.From(ctx.HttpContext).CanAccessPayroll ? await next(ctx)
                : Results.Problem("Payroll access is required.", statusCode: StatusCodes.Status403Forbidden));

    public static RouteHandlerBuilder RequireAdminister(this RouteHandlerBuilder b) =>
        b.AddEndpointFilter(async (ctx, next) =>
            CompanyAccess.From(ctx.HttpContext).CanAdminister ? await next(ctx)
                : Results.Problem("Company administrator access is required.", statusCode: StatusCodes.Status403Forbidden));
}
