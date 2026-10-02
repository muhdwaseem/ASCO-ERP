using AegisErp.Domain.Entities;

namespace AegisErp.Infrastructure.Identity;

/// <summary>
/// Grants one user access to one company, with the role they hold *in that company*. A user can
/// be an Accountant for one client and a Viewer (or absent entirely) for another — which is how
/// staff are restricted to the engagements they are assigned to.
/// </summary>
public class UserCompanyAccess
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;
    public AppUser User { get; set; } = null!;

    public int CompanyId { get; set; }
    public CompanySetup Company { get; set; } = null!;

    /// <summary>Role held in this company: Admin, Accountant or Viewer.</summary>
    public string Role { get; set; } = AppRoles.Viewer;

    /// <summary>
    /// Extra grant on top of <see cref="Role"/>: lets an Accountant see and enter HR & Payroll data
    /// without being promoted to Admin (which would also hand them settings/user management).
    /// Meaningless for Admin/Viewer — Admin already has it implicitly via
    /// <c>CompanySession.CanAccessPayroll</c>, Viewer can't post anything regardless.
    /// </summary>
    public bool CanAccessPayroll { get; set; }
}
