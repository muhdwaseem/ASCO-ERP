namespace AegisErp.Domain.Entities;

/// <summary>One change to an employee's salary, designation, or department (cost centre). Logged by
/// EmployeeService whenever the value actually changes — never on hire, since the starting values
/// are already visible on the employee's own record. Exists so HR has a visible history of "what
/// changed and when" without needing to consult payroll runs or guess from memory.</summary>
public class EmployeeChangeHistory
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public EmployeeChangeType ChangeType { get; set; }
    public string PreviousValue { get; set; } = "—";
    public string NewValue { get; set; } = "—";

    /// <summary>Optional free-text note, e.g. "Annual increment" or "Promoted to Team Lead".</summary>
    public string? Reason { get; set; }

    public DateTime ChangedAtUtc { get; set; }
    public string ChangedBy { get; set; } = "System Admin";
}

public enum EmployeeChangeType { Salary = 1, Designation = 2, Department = 3, Manager = 4 }
