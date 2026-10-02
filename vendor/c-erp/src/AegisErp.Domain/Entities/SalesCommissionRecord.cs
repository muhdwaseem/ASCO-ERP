namespace AegisErp.Domain.Entities;

/// <summary>
/// The calculated Agent/Employee commission for one posted <see cref="SalesInvoiceLine"/> — Gross
/// Profit and item category are line-level attributes, so calculation happens at this granularity,
/// not per-invoice or per-customer/period. Written once, at posting time, by
/// <see cref="Services.CommissionService.CalculateForInvoiceAsync"/>; the Employee/Agent stored here
/// are whichever were assigned to the customer as of the invoice's own date (see
/// <see cref="CustomerAssignmentHistory"/>), not the customer's current assignment — so a later
/// reassignment never retroactively changes an already-calculated record. Deleted if the invoice is
/// voided.
/// </summary>
public class SalesCommissionRecord : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    public int SalesInvoiceId { get; set; }
    public SalesInvoice SalesInvoice { get; set; } = null!;

    public int SalesInvoiceLineId { get; set; }
    public SalesInvoiceLine SalesInvoiceLine { get; set; } = null!;

    /// <summary>Employee assigned to the customer as of the invoice date. Null if none was assigned.</summary>
    public int? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    /// <summary>Agent assigned to the customer as of the invoice date. Null if none was assigned.</summary>
    public int? AgentId { get; set; }
    public Agent? Agent { get; set; }

    /// <summary>(Sales price − Cost) × Quantity for this line.</summary>
    public decimal GrossProfit { get; set; }

    /// <summary>False when <see cref="GrossProfit"/> falls in the zero-rate slab — no commission for
    /// either party, and the amount fields below are all 0.</summary>
    public bool IsEligible { get; set; }

    public decimal AgentCommission { get; set; }

    /// <summary><see cref="GrossProfit"/> minus <see cref="AgentCommission"/> — the base the Employee
    /// commission is calculated on.</summary>
    public decimal AdjustedProfit { get; set; }

    public decimal EmployeeCommission { get; set; }

    public DateTime CalculatedAtUtc { get; set; }
}
