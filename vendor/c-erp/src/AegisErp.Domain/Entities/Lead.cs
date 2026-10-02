namespace AegisErp.Domain.Entities;

/// <summary>
/// A pre-sale enquiry moving through a simple pipeline. Has no GL impact anywhere — a Lead only
/// touches accounting once it's converted into a real <see cref="Customer"/> and goes through the
/// normal Estimate/Sales Invoice flow.
/// </summary>
public class Lead : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
    public string? Mobile { get; set; }
    public string? Email { get; set; }

    /// <summary>Free text (e.g. "Referral", "Website", "Walk-in") — no curated list for v1.</summary>
    public string? Source { get; set; }

    public LeadStage Stage { get; set; } = LeadStage.New;
    public decimal? EstimatedValue { get; set; }

    /// <summary>Reuses the company-curated Salesperson list (<see cref="CompanySetup.Salespersons"/>)
    /// — the exact dropdown pattern Estimate/Sales Invoice already use, no new curated-list entity.</summary>
    public string? AssignedTo { get; set; }

    public string? Notes { get; set; }

    /// <summary>Set once this lead is converted — see <c>LeadService.ConvertToCustomerAsync</c>.</summary>
    public int? ConvertedCustomerId { get; set; }
    public Customer? ConvertedCustomer { get; set; }

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Denormalized from the latest <see cref="LeadActivity"/> — lets the list page sort
    /// stale leads to the top without a join on every load.</summary>
    public DateTime? LastActivityAtUtc { get; set; }

    public List<LeadActivity> Activities { get; set; } = new();
}

/// <summary>One logged touchpoint on a lead — a simple append-only timeline, same shape as
/// <see cref="InvoiceReminderLog"/>.</summary>
public class LeadActivity
{
    public int Id { get; set; }

    public int LeadId { get; set; }
    public Lead Lead { get; set; } = null!;

    public LeadActivityType Type { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateOnly ActivityDate { get; set; }

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }
}

public enum LeadStage
{
    New = 1,
    Contacted = 2,
    Qualified = 3,
    Won = 4,
    Lost = 5,
}

public enum LeadActivityType
{
    Call = 1,
    Email = 2,
    Meeting = 3,
    Note = 4,
}
