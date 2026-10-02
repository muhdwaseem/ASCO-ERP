namespace AegisErp.Domain.Entities;

/// <summary>
/// One employee's leave request. Has no GL impact — leave *encashment* (converting unused leave
/// into a cash payout) would, but that's an explicitly deferred follow-up, not built here. The
/// employee's balance is computed on demand (see <c>LeaveService.GetBalanceAsync</c>), not stored
/// as a running ledger.
/// </summary>
public class LeaveRequest : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public LeaveType Type { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }

    /// <summary>Computed from the date range at creation and stored — so a later edit to the
    /// underlying dates (not currently exposed, but defensively) can't silently redefine an
    /// already-approved request's day count.</summary>
    public int Days { get; set; }

    public string? Reason { get; set; }
    public LeaveRequestStatus Status { get; set; } = LeaveRequestStatus.Pending;

    public string? DecisionBy { get; set; }
    public DateTime? DecisionAtUtc { get; set; }

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }
}

public enum LeaveType
{
    Annual = 1,
    Sick = 2,
    Unpaid = 3,
    Other = 4,
}

public enum LeaveRequestStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
}
