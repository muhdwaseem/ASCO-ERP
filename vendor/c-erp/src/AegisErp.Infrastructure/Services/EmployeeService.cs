using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

public record EmployeeInput(
    string FullName, string? Designation, int? CostCenterId, DateOnly JoiningDate,
    decimal BasicSalary, decimal HousingAllowance, decimal TransportAllowance, decimal OtherAllowance,
    string? Mobile, string? Email, string? BankName, string? Iban, int EmployeeExpenseAccountId, string? Notes,
    DateOnly? VisaExpiryDate = null, string? EmiratesIdNumber = null, DateOnly? EmiratesIdExpiryDate = null,
    string? LabourCardNumber = null, DateOnly? LabourCardExpiryDate = null,
    string? PassportNumber = null, DateOnly? PassportExpiryDate = null,
    bool GratuityEligible = true, string? WpsAgentId = null,
    int? ManagerId = null,
    string? InsuranceProvider = null, string? InsurancePolicyNumber = null,
    string? InsuranceCoverageType = null, DateOnly? InsurancePolicyExpiryDate = null,
    // Optional note attached to whichever EmployeeChangeHistory row(s) this save produces (e.g.
    // "Annual increment") — never persisted on Employee itself, and ignored on Create since
    // nothing is being logged there.
    string? ChangeReason = null);

/// <summary>One employee's document-compliance flag for the expiry warning banner — see
/// <see cref="EmployeeService.GetExpiringDocumentsAsync"/>.</summary>
public record ExpiringDocument(int EmployeeId, string EmployeeCode, string EmployeeName, string DocumentType, DateOnly ExpiryDate);

/// <summary>Result of <see cref="EmployeeService.GrantPortalAccessAsync"/> — tells the caller
/// whether a brand-new login was created or an existing account was linked, so the UI can word its
/// confirmation accordingly. Mirrors <c>CompanyAccessService.CreateUserResult</c>.</summary>
public record GrantPortalAccessResult(bool UserWasCreated, string Email);

