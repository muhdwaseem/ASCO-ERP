using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Input line for creating an estimate / quotation from the UI.</summary>
public record EstimateLineInput(string Description, int RevenueAccountId, int? CostCenterId,
    decimal Quantity, decimal UnitPrice, decimal VatRate,
    decimal GovtFee = 0, decimal BankCharge = 0, int? AssignedToEmployeeId = null);

public class EstimateService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly SalesInvoiceService _invoices;
    private readonly ICurrentCompany _current;
    public EstimateService(IDbContextFactory<AegisDbContext> dbf, SalesInvoiceService invoices, ICurrentCompany current)
    {
        _dbf = dbf;
        _invoices = invoices;
        _current = current;
    }

    public async Task<List<Estimate>> GetRecentAsync(int take = 50)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Estimates.AsNoTracking()
            .Include(e => e.Customer).Include(e => e.Lines).Include(e => e.ConvertedInvoice).Include(e => e.Organization)
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.Id)
            .Take(take).ToListAsync();
    }

    /// <summary>One estimate with full detail, for the estimate detail view.</summary>
    public async Task<Estimate?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Estimates.AsNoTracking()
            .Include(e => e.Customer).Include(e => e.Lines).ThenInclude(l => l.AssignedToEmployee)
            .Include(e => e.ConvertedInvoice).Include(e => e.Organization)
            .FirstOrDefaultAsync(e => e.Id == id);
    }

    /// <summary>Creates a quotation (non-posting). Validity defaults to 30 days if not given.</summary>
    public async Task<Estimate> CreateAsync(
        int customerId, DateOnly date, DateOnly validUntil, string? narration,
        string createdBy, IEnumerable<EstimateLineInput> lines, DateTime nowUtc,
        int? organizationId = null, string? contactMobile = null, string? contactEmail = null,
        string? contactTrn = null, string? contactPerson = null, string? billingAddress = null,
        string? jobRequest = null)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        if (customerId == 0) throw new PostingException("Estimate has no customer.");
        var lineList = lines.ToList();
        if (lineList.Count == 0) throw new PostingException("Estimate needs at least one line.");
        if (validUntil < date) throw new PostingException("Valid-until date cannot be before the estimate date.");

        await using var db = await _dbf.CreateDbContextAsync();
        var customer = await db.Customers.FindAsync(customerId)
            ?? throw new PostingException("Customer not found.");

        var numbers = await db.Estimates.Select(e => e.EstimateNo).ToListAsync();

        var estimate = new Estimate
        {
            EstimateNo = JournalPoster.NextDocNo(numbers, "EST", date.Year),
            CustomerId = customerId,
            OrganizationId = organizationId,
            ContactMobile = contactMobile,
            ContactEmail = contactEmail,
            ContactTrn = contactTrn,
            ContactPerson = contactPerson,
            BillingAddressSnapshot = billingAddress,
            JobRequest = jobRequest,
            Date = date,
            ValidUntil = validUntil,
            Status = DocumentStatus.Draft,
            Narration = string.IsNullOrWhiteSpace(narration) ? $"Quotation — {customer.Name}" : narration,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
        };

        var no = 1;
        foreach (var l in lineList)
        {
            if (l.RevenueAccountId == 0) throw new PostingException($"Line {no} has no revenue account.");
            if (l.Quantity <= 0) throw new PostingException($"Line {no} quantity must be positive.");
            estimate.Lines.Add(new EstimateLine
            {
                LineNo = no++,
                Description = l.Description,
                RevenueAccountId = l.RevenueAccountId,
                CostCenterId = l.CostCenterId,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                VatRate = l.VatRate,
                GovtFee = l.GovtFee,
                BankCharge = l.BankCharge,
                AssignedToEmployeeId = l.AssignedToEmployeeId,
            });
        }

        db.Estimates.Add(estimate);
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return estimate;
    }

    /// <summary>
    /// Replaces a quotation's header fields and lines in place — the estimate number is untouched.
    /// Only valid while still Draft or Sent; once Accepted/Declined/Converted the terms are final.
    /// </summary>
    public async Task<Estimate> UpdateAsync(
        int estimateId, int customerId, DateOnly date, DateOnly validUntil, string? narration,
        IEnumerable<EstimateLineInput> lines,
        int? organizationId = null, string? contactMobile = null, string? contactEmail = null,
        string? contactTrn = null, string? contactPerson = null, string? billingAddress = null,
        string? jobRequest = null)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        if (customerId == 0) throw new PostingException("Estimate has no customer.");
        var lineList = lines.ToList();
        if (lineList.Count == 0) throw new PostingException("Estimate needs at least one line.");
        if (validUntil < date) throw new PostingException("Valid-until date cannot be before the estimate date.");

        await using var db = await _dbf.CreateDbContextAsync();
        var estimate = await db.Estimates.Include(e => e.Lines)
            .FirstOrDefaultAsync(e => e.Id == estimateId)
            ?? throw new PostingException("Estimate not found.");
        if (estimate.Status is DocumentStatus.Accepted or DocumentStatus.Declined or DocumentStatus.Converted)
            throw new PostingException("This quotation is no longer editable — its terms have been finalized.");

        var customer = await db.Customers.FindAsync(customerId)
            ?? throw new PostingException("Customer not found.");

        estimate.CustomerId = customerId;
        estimate.OrganizationId = organizationId;
        estimate.ContactMobile = contactMobile;
        estimate.ContactEmail = contactEmail;
        estimate.ContactTrn = contactTrn;
        estimate.ContactPerson = contactPerson;
        estimate.BillingAddressSnapshot = billingAddress;
        estimate.JobRequest = jobRequest;
        estimate.Date = date;
        estimate.ValidUntil = validUntil;
        estimate.Narration = string.IsNullOrWhiteSpace(narration) ? $"Quotation — {customer.Name}" : narration;

        estimate.Lines.Clear();
        var no = 1;
        foreach (var l in lineList)
        {
            if (l.RevenueAccountId == 0) throw new PostingException($"Line {no} has no revenue account.");
            if (l.Quantity <= 0) throw new PostingException($"Line {no} quantity must be positive.");
            estimate.Lines.Add(new EstimateLine
            {
                LineNo = no++,
                Description = l.Description,
                RevenueAccountId = l.RevenueAccountId,
                CostCenterId = l.CostCenterId,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                VatRate = l.VatRate,
                GovtFee = l.GovtFee,
                BankCharge = l.BankCharge,
                AssignedToEmployeeId = l.AssignedToEmployeeId,
            });
        }

        await JournalPoster.SaveChangesTranslatedAsync(db);
        return estimate;
    }

    /// <summary>Moves an estimate through its non-posting workflow (Draft → Sent → Accepted / Declined).</summary>
    public async Task SetStatusAsync(int estimateId, DocumentStatus status)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        await using var db = await _dbf.CreateDbContextAsync();
        var estimate = await db.Estimates.FirstOrDefaultAsync(e => e.Id == estimateId)
            ?? throw new PostingException("Estimate not found.");
        if (estimate.Status == DocumentStatus.Converted)
            throw new PostingException("A converted estimate can no longer change status.");
        estimate.Status = status;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Converts an accepted estimate into a posted sales invoice (which is what hits the GL), then
    /// marks the estimate Converted and links it to the new invoice.
    /// </summary>
    public async Task<SalesInvoice> ConvertToInvoiceAsync(int estimateId, int fiscalPeriodId, string createdBy, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var estimate = await db.Estimates.Include(e => e.Lines)
            .FirstOrDefaultAsync(e => e.Id == estimateId)
            ?? throw new PostingException("Estimate not found.");
        if (estimate.ConvertedInvoiceId is not null)
            throw new PostingException("This estimate has already been converted.");

        // GovtFee/BankCharge/AssignedToEmployeeId and the header snapshot fields must carry straight
        // through — otherwise a PRO-format estimate's non-taxable disbursements would silently fold
        // into the taxable UnitPrice on conversion and overcharge VAT on what should be a government
        // fee (and the task assignment would be lost on the invoice that actually gets billed).
        var inputs = estimate.Lines.OrderBy(l => l.LineNo).Select(l =>
            new InvoiceLineInput(l.Description, l.RevenueAccountId, l.CostCenterId, l.Quantity, l.UnitPrice, l.VatRate,
                GovtFee: l.GovtFee, BankCharge: l.BankCharge, AssignedToEmployeeId: l.AssignedToEmployeeId));

        var invoice = await _invoices.CreateAndPostAsync(
            estimate.CustomerId, DateOnly.FromDateTime(nowUtc.Date), fiscalPeriodId,
            $"Converted from {estimate.EstimateNo}", createdBy, inputs, nowUtc,
            organizationId: estimate.OrganizationId, contactMobile: estimate.ContactMobile,
            contactEmail: estimate.ContactEmail, contactTrn: estimate.ContactTrn,
            contactPerson: estimate.ContactPerson, billingAddress: estimate.BillingAddressSnapshot,
            jobRequest: estimate.JobRequest);

        estimate.Status = DocumentStatus.Converted;
        estimate.ConvertedInvoiceId = invoice.Id;
        await db.SaveChangesAsync();
        return invoice;
    }

    /// <summary>The token backing this quotation's public "Share" link — generated once, then stable.</summary>
    public async Task<string> GetOrCreateShareTokenAsync(int estimateId)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        await using var db = await _dbf.CreateDbContextAsync();
        var estimate = await db.Estimates.FirstOrDefaultAsync(e => e.Id == estimateId)
            ?? throw new PostingException("Estimate not found.");

        if (string.IsNullOrEmpty(estimate.ShareToken))
        {
            estimate.ShareToken = Guid.NewGuid().ToString("N");
            await db.SaveChangesAsync();
        }
        return estimate.ShareToken;
    }

    /// <summary>
    /// Looks up a quotation by its public share token, for the anonymous Share page. Returns null
    /// for an unknown/never-shared token so the page can show "not found" rather than leaking which
    /// tokens exist. Runs unscoped — the opaque token is the security boundary, not company scoping.
    /// </summary>
    public async Task<Estimate?> GetBySharedTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Estimates.AsNoTracking()
            .Include(e => e.Customer).Include(e => e.Lines).Include(e => e.Organization)
            .FirstOrDefaultAsync(e => e.ShareToken == token);
    }
}
