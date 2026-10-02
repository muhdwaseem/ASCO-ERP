using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace AegisErp.Infrastructure.Identity;

/// <summary>Application user. DisplayName is what appears on vouchers as CreatedBy.</summary>
public class AppUser : IdentityUser
{
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>
/// Adds DisplayName onto the signed-in cookie as its own claim, so the app bar can show "Fatima
/// Al Rashidi" instead of her login email without a per-page database round trip — Identity's
/// default factory only ever puts the Name (=email/username), Role and security-stamp claims on
/// the principal.
/// </summary>
public class AppUserClaimsPrincipalFactory : UserClaimsPrincipalFactory<AppUser, IdentityRole>
{
    public const string DisplayNameClaimType = "DisplayName";

    public AppUserClaimsPrincipalFactory(
        UserManager<AppUser> userManager, RoleManager<IdentityRole> roleManager, IOptions<IdentityOptions> options)
        : base(userManager, roleManager, options) { }

    protected override async Task<System.Security.Claims.ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
            identity.AddClaim(new System.Security.Claims.Claim(DisplayNameClaimType, user.DisplayName));
        return identity;
    }
}

/// <summary>Role names used across the app. Admin ⊃ Accountant ⊃ Viewer in practice.</summary>
public static class AppRoles
{
    /// <summary>
    /// Firm-level administrator: spans every company, creates companies and assigns staff to them.
    /// This is the only role that is genuinely global — the others are held per company.
    /// </summary>
    public const string FirmAdmin = "FirmAdmin";

    public const string Admin = "Admin";
    public const string Accountant = "Accountant";
    public const string Viewer = "Viewer";

    /// <summary>Employee Self-Service login — deliberately not a <see cref="CompanyRoles"/> member.
    /// It carries no accounting access at all; see <c>EmployeePortalSession</c>, which resolves the
    /// single <c>Employee</c> row this account is linked to instead of a company/role grant.</summary>
    public const string Employee = "Employee";

    /// <summary>Roles allowed to post documents to the ledger.</summary>
    public const string Posters = FirmAdmin + "," + Admin + "," + Accountant;

    /// <summary>Roles allowed to administer a company's settings and users.</summary>
    public const string Admins = FirmAdmin + "," + Admin;

    /// <summary>Every role that can be granted on a specific company.</summary>
    public static readonly string[] CompanyRoles = { Admin, Accountant, Viewer };
}
