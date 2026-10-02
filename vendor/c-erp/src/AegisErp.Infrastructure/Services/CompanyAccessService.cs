using AegisErp.Domain;
using AegisErp.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>A company the signed-in user may work in, and the role they hold there. The three
/// Subscription* fields are the raw inputs to <see cref="SubscriptionStatusCalculator.Compute"/> —
/// carried here (rather than looked up separately) so <c>CompanySession</c> can gate access without
/// an extra query on every company switch.</summary>
public record CompanyAccessRow(
    int CompanyId, string Code, string Name, string Role, bool CanAccessPayroll,
    bool SubscriptionEnabled, DateOnly? SubscriptionPaidThroughDate, int SubscriptionGraceDays);

/// <summary>One user's grant on a company (for the access-management screen).</summary>
public record CompanyMemberRow(string UserId, string Email, string DisplayName, string Role, bool CanAccessPayroll);

/// <summary>Result of <see cref="CompanyAccessService.CreateUserAndGrantAsync"/> — tells the caller
/// whether a brand-new login was created or an existing account was simply granted access, so the
/// UI can word its confirmation accordingly.</summary>
public record CreateUserResult(bool UserWasCreated, string Email);

/// <summary>
/// Resolves and manages which companies a user can work in. A firm administrator implicitly
/// reaches every company; everyone else is limited to their explicit grants.
/// </summary>
public class CompanyAccessService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly UserManager<AppUser> _users;
    private readonly ICurrentCompany _current;

    public CompanyAccessService(IDbContextFactory<AegisDbContext> dbf, UserManager<AppUser> users, ICurrentCompany current)
    {
        _dbf = dbf;
        _users = users;
        _current = current;
    }

    /// <summary>Companies this user may open, with their role in each.</summary>
    public async Task<List<CompanyAccessRow>> GetCompaniesForUserAsync(string userId, bool isFirmAdmin)
    {
        await using var db = await _dbf.CreateDbContextAsync();

        if (isFirmAdmin)
        {
            // A FirmAdmin already bypasses every other per-company restriction (settings, GL,
            // every document type) — payroll gets no special exception from that same trust
            // boundary; see the Identity & Access Engineer review in the HR & Payroll plan.
            return await db.CompanySetups.AsNoTracking()
                .OrderBy(c => c.LegalName)
                .Select(c => new CompanyAccessRow(c.Id, c.CompanyCode, c.LegalName, AppRoles.Admin, true,
                    c.SubscriptionEnabled, c.SubscriptionPaidThroughDate, c.SubscriptionGraceDays))
                .ToListAsync();
        }

        return await db.UserCompanyAccess.AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderBy(a => a.Company.LegalName)
            .Select(a => new CompanyAccessRow(a.CompanyId, a.Company.CompanyCode, a.Company.LegalName, a.Role, a.CanAccessPayroll,
                a.Company.SubscriptionEnabled, a.Company.SubscriptionPaidThroughDate, a.Company.SubscriptionGraceDays))
            .ToListAsync();
    }

    /// <summary>The user's role in one company, or null if they have no access to it.</summary>
    public async Task<string?> GetRoleAsync(string userId, int companyId, bool isFirmAdmin)
    {
        if (isFirmAdmin) return AppRoles.Admin;

        await using var db = await _dbf.CreateDbContextAsync();
        return await db.UserCompanyAccess.AsNoTracking()
            .Where(a => a.UserId == userId && a.CompanyId == companyId)
            .Select(a => a.Role)
            .FirstOrDefaultAsync();
    }

    /// <summary>True when the user may open the given company.</summary>
    public async Task<bool> CanAccessAsync(string userId, int companyId, bool isFirmAdmin) =>
        await GetRoleAsync(userId, companyId, isFirmAdmin) is not null;

    /// <summary>Everyone granted access to a company.</summary>
    public async Task<List<CompanyMemberRow>> GetMembersAsync(int companyId)
    {
        if (!_current.IsFirmAdmin && (companyId != _current.CompanyId || !_current.CanAdminister))
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        return await db.UserCompanyAccess.AsNoTracking()
            .Where(a => a.CompanyId == companyId)
            .OrderBy(a => a.User.Email)
            .Select(a => new CompanyMemberRow(a.UserId, a.User.Email ?? "", a.User.DisplayName, a.Role, a.CanAccessPayroll))
            .ToListAsync();
    }

    /// <summary>
    /// Grants or revokes an Accountant's extra access to HR &amp; Payroll data — lets an owner
    /// delegate payroll to a trusted bookkeeper without promoting them to Admin (which would also
    /// unlock settings and team management). No-op in effect for Admin (already has it implicitly
    /// via <c>CompanySession.CanAccessPayroll</c>) or Viewer (can't post anything regardless), but
    /// still settable so the flag reflects intent if their role changes later.
    /// </summary>
    public async Task SetPayrollAccessAsync(string userId, int companyId, bool canAccess)
    {
        if (!_current.CanAdminister || (!_current.IsFirmAdmin && companyId != _current.CompanyId))
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var existing = await db.UserCompanyAccess.FirstOrDefaultAsync(a => a.UserId == userId && a.CompanyId == companyId)
            ?? throw new PostingException("This user has no access to this company.");
        existing.CanAccessPayroll = canAccess;
        await db.SaveChangesAsync();
    }

    /// <summary>Grants or updates a user's role on a company. CanAdminister is company-scoped
    /// (checked against the caller's <em>active</em> company), but a FirmAdmin's flag is true
    /// regardless of which company is active, so this still works correctly when a FirmAdmin grants
    /// access to a company other than the one they currently have open (e.g. right after creating
    /// it, from the Companies page).</summary>
    public async Task GrantAsync(string userId, int companyId, string role)
    {
        if (!_current.CanAdminister || (!_current.IsFirmAdmin && companyId != _current.CompanyId))
            throw new PostingException("You don't have permission to do this.");
        if (!AppRoles.CompanyRoles.Contains(role))
            throw new PostingException($"'{role}' is not a valid company role.");

        await using var db = await _dbf.CreateDbContextAsync();
        var existing = await db.UserCompanyAccess
            .FirstOrDefaultAsync(a => a.UserId == userId && a.CompanyId == companyId);

        if (existing is null)
        {
            db.UserCompanyAccess.Add(new UserCompanyAccess { UserId = userId, CompanyId = companyId, Role = role });
        }
        else
        {
            if (existing.Role == AppRoles.Admin && role != AppRoles.Admin)
                await EnsureNotLastAdminAsync(db, companyId, userId);
            existing.Role = role;
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Removes a user's access to a company.</summary>
    public async Task RevokeAsync(string userId, int companyId)
    {
        if (!_current.CanAdminister || (!_current.IsFirmAdmin && companyId != _current.CompanyId))
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var existing = await db.UserCompanyAccess
            .FirstOrDefaultAsync(a => a.UserId == userId && a.CompanyId == companyId);
        if (existing is null) return;

        if (existing.Role == AppRoles.Admin)
            await EnsureNotLastAdminAsync(db, companyId, userId);

        db.UserCompanyAccess.Remove(existing);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Guards against a company being left with no one who can manage it. Called before demoting
    /// or revoking an Admin — throws if <paramref name="userId"/> is the company's only remaining
    /// Admin (a FirmAdmin can always still get in, but a company should never depend on one having
    /// to intervene just to fix its own access list).
    /// </summary>
    private static async Task EnsureNotLastAdminAsync(AegisDbContext db, int companyId, string userId)
    {
        var otherAdmins = await db.UserCompanyAccess
            .CountAsync(a => a.CompanyId == companyId && a.Role == AppRoles.Admin && a.UserId != userId);
        if (otherAdmins == 0)
            throw new PostingException("This is the only Admin on this company — grant Admin to someone else first.");
    }

    /// <summary>
    /// Onboards a company's own user: creates the login if the email doesn't already have one
    /// (otherwise reuses the existing account, so someone already working in another company can
    /// simply be added to this one too), then grants the given role on <paramref name="companyId"/>.
    /// </summary>
    public async Task<CreateUserResult> CreateUserAndGrantAsync(
        string email, string displayName, string password, int companyId, string role, bool canAccessPayroll = false)
    {
        // Checked here too (not just relying on the GrantAsync call below) so a caller without
        // permission never gets as far as creating the Identity account in the first place.
        // FirmAdmin bypasses the companyId match (this is also called right after creating a new
        // company, from the Companies page, before that company is the caller's active one).
        if (!_current.CanAdminister || (!_current.IsFirmAdmin && companyId != _current.CompanyId))
            throw new PostingException("You don't have permission to do this.");

        email = email.Trim();
        if (string.IsNullOrWhiteSpace(email)) throw new PostingException("Email is required.");
        if (string.IsNullOrWhiteSpace(displayName)) throw new PostingException("Name is required.");
        if (!AppRoles.CompanyRoles.Contains(role))
            throw new PostingException($"'{role}' is not a valid company role.");

        var user = await _users.FindByEmailAsync(email);
        var created = false;

        if (user is null)
        {
            if (string.IsNullOrWhiteSpace(password))
                throw new PostingException("Password is required for a new user.");

            user = new AppUser { UserName = email, Email = email, DisplayName = displayName.Trim(), EmailConfirmed = true };
            var result = await _users.CreateAsync(user, password);
            if (!result.Succeeded)
                throw new PostingException(string.Join(" ", result.Errors.Select(e => e.Description)));
            created = true;
        }

        await GrantAsync(user.Id, companyId, role);
        if (canAccessPayroll) await SetPayrollAccessAsync(user.Id, companyId, true);
        return new CreateUserResult(created, email);
    }
}
