using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>One posted line's commission calculation, flattened for the Commission Report — see
/// <see cref="CommissionService.GetCommissionReportAsync"/>.</summary>
public record CommissionReportRow(
    int RecordId, int CustomerId, DateOnly InvoiceDate, string InvoiceNo, string CustomerName,
    string? ItemName, decimal GrossProfit, bool IsEligible,
    int? EmployeeId, string? EmployeeName, int? AgentId, string? AgentName,
    decimal AgentCommission, decimal AdjustedProfit, decimal EmployeeCommission);

/// <summary>One customer reassignment, with the immediately-preceding assignment alongside it — see
/// <see cref="CommissionService.GetReassignmentAuditLogAsync"/>.</summary>
public record ReassignmentAuditRow(
    int HistoryId, string CustomerCode, string CustomerName,
    DateOnly EffectiveFrom, string ChangedBy, DateTime ChangedAtUtc,
    string? PreviousEmployeeName, string? PreviousAgentName,
    string? NewEmployeeName, string? NewAgentName);

/// <summary>
/// The Commission Workflow's calculation engine and report queries. Calculation
/// (<see cref="CalculateForInvoiceAsync"/>) is static and takes an already-open
/// <see cref="AegisDbContext"/> because it must run inside <c>SalesInvoiceService</c>'s own posting
/// transaction — see its call sites in <c>PostDraftAsync</c>/<c>CreateAndPostAsync</c> — so a
/// commission record is never created for an invoice whose GL posting then fails to commit, and vice
/// versa. The report methods below are ordinary instance methods with their own context, like any
/// other read-only query.
/// </summary>
public class CommissionService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;

    public CommissionService(IDbContextFactory<AegisDbContext> dbf)
    {
        _dbf = dbf;
    }

    /// <summary>
    /// Calculates and stages (via <c>db.SalesCommissionRecords.Add</c> — not yet saved; the caller's
    /// own <c>SaveChangesAsync</c>/<c>SaveAndCommitAsync</c> persists it) one <see cref="SalesCommissionRecord"/>
    /// per eligible line of a just-posted invoice, per Section 6 of the Commission Workflow spec:
    /// Gross Profit → minimum-eligibility/profit-slab check → category + slab rate → Agent commission
    /// (deducted first) → Employee commission on the Agent-adjusted profit, position-multiplied.
    /// The Agent used is whichever was assigned to the customer as of the invoice's own date
    /// (<see cref="CustomerService.ResolveAssignmentAsOf"/>) — Agents aren't assigned per line.
    /// The Employee is <see cref="SalesInvoiceLine.AssignedToEmployeeId"/> when that specific line
    /// has a Task assignment (commission follows whoever actually delivered that service), falling
    /// back to the customer's date-of-invoice assignment when the line has none — so a plain (non-PRO,
    /// unassigned) invoice still attributes exactly as it did before Task assignment existed, and a
    /// later reassignment/task edit never rewrites an already-calculated record. A line with no
    /// <see cref="SalesInvoiceLine.ItemId"/> or whose <see cref="Item.CostPrice"/> is unset has no
    /// Gross Profit basis and is skipped entirely (no record — not "zero commission").
    /// <paramref name="invoice"/> must already have <see cref="SalesInvoice.Lines"/> and
    /// <see cref="SalesInvoice.Customer"/> loaded.
    /// </summary>
    public static async Task CalculateForInvoiceAsync(AegisDbContext db, SalesInvoice invoice, DateTime nowUtc)
    {
        var itemIds = invoice.Lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value).Distinct().ToList();
        var itemsById = await db.Items.AsNoTracking()
            .Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);

        var history = await db.CustomerAssignmentHistories.AsNoTracking()
            .Where(h => h.CustomerId == invoice.CustomerId).ToListAsync();
        var (customerEmployeeId, agentId) = CustomerService.ResolveAssignmentAsOf(history, invoice.Customer, invoice.Date);

        var categoryRates = await db.CommissionCategoryRates.AsNoTracking().Where(r => r.IsActive).ToListAsync();
        // Ordered client-side, not via .OrderBy on the query — Sqlite can't translate ORDER BY on a
        // decimal column (see CommissionConfigService.GetProfitSlabsAsync, which has the same issue).
        var slabs = (await db.CommissionProfitSlabs.AsNoTracking().Where(s => s.IsActive).ToListAsync())
            .OrderBy(s => s.GrossProfitFrom).ToList();

        // Cached per employee, not computed once for the whole invoice — different lines can now
        // resolve to different employees via AssignedToEmployeeId.
        var positionMultiplierCache = new Dictionary<int, decimal>();
        async Task<decimal> ResolvePositionMultiplierAsync(int empId)
        {
            if (positionMultiplierCache.TryGetValue(empId, out var cached)) return cached;
            var multiplier = 1.0m;
            var designation = (await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == empId))?.Designation;
            if (!string.IsNullOrWhiteSpace(designation))
            {
                var posRate = await db.CommissionPositionRates.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.IsActive && p.Position.ToLower() == designation.ToLower());
                if (posRate is not null) multiplier = posRate.RateMultiplier;
            }
            positionMultiplierCache[empId] = multiplier;
            return multiplier;
        }

        foreach (var line in invoice.Lines)
        {
            if (line.ItemId is not int itemId || !itemsById.TryGetValue(itemId, out var item) || item.CostPrice is not decimal cost)
                continue;

            var employeeId = line.AssignedToEmployeeId ?? customerEmployeeId;
            var positionMultiplier = employeeId is int empId ? await ResolvePositionMultiplierAsync(empId) : 1.0m;

            var grossProfit = (line.UnitPrice - cost) * line.Quantity;
            var slab = slabs.FirstOrDefault(s => grossProfit >= s.GrossProfitFrom && (s.GrossProfitTo is null || grossProfit <= s.GrossProfitTo));
            var isEligible = slab is not null && slab.CommissionAdditionPercent > 0;

            var record = new SalesCommissionRecord
            {
                // Navigation refs, not raw FK ints: CreateAndPostAsync builds a brand-new invoice and
                // lines that don't have real ids until this same transaction's SaveChanges runs — EF's
                // change-tracker fixes up the FK from the tracked reference regardless of save order.
                SalesInvoice = invoice,
                SalesInvoiceLine = line,
                EmployeeId = employeeId,
                AgentId = agentId,
                GrossProfit = grossProfit,
                IsEligible = isEligible,
                CalculatedAtUtc = nowUtc,
            };

            if (isEligible)
            {
                var categoryPercent = item.CategoryId is int catId
                    ? categoryRates.FirstOrDefault(r => r.ItemCategoryId == catId)?.BaseCommissionPercent ?? 0
                    : 0;
                var rate = (categoryPercent + slab!.CommissionAdditionPercent) / 100m;

                record.AgentCommission = agentId is not null ? grossProfit * rate : 0;
                record.AdjustedProfit = grossProfit - record.AgentCommission;
                record.EmployeeCommission = employeeId is not null ? record.AdjustedProfit * rate * positionMultiplier : 0;
            }
            else
            {
                record.AdjustedProfit = grossProfit;
            }

            db.SalesCommissionRecords.Add(record);
        }
    }

    /// <summary>Posted-line commission calculations for invoices dated within [<paramref name="from"/>,
    /// <paramref name="to"/>], independently filterable by Employee and Agent. Splitting into
    /// "Before cut-off" / "After cut-off" blocks per Section 7.2 is done by the caller — each row
    /// carries <see cref="CommissionReportRow.CustomerId"/> so the UI can pair it against
    /// <see cref="CustomerService.GetAssignmentHistoryAsync"/>.</summary>
    public async Task<List<CommissionReportRow>> GetCommissionReportAsync(DateOnly from, DateOnly to, int? employeeId = null, int? agentId = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.SalesCommissionRecords.AsNoTracking()
            .Include(r => r.SalesInvoice).Include(r => r.SalesInvoiceLine).ThenInclude(l => l.Item)
            .Include(r => r.Employee).Include(r => r.Agent)
            .Where(r => r.SalesInvoice.Date >= from && r.SalesInvoice.Date <= to)
            .AsQueryable();
        if (employeeId is int e) q = q.Where(r => r.EmployeeId == e);
        if (agentId is int a) q = q.Where(r => r.AgentId == a);

        var records = await q.OrderBy(r => r.SalesInvoice.Date).ThenBy(r => r.SalesInvoice.InvoiceNo).ToListAsync();

        // Customer.Name isn't reachable via a single Include chain above (SalesInvoice.Customer isn't
        // loaded), so it's joined in memory the same way TransactionService joins side-tables.
        var customerIds = records.Select(r => r.SalesInvoice.CustomerId).Distinct().ToList();
        var customers = await db.Customers.AsNoTracking()
            .Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id);

        return records.Select(r => new CommissionReportRow(
            r.Id, r.SalesInvoice.CustomerId, r.SalesInvoice.Date, r.SalesInvoice.InvoiceNo,
            customers.GetValueOrDefault(r.SalesInvoice.CustomerId)?.Name ?? "—",
            r.SalesInvoiceLine.Item?.Name, r.GrossProfit, r.IsEligible,
            r.EmployeeId, r.Employee?.FullName, r.AgentId, r.Agent?.Name,
            r.AgentCommission, r.AdjustedProfit, r.EmployeeCommission)).ToList();
    }

    /// <summary>Every customer Employee/Agent reassignment, newest first, each paired with the
    /// assignment it replaced — see Section 7.3 of the Commission Workflow spec.</summary>
    public async Task<List<ReassignmentAuditRow>> GetReassignmentAuditLogAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var history = await db.CustomerAssignmentHistories.AsNoTracking()
            .Include(h => h.Customer).Include(h => h.Employee).Include(h => h.Agent)
            .OrderBy(h => h.CustomerId).ThenBy(h => h.EffectiveFrom).ThenBy(h => h.Id)
            .ToListAsync();

        var rows = new List<ReassignmentAuditRow>();
        CustomerAssignmentHistory? previous = null;
        foreach (var h in history)
        {
            if (previous is not null && previous.CustomerId != h.CustomerId)
                previous = null; // first row seen for this customer — nothing to pair against

            rows.Add(new ReassignmentAuditRow(
                h.Id, h.Customer.Code, h.Customer.Name, h.EffectiveFrom, h.ChangedBy, h.ChangedAtUtc,
                previous?.Employee?.FullName, previous?.Agent?.Name,
                h.Employee?.FullName, h.Agent?.Name));

            previous = h;
        }
        return rows.OrderByDescending(r => r.ChangedAtUtc).ToList();
    }
}
