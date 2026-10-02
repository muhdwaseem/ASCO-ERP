namespace AegisErp.Domain.Entities;

/// <summary>
/// A third-party sales agent — distinct from <see cref="Customer"/> and <see cref="Vendor"/> — who
/// can be linked to a customer alongside (or instead of) an <see cref="Employee"/>, for commission
/// attribution. See <see cref="CustomerAssignmentHistory"/> for how that link changes over time.
/// </summary>
public class Agent : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    /// <summary>System-generated, e.g. "AGT-0001". Unique within the company.</summary>
    public string AgentCode { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }

    /// <summary>For commission payout.</summary>
    public string? BankName { get; set; }
    public string? Iban { get; set; }

    /// <summary>The employee who introduced/manages this agent and shares responsibility for their
    /// customers — null means the agent operates independently ("Direct to Company").</summary>
    public int? ReportsToEmployeeId { get; set; }
    public Employee? ReportsToEmployee { get; set; }

    public AgentStatus Status { get; set; } = AgentStatus.Active;
}

public enum AgentStatus
{
    Active = 1,
    Inactive = 2
}
