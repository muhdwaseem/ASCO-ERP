using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Everything the "New Agent" / "Edit Agent" form collects.</summary>
public record AgentInput(
    string Name, string? Phone, string? Email, string? Address,
    string? BankName, string? Iban, int? ReportsToEmployeeId);

public class AgentService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;

    public AgentService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    public async Task<List<Agent>> GetAllAsync(bool activeOnly = false)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.Agents.AsNoTracking().Include(a => a.ReportsToEmployee)
            .OrderBy(a => a.AgentCode).AsQueryable();
        if (activeOnly) q = q.Where(a => a.Status == AgentStatus.Active);
        return await q.ToListAsync();
    }

    /// <summary>The next agent code that will be assigned (for display on the New form).</summary>
    public async Task<string> PeekNextCodeAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var codes = await db.Agents.Select(a => a.AgentCode).ToListAsync();
        var max = 0;
        foreach (var code in codes)
            if (code.StartsWith("AGT-") && int.TryParse(code.AsSpan(4), out var n) && n > max)
                max = n;
        return $"AGT-{max + 1:0000}";
    }

    public async Task<Agent?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Agents.AsNoTracking().Include(a => a.ReportsToEmployee)
            .FirstOrDefaultAsync(a => a.Id == id);
    }

    public async Task<Agent> CreateAsync(AgentInput input)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        ValidateInput(input);

        await using var db = await _dbf.CreateDbContextAsync();
        var codes = await db.Agents.Select(a => a.AgentCode).ToListAsync();
        var max = 0;
        foreach (var code in codes)
            if (code.StartsWith("AGT-") && int.TryParse(code.AsSpan(4), out var n) && n > max)
                max = n;

        var agent = new Agent { AgentCode = $"AGT-{max + 1:0000}" };
        ApplyInput(agent, input);

        db.Agents.Add(agent);
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return agent;
    }

    public async Task UpdateAsync(int id, AgentInput input)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        ValidateInput(input);

        await using var db = await _dbf.CreateDbContextAsync();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id)
            ?? throw new PostingException("Agent not found.");

        ApplyInput(agent, input);
        await JournalPoster.SaveChangesTranslatedAsync(db);
    }

    public async Task SetStatusAsync(int id, AgentStatus status)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id)
            ?? throw new PostingException("Agent not found.");
        agent.Status = status;
        await JournalPoster.SaveChangesTranslatedAsync(db);
    }

    private static void ValidateInput(AgentInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) throw new PostingException("Agent name is required.");
    }

    private static void ApplyInput(Agent agent, AgentInput input)
    {
        agent.Name = input.Name.Trim();
        agent.Phone = string.IsNullOrWhiteSpace(input.Phone) ? null : input.Phone.Trim();
        agent.Email = string.IsNullOrWhiteSpace(input.Email) ? null : input.Email.Trim();
        agent.Address = string.IsNullOrWhiteSpace(input.Address) ? null : input.Address.Trim();
        agent.BankName = string.IsNullOrWhiteSpace(input.BankName) ? null : input.BankName.Trim();
        agent.Iban = string.IsNullOrWhiteSpace(input.Iban) ? null : input.Iban.Trim();
        // Null ReportsToEmployeeId means "Direct to Company" — no validation needed beyond the FK
        // constraint itself, since Employee is company-scoped like Agent.
        agent.ReportsToEmployeeId = input.ReportsToEmployeeId;
    }
}
