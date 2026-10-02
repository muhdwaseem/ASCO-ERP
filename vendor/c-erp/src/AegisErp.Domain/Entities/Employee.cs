namespace AegisErp.Domain.Entities;

/// <summary>
/// An HR & Payroll staff record. Registering an employee has no GL impact by itself — only a
/// <see cref="PayrollRun"/> posts anything, and only once it's actually posted.
/// </summary>
public class Employee : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    /// <summary>Auto-numbered, e.g. "EMP-0001" — same "max numeric suffix + 1" convention as Customer/Vendor/FixedAsset codes.</summary>
    public string EmployeeCode { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;
    public string? Designation { get; set; }

    /// <summary>Reporting line — who this employee reports to. Foundation for Phase 3's Employee
    /// Self-Service (approval routing needs this to exist first); until then, purely informational.</summary>
    public int? ManagerId { get; set; }
    public Employee? Manager { get; set; }

    /// <summary>Reused as "Department" — a Cost Center already represents a company segment in
    /// this app's existing P&amp;L reporting, so this avoids a second, parallel department concept.</summary>
    public int? CostCenterId { get; set; }
    public CostCenter? CostCenter { get; set; }

    public DateOnly JoiningDate { get; set; }
    public EmployeeStatus Status { get; set; } = EmployeeStatus.Active;
    public DateOnly? TerminationDate { get; set; }

    public decimal BasicSalary { get; set; }
    public decimal HousingAllowance { get; set; }
    public decimal TransportAllowance { get; set; }
    public decimal OtherAllowance { get; set; }

    public string? Mobile { get; set; }
    public string? Email { get; set; }
    public string? BankName { get; set; }
    public string? Iban { get; set; }

    /// <summary>The P&amp;L account this employee's salary posts to when a payroll run is posted.</summary>
    public int EmployeeExpenseAccountId { get; set; }
    public Account EmployeeExpenseAccount { get; set; } = null!;

    public string? Notes { get; set; }

    // ── UAE compliance documents (all optional — not every employer has entered all four) ──
    public DateOnly? VisaExpiryDate { get; set; }
    public string? EmiratesIdNumber { get; set; }
    public DateOnly? EmiratesIdExpiryDate { get; set; }

    /// <summary>Also serves as the UAE WPS 14-digit Person ID (see <c>WpsFileService</c>).</summary>
    public string? LabourCardNumber { get; set; }
    public DateOnly? LabourCardExpiryDate { get; set; }
    public string? PassportNumber { get; set; }
    public DateOnly? PassportExpiryDate { get; set; }

    /// <summary>Escape hatch for a category the employer knows is excluded from gratuity (e.g. a
    /// contract type outside UAE Federal Decree-Law No. 33 of 2021's scope) — the app doesn't try
    /// to infer this itself. See <c>GratuityService</c>.</summary>
    public bool GratuityEligible { get; set; } = true;

    /// <summary>9-digit bank/exchange-house routing code required on every UAE WPS SIF file line
    /// for this employee — see <c>WpsFileService</c>.</summary>
    public string? WpsAgentId { get; set; }

    // ── Health insurance (mandatory for employees in Dubai/Abu Dhabi under DHA/DOH rules) ──
    public string? InsuranceProvider { get; set; }
    public string? InsurancePolicyNumber { get; set; }
    /// <summary>Free text, e.g. "Basic", "Enhanced" — DHA's mandated tiers differ by salary band,
    /// but the app doesn't try to infer or enforce which tier applies.</summary>
    public string? InsuranceCoverageType { get; set; }
    public DateOnly? InsurancePolicyExpiryDate { get; set; }

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Links this employee to their Employee Self-Service login (<c>AppUser.Id</c>). Null
    /// means no portal login has been granted yet. Deliberately not a foreign key into ASP.NET
    /// Identity's tables — see <c>EmployeePortalSession</c> for how this is resolved.</summary>
    public string? UserId { get; set; }

    public decimal GrossSalary => BasicSalary + HousingAllowance + TransportAllowance + OtherAllowance;

    public List<EmployeeChangeHistory> ChangeHistory { get; set; } = new();
}

public enum EmployeeStatus
{
    Active = 1,
    Terminated = 2,
}
