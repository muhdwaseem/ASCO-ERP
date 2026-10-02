using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

public record LeadInput(
    string Name, string? CompanyName, string? Mobile, string? Email, string? Source,
    decimal? EstimatedValue, string? AssignedTo, string? Notes);

public class LeadService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly CustomerService _customers;
    private readonly ICurrentCompany _current;
    public LeadService(IDbContextFactory<AegisDbContext> dbf, CustomerService customers, ICurrentCompany current)
    {
        _dbf = dbf;
        _customers = customers;
        _current = current;
    }

    public async Task<List<Lead>> GetAllAsync(LeadStage? stage = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var query = db.Leads.AsNoTracking().Include(l => l.ConvertedCustomer).AsQueryable();
        if (stage is not null) query = query.Where(l => l.Stage == stage);
        return await query
            .OrderByDescending(l => l.LastActivityAtUtc ?? l.CreatedAtUtc)
            .ToListAsync();
    }

    public async Task<Lead?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Leads.AsNoTracking()
            .Include(l => l.ConvertedCustomer)
            .Include(l => l.Activities)
            .FirstOrDefaultAsync(l => l.Id == id);
    }

    public async Task<Lead> CreateAsync(LeadInput input, string createdBy, DateTime nowUtc)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        ValidateInput(input);

        await using var db = await _dbf.CreateDbContextAsync();
        var lead = new Lead { CreatedBy = createdBy, CreatedAtUtc = nowUtc };
        ApplyInput(lead, input);

        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        return lead;
    }

    public async Task UpdateAsync(int id, LeadInput input)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        ValidateInput(input);

        await using var db = await _dbf.CreateDbContextAsync();
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == id)
            ?? throw new PostingException("Lead not found.");
        if (lead.Stage is LeadStage.Won or LeadStage.Lost)
            throw new PostingException("A Won or Lost lead can no longer be edited.");
        ApplyInput(lead, input);
        await db.SaveChangesAsync();
    }

    /// <summary>Moves a lead to a new stage. Use <see cref="LeadService.ConvertToCustomerAsync"/>
    /// (in the caller's own transaction) rather than this to reach Won — that also creates the
    /// real Customer record; calling this directly with Won would leave the lead unconverted.</summary>
    public async Task ChangeStageAsync(int id, LeadStage stage)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        await using var db = await _dbf.CreateDbContextAsync();
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == id)
            ?? throw new PostingException("Lead not found.");
        if (lead.Stage is LeadStage.Won or LeadStage.Lost)
            throw new PostingException("A Won or Lost lead's stage can no longer be changed.");
        if (stage == LeadStage.Won)
            throw new PostingException("Use Convert to Customer to move a lead to Won.");

        lead.Stage = stage;
        await db.SaveChangesAsync();
    }

    public async Task LogActivityAsync(int leadId, LeadActivityType type, string description, DateOnly activityDate, string createdBy, DateTime nowUtc)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        if (string.IsNullOrWhiteSpace(description)) throw new PostingException("Activity description is required.");

        await using var db = await _dbf.CreateDbContextAsync();
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == leadId)
            ?? throw new PostingException("Lead not found.");

        lead.Activities.Add(new LeadActivity
        {
            Type = type,
            Description = description.Trim(),
            ActivityDate = activityDate,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
        });
        lead.LastActivityAtUtc = nowUtc;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Converts a lead into a real Customer via the existing <see cref="CustomerService.CreateAsync"/>
    /// — mirrors <c>EstimateService.ConvertToInvoiceAsync</c>'s "one document produces another,
    /// linked by FK" shape. Moves the lead to Won and links <see cref="Lead.ConvertedCustomerId"/>;
    /// no GL impact here — accounting only starts once the new Customer goes through the normal
    /// Estimate/Sales Invoice flow.
    /// </summary>
    public async Task<Customer> ConvertToCustomerAsync(int leadId, string createdBy, DateTime nowUtc)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        await using var db = await _dbf.CreateDbContextAsync();
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == leadId)
            ?? throw new PostingException("Lead not found.");
        if (lead.ConvertedCustomerId is not null)
            throw new PostingException("This lead has already been converted.");
        if (lead.Stage is LeadStage.Won or LeadStage.Lost)
            throw new PostingException("A Won or Lost lead can no longer be converted.");

        var input = new NewCustomerInput(
            Name: lead.CompanyName ?? lead.Name,
            Group: null, Currency: "AED", CreditLimit: 0, PaymentTermsDays: 30,
            Trn: null, Email: lead.Email, Phone: lead.Mobile, Address: null,
            Salesperson: lead.AssignedTo);
        var customer = await _customers.CreateAsync(input, changedBy: createdBy, nowUtc: nowUtc);

        lead.ConvertedCustomerId = customer.Id;
        lead.Stage = LeadStage.Won;
        await db.SaveChangesAsync();
        return customer;
    }

    private static void ValidateInput(LeadInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) throw new PostingException("Lead name is required.");
        if (input.EstimatedValue is < 0) throw new PostingException("Estimated value cannot be negative.");
    }

    private static void ApplyInput(Lead lead, LeadInput input)
    {
        lead.Name = input.Name.Trim();
        lead.CompanyName = string.IsNullOrWhiteSpace(input.CompanyName) ? null : input.CompanyName.Trim();
        lead.Mobile = string.IsNullOrWhiteSpace(input.Mobile) ? null : input.Mobile.Trim();
        lead.Email = string.IsNullOrWhiteSpace(input.Email) ? null : input.Email.Trim();
        lead.Source = string.IsNullOrWhiteSpace(input.Source) ? null : input.Source.Trim();
        lead.EstimatedValue = input.EstimatedValue;
        lead.AssignedTo = string.IsNullOrWhiteSpace(input.AssignedTo) ? null : input.AssignedTo.Trim();
        lead.Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
    }
}
