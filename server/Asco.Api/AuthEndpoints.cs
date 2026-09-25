using System.Security.Claims;
using AegisErp.Domain;
using AegisErp.Infrastructure.Identity;
using AegisErp.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;

namespace Asco.Api;

public record LoginRequest(string Email, string Password);

internal static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapPost("/login", async (LoginRequest req, SignInManager<AppUser> signIn, UserManager<AppUser> users) =>
        {
            var user = await users.FindByEmailAsync(req.Email ?? "");
            if (user is null) return Results.Problem("Invalid email or password.", statusCode: 401);
            // Employee-only logins are allowed: /me reports them as employees and the UI opens the
            // self-service portal; they hold no company grant, so every /api data endpoint refuses them.
            var result = await signIn.PasswordSignInAsync(user, req.Password ?? "", isPersistent: false, lockoutOnFailure: true);
            if (result.IsLockedOut) return Results.Problem("Too many failed attempts — try again in 5 minutes.", statusCode: 423);
            if (!result.Succeeded) return Results.Problem("Invalid email or password.", statusCode: 401);
            return Results.NoContent();
        }).RequireRateLimiting("login");

        auth.MapPost("/logout", async (SignInManager<AppUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.NoContent();
        });

        auth.MapGet("/me", async (ClaimsPrincipal user, CompanyAccessService access, EmployeeService employees) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var isFirmAdmin = user.IsInRole(AppRoles.FirmAdmin);
            var today = DateOnly.FromDateTime(DateTime.Today);
            var companies = (await access.GetCompaniesForUserAsync(userId, isFirmAdmin)).Select(c =>
            {
                var g = new CompanyAccess(c, isFirmAdmin);
                return new
                {
                    id = c.CompanyId, code = c.Code, name = c.Name, role = isFirmAdmin ? AppRoles.FirmAdmin : c.Role,
                    canPost = g.CanPost, canAdminister = g.CanAdminister, canAccessPayroll = g.CanAccessPayroll,
                    subscription = SubscriptionStatusCalculator.Compute(c.SubscriptionEnabled, c.SubscriptionPaidThroughDate, c.SubscriptionGraceDays, today),
                };
            });
            return Results.Ok(new
            {
                email = user.FindFirstValue(ClaimTypes.Name),
                displayName = user.FindFirstValue(AppUserClaimsPrincipalFactory.DisplayNameClaimType) ?? user.FindFirstValue(ClaimTypes.Name),
                isFirmAdmin,
                companies,
                employee = user.IsInRole(AppRoles.Employee) && await employees.GetByUserIdAsync(userId) is { } e
                    ? new { e.EmployeeCode, e.FullName } : null,
            });
        }).RequireAuthorization();
    }
}
