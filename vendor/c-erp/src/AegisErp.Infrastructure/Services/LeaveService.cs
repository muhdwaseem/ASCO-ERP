using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

public record LeaveBalance(int EmployeeId, int AccruedDays, int UsedDays, int RemainingDays);

public class LeaveService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;
    public LeaveService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    /// <summary>
    /// UAE law grants 30 calendar days/year of annual leave after 1 year of service; this prorates
    /// from day one instead ("nothing before 1 year"), which is a disclosed, employee-favorable
    /// simplification rather than a strict reading of the law.
    /// </summary>
    public static int AccruedDays(DateOnly joiningDate, DateOnly asOf)
    {
        var daysEmployed = asOf.DayNumber - joiningDate.DayNumber;
        if (daysEmployed <= 0) return 0;
        return Math.Min(30, (int)Math.Floor(daysEmployed / 365.0 * 30));
    }

    public async Task<LeaveBalance> GetBalanceAsync(int employeeId, DateOnly asOf)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var employee = await db.Employees.FirstOrDefaultAsync(m => m.Id == employeeId)
            ?? throw new PostingException("Employee not found.");

        var accrued = AccruedDays(employee.JoiningDate, asOf);
        var used = await db.LeaveRequests
            .Where(l => l.EmployeeId == employeeId && l.Type == LeaveType.Annual && l.Status == LeaveRequestStatus.Approved)
            .SumAsync(l => (int?)l.Days) ?? 0;

        return new LeaveBalance(employeeId, accrued, used, Math.Max(0, accrued - used));
    }

    public async Task<List<LeaveRequest>> GetAllAsync(int? employeeId = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var query = db.LeaveRequests.AsNoTracking().Include(l => l.Employee).AsQueryable();
        if (employeeId is int id) query = query.Where(l => l.EmployeeId == id);
        return await query.OrderByDescending(l => l.StartDate).ToListAsync();
    }

    public async Task<LeaveRequest> CreateRequestAsync(
        int employeeId, LeaveType type, DateOnly startDate, DateOnly endDate, string? reason, string createdBy, DateTime nowUtc)
    {
        if (endDate < startDate) throw new PostingException("End date cannot be before start date.");
        var days = endDate.DayNumber - startDate.DayNumber + 1;

        await using var db = await _dbf.CreateDbContextAsync();
        var employee = await db.Employees.FirstOrDefaultAsync(m => m.Id == employeeId)
            ?? throw new PostingException("Employee not found.");

        if (type == LeaveType.Annual)
        {
            var balance = await GetBalanceAsync(employeeId, startDate);
            if (days > balance.RemainingDays)
                throw new PostingException($"{employee.FullName} only has {balance.RemainingDays} annual leave day(s) remaining as of {startDate:dd MMM yyyy}.");
        }

        var request = new LeaveRequest
        {
            EmployeeId = employeeId,
            Type = type,
            StartDate = startDate,
            EndDate = endDate,
            Days = days,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            Status = LeaveRequestStatus.Pending,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
        };
        db.LeaveRequests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    public async Task DecideAsync(int requestId, bool approved, string decidedBy, DateTime nowUtc)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        await using var db = await _dbf.CreateDbContextAsync();
        var request = await db.LeaveRequests.Include(l => l.Employee).FirstOrDefaultAsync(l => l.Id == requestId)
            ?? throw new PostingException("Leave request not found.");
        if (request.Status != LeaveRequestStatus.Pending)
            throw new PostingException("Only pending requests can be decided.");

        if (approved && request.Type == LeaveType.Annual)
        {
            var balance = await GetBalanceAsync(request.EmployeeId, request.StartDate);
            if (request.Days > balance.RemainingDays)
                throw new PostingException($"{request.Employee.FullName} only has {balance.RemainingDays} annual leave day(s) remaining as of {request.StartDate:dd MMM yyyy}.");
        }

        request.Status = approved ? LeaveRequestStatus.Approved : LeaveRequestStatus.Rejected;
        request.DecisionBy = decidedBy;
        request.DecisionAtUtc = nowUtc;
        await db.SaveChangesAsync();
    }
}