public class EmployeeService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly UserManager<AppUser> _users;
    private readonly ICurrentCompany _current;

    public EmployeeService(IDbContextFactory<AegisDbContext> dbf, UserManager<AppUser> users, ICurrentCompany current)
    {
        _dbf = dbf;
        _users = users;
        _current = current;
    }

    public async Task<List<Employee>> GetAllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Employees.AsNoTracking()
            .Include(m => m.CostCenter).Include(m => m.EmployeeExpenseAccount)
            .OrderBy(m => m.EmployeeCode)
            .ToListAsync();
    }

    public async Task<Employee?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Employees.AsNoTracking()
            .Include(m => m.CostCenter).Include(m => m.EmployeeExpenseAccount).Include(m => m.Manager)
            .FirstOrDefaultAsync(m => m.Id == id);
    }

    /// <summary>Resolves the Employee Self-Service login for a signed-in portal user. Called by
    /// <c>EmployeePortalSession</c> before it has set <see cref="ICurrentCompany.CompanyId"/> — the
    /// resulting unscoped read (see <c>AegisDbContext.CurrentCompanyId == null</c>) is exactly what's
    /// needed here, since which company this employee belongs to is the very thing being resolved.</summary>
    public async Task<Employee?> GetByUserIdAsync(string userId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Employees.AsNoTracking().Include(m => m.Manager)
            .FirstOrDefaultAsync(m => m.UserId == userId);
    }

    /// <summary>Turns on Employee Self-Service for one employee — creates the login if the email
    /// doesn't already have one (otherwise reuses the existing account, same "create-or-reuse"
    /// pattern as <c>CompanyAccessService.CreateUserAndGrantAsync</c>), grants the
    /// <see cref="AppRoles.Employee"/> role, and links it back via <see cref="Employee.UserId"/>.
    /// Rejects re-granting over an already-linked employee — call
    /// <see cref="RevokePortalAccessAsync"/> first to change the login.</summary>
    public async Task<GrantPortalAccessResult> GrantPortalAccessAsync(int employeeId, string email, string password)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        email = email.Trim();
        if (string.IsNullOrWhiteSpace(email)) throw new PostingException("Email is required.");

        await using var db = await _dbf.CreateDbContextAsync();
        var employee = await db.Employees.FirstOrDefaultAsync(m => m.Id == employeeId)
            ?? throw new PostingException("Employee not found.");
        if (employee.UserId is not null)
            throw new PostingException("Portal access is already granted — revoke it first to change the login.");

        var user = await _users.FindByEmailAsync(email);
        var created = false;

        if (user is null)
        {
            if (string.IsNullOrWhiteSpace(password))
                throw new PostingException("Password is required for a new login.");

            user = new AppUser { UserName = email, Email = email, DisplayName = employee.FullName, EmailConfirmed = true };
            var result = await _users.CreateAsync(user, password);
            if (!result.Succeeded)
                throw new PostingException(string.Join(" ", result.Errors.Select(e => e.Description)));
            created = true;
        }

        if (!await _users.IsInRoleAsync(user, AppRoles.Employee))
        {
            var result = await _users.AddToRoleAsync(user, AppRoles.Employee);
            if (!result.Succeeded)
                throw new PostingException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }

        employee.UserId = user.Id;
        await db.SaveChangesAsync();
        return new GrantPortalAccessResult(created, email);
    }

    /// <summary>Turns off Employee Self-Service for one employee. The login itself is left alone
    /// (same "revoke access, don't delete the account" pattern as <c>CompanyAccessService.RevokeAsync</c>)
    /// — only the link back to this employee is cleared, which is what every ESS page actually checks.</summary>
    public async Task RevokePortalAccessAsync(int employeeId)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var employee = await db.Employees.FirstOrDefaultAsync(m => m.Id == employeeId)
            ?? throw new PostingException("Employee not found.");

        employee.UserId = null;
        await db.SaveChangesAsync();
    }

    public async Task<Employee> CreateAsync(EmployeeInput input, string createdBy, DateTime nowUtc)
    {
        ValidateInput(input);

        await using var db = await _dbf.CreateDbContextAsync();

        var codes = await db.Employees.Select(m => m.EmployeeCode).ToListAsync();
        var max = 0;
        foreach (var code in codes)
            if (code.StartsWith("EMP-") && int.TryParse(code.AsSpan(4), out var n) && n > max)
                max = n;

        var employee = new Employee { EmployeeCode = $"EMP-{max + 1:0000}", CreatedBy = createdBy, CreatedAtUtc = nowUtc };
        ApplyInput(employee, input);

        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee;
    }

    public async Task UpdateAsync(int id, EmployeeInput input, string changedBy, DateTime nowUtc)
    {
        ValidateInput(input);

        await using var db = await _dbf.CreateDbContextAsync();
        var employee = await db.Employees.Include(m => m.CostCenter).FirstOrDefaultAsync(m => m.Id == id)
            ?? throw new PostingException("Employee not found.");

        if (input.ManagerId is int managerId)
        {
            if (managerId == id) throw new PostingException("An employee cannot be their own manager.");

            // Walk up from the proposed manager's own chain — if it ever reaches this employee,
            // the proposed manager already (directly or indirectly) reports to them, so assigning
            // them as manager would create a reporting-line loop.
            var chain = await db.Employees.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.ManagerId);
            var cursor = (int?)managerId;
            var seen = new HashSet<int>();
            while (cursor is int c && seen.Add(c))
            {
                if (c == id) throw new PostingException("That manager assignment would create a reporting-line loop.");
                cursor = chain.GetValueOrDefault(c);
            }
        }

        var costCenterNames = await db.CostCenters.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name);
        var employeeNames = await db.Employees.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.FullName);
        LogChangesIfNeeded(employee, input, costCenterNames, employeeNames, changedBy, nowUtc);

        ApplyInput(employee, input);
        await db.SaveChangesAsync();
    }

    /// <summary>Logs one EmployeeChangeHistory row per change type (Salary/Designation/Department)
    /// that this save actually alters — called before <see cref="ApplyInput"/> overwrites the
    /// tracked entity, so old-vs-new can still be diffed. Never called from CreateAsync: an
    /// employee's starting values are already visible on their own record, so there's nothing to
    /// diff against on hire.</summary>
    private static void LogChangesIfNeeded(Employee e, EmployeeInput input, Dictionary<int, string> costCenterNames,
        Dictionary<int, string> employeeNames, string changedBy, DateTime nowUtc)
    {
        var by = string.IsNullOrWhiteSpace(changedBy) ? "System Admin" : changedBy.Trim();
        var reason = string.IsNullOrWhiteSpace(input.ChangeReason) ? null : input.ChangeReason.Trim();

        void Log(EmployeeChangeType type, string previous, string next) =>
            e.ChangeHistory.Add(new EmployeeChangeHistory
            {
                ChangeType = type, PreviousValue = previous, NewValue = next,
                Reason = reason, ChangedAtUtc = nowUtc, ChangedBy = by,
            });

        var newDesignation = string.IsNullOrWhiteSpace(input.Designation) ? null : input.Designation.Trim();
        if (!string.Equals(e.Designation, newDesignation, StringComparison.Ordinal))
            Log(EmployeeChangeType.Designation, e.Designation ?? "—", newDesignation ?? "—");

        if (e.CostCenterId != input.CostCenterId)
        {
            string Name(int? id) => id is int i && costCenterNames.TryGetValue(i, out var n) ? n : "—";
            Log(EmployeeChangeType.Department, Name(e.CostCenterId), Name(input.CostCenterId));
        }

        if (e.ManagerId != input.ManagerId)
        {
            string Name(int? id) => id is int i && employeeNames.TryGetValue(i, out var n) ? n : "—";
            Log(EmployeeChangeType.Manager, Name(e.ManagerId), Name(input.ManagerId));
        }

        if (e.BasicSalary != input.BasicSalary || e.HousingAllowance != input.HousingAllowance
            || e.TransportAllowance != input.TransportAllowance || e.OtherAllowance != input.OtherAllowance)
        {
            string Format(decimal basic, decimal housing, decimal transport, decimal other) =>
                $"Basic {basic:N2} / Housing {housing:N2} / Transport {transport:N2} / Other {other:N2} (Gross {basic + housing + transport + other:N2})";
            Log(EmployeeChangeType.Salary,
                Format(e.BasicSalary, e.HousingAllowance, e.TransportAllowance, e.OtherAllowance),
                Format(input.BasicSalary, input.HousingAllowance, input.TransportAllowance, input.OtherAllowance));
        }
    }

    /// <summary>Salary/designation/department change history for one employee, oldest first.
    /// Goes through Employees (company-scoped) rather than querying EmployeeChangeHistories
    /// directly, since the history row itself carries no CompanyId — same "scope through the
    /// parent" pattern PayrollService.GetRunLineAsync uses for PayrollRunLine.</summary>
    public async Task<List<EmployeeChangeHistory>> GetChangeHistoryAsync(int employeeId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Employees.AsNoTracking().SelectMany(e => e.ChangeHistory)
            .Where(h => h.EmployeeId == employeeId)
            .OrderBy(h => h.ChangedAtUtc).ThenBy(h => h.Id)
            .ToListAsync();
    }

    /// <summary>Marks an employee Terminated (or reactivates them) — does not touch any past
    /// payroll run, since <see cref="PayrollRunLine"/> snapshots salary at run creation time.</summary>
    public async Task SetStatusAsync(int id, EmployeeStatus status, DateOnly? terminationDate)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var employee = await db.Employees.FirstOrDefaultAsync(m => m.Id == id)
            ?? throw new PostingException("Employee not found.");
        employee.Status = status;
        employee.TerminationDate = status == EmployeeStatus.Terminated ? terminationDate : null;
        await db.SaveChangesAsync();
    }

    private static void ValidateInput(EmployeeInput input)
    {
        if (string.IsNullOrWhiteSpace(input.FullName)) throw new PostingException("Employee name is required.");
        if (input.BasicSalary < 0) throw new PostingException("Basic salary cannot be negative.");
        if (input.HousingAllowance < 0 || input.TransportAllowance < 0 || input.OtherAllowance < 0)
            throw new PostingException("Allowances cannot be negative.");
        if (input.EmployeeExpenseAccountId == 0) throw new PostingException("Select the salary expense account.");
    }

    private static void ApplyInput(Employee employee, EmployeeInput input)
    {
        employee.FullName = input.FullName.Trim();
        employee.Designation = string.IsNullOrWhiteSpace(input.Designation) ? null : input.Designation.Trim();
        employee.CostCenterId = input.CostCenterId;
        employee.JoiningDate = input.JoiningDate;
        employee.BasicSalary = input.BasicSalary;
        employee.HousingAllowance = input.HousingAllowance;
        employee.TransportAllowance = input.TransportAllowance;
        employee.OtherAllowance = input.OtherAllowance;
        employee.Mobile = string.IsNullOrWhiteSpace(input.Mobile) ? null : input.Mobile.Trim();
        employee.Email = string.IsNullOrWhiteSpace(input.Email) ? null : input.Email.Trim();
        employee.BankName = string.IsNullOrWhiteSpace(input.BankName) ? null : input.BankName.Trim();
        employee.Iban = string.IsNullOrWhiteSpace(input.Iban) ? null : input.Iban.Trim();
        employee.EmployeeExpenseAccountId = input.EmployeeExpenseAccountId;
        employee.Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
        employee.VisaExpiryDate = input.VisaExpiryDate;
        employee.EmiratesIdNumber = string.IsNullOrWhiteSpace(input.EmiratesIdNumber) ? null : input.EmiratesIdNumber.Trim();
        employee.EmiratesIdExpiryDate = input.EmiratesIdExpiryDate;
        employee.LabourCardNumber = string.IsNullOrWhiteSpace(input.LabourCardNumber) ? null : input.LabourCardNumber.Trim();
        employee.LabourCardExpiryDate = input.LabourCardExpiryDate;
        employee.PassportNumber = string.IsNullOrWhiteSpace(input.PassportNumber) ? null : input.PassportNumber.Trim();
        employee.PassportExpiryDate = input.PassportExpiryDate;
        employee.GratuityEligible = input.GratuityEligible;
        employee.WpsAgentId = string.IsNullOrWhiteSpace(input.WpsAgentId) ? null : input.WpsAgentId.Trim();
        employee.ManagerId = input.ManagerId;
        employee.InsuranceProvider = string.IsNullOrWhiteSpace(input.InsuranceProvider) ? null : input.InsuranceProvider.Trim();
        employee.InsurancePolicyNumber = string.IsNullOrWhiteSpace(input.InsurancePolicyNumber) ? null : input.InsurancePolicyNumber.Trim();
        employee.InsuranceCoverageType = string.IsNullOrWhiteSpace(input.InsuranceCoverageType) ? null : input.InsuranceCoverageType.Trim();
        employee.InsurancePolicyExpiryDate = input.InsurancePolicyExpiryDate;
    }

    /// <summary>
    /// Every Active employee's compliance documents expiring within <paramref name="withinDays"/>
    /// (visa, Emirates ID, labour card, passport), soonest first — for a warning banner. An
    /// employee with multiple documents expiring soon appears once per document.
    /// </summary>
    public async Task<List<ExpiringDocument>> GetExpiringDocumentsAsync(int withinDays = 90, DateTime? asOfUtc = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var cutoff = DateOnly.FromDateTime(asOfUtc ?? DateTime.UtcNow).AddDays(withinDays);

        var employees = await db.Employees
            .Where(m => m.Status == EmployeeStatus.Active)
            .ToListAsync();

        var results = new List<ExpiringDocument>();
        foreach (var m in employees)
        {
            void AddIfExpiring(string docType, DateOnly? expiry)
            {
                if (expiry is DateOnly d && d <= cutoff)
                    results.Add(new ExpiringDocument(m.Id, m.EmployeeCode, m.FullName, docType, d));
            }
            AddIfExpiring("Visa", m.VisaExpiryDate);
            AddIfExpiring("Emirates ID", m.EmiratesIdExpiryDate);
            AddIfExpiring("Labour Card", m.LabourCardExpiryDate);
            AddIfExpiring("Passport", m.PassportExpiryDate);
            AddIfExpiring("Health Insurance", m.InsurancePolicyExpiryDate);
        }

        return results.OrderBy(r => r.ExpiryDate).ToList();
    }
}
