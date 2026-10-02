namespace AegisErp.Domain.Entities;

/// <summary>
/// One date-ranged period of a customer's Employee/Agent assignment, for the Commission Workflow.
/// Unlike <see cref="SalespersonAssignmentHistory"/> (a flat append-log of free-text names with no
/// closing date), this table is a true interval history: reassigning a customer closes the current
/// open row (<see cref="EffectiveTo"/> = the cut-off date) and opens a new one
/// (<see cref="EffectiveFrom"/> = the same date), so a transaction dated before the cut-off can be
/// attributed to the old Employee/Agent and one dated on/after it to the new one — see
/// <see cref="Services.CustomerService.ReassignEmployeeAgentAsync"/>. <see cref="EffectiveTo"/> null
/// means this row is the customer's current assignment.
/// </summary>
public class CustomerAssignmentHistory : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>Null means no employee was assigned for this period.</summary>
    public int? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    /// <summary>Null means no agent was assigned for this period.</summary>
    public int? AgentId { get; set; }
    public Agent? Agent { get; set; }

    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }

    public string ChangedBy { get; set; } = "System Admin";
    public DateTime ChangedAtUtc { get; set; }
}
