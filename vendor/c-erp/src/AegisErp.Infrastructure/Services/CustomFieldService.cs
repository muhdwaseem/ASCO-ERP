using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Everything the admin "define a custom field" form collects.</summary>
public record NewCustomFieldDefinitionInput(
    string Module, string Label, CustomFieldType FieldType, string? DropdownOptionsCsv, bool IsRequired, int SortOrder);

/// <summary>
/// Admin CRUD for <see cref="CustomFieldDefinition"/> — the field is defined once here, then
/// rendered dynamically on whichever module's form reads it (only the Customer form does, today).
/// </summary>
public class CustomFieldService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;
    public CustomFieldService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    public async Task<List<CustomFieldDefinition>> GetDefinitionsAsync(string module, bool activeOnly = false)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.CustomFieldDefinitions.AsNoTracking()
            .Where(f => f.Module == module).OrderBy(f => f.SortOrder).ThenBy(f => f.Label).AsQueryable();
        if (activeOnly) q = q.Where(f => f.IsActive);
        return await q.ToListAsync();
    }

    public async Task<CustomFieldDefinition> CreateDefinitionAsync(NewCustomFieldDefinitionInput input)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");
        Validate(input);
        await using var db = await _dbf.CreateDbContextAsync();
        var def = new CustomFieldDefinition
        {
            Module = input.Module.Trim(),
            Label = input.Label.Trim(),
            FieldType = input.FieldType,
            DropdownOptionsCsv = NormalizeOptions(input),
            IsRequired = input.IsRequired,
            SortOrder = input.SortOrder,
        };
        db.CustomFieldDefinitions.Add(def);
        await db.SaveChangesAsync();
        return def;
    }

    public async Task UpdateDefinitionAsync(int id, NewCustomFieldDefinitionInput input)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");
        Validate(input);
        await using var db = await _dbf.CreateDbContextAsync();
        var def = await db.CustomFieldDefinitions.FirstOrDefaultAsync(f => f.Id == id)
            ?? throw new PostingException("Custom field not found.");
        def.Label = input.Label.Trim();
        def.FieldType = input.FieldType;
        def.DropdownOptionsCsv = NormalizeOptions(input);
        def.IsRequired = input.IsRequired;
        def.SortOrder = input.SortOrder;
        await db.SaveChangesAsync();
    }

    public async Task SetActiveAsync(int id, bool isActive)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var def = await db.CustomFieldDefinitions.FirstOrDefaultAsync(f => f.Id == id)
            ?? throw new PostingException("Custom field not found.");
        def.IsActive = isActive;
        await db.SaveChangesAsync();
    }

    /// <summary>Refuses to delete a field that already has values recorded against a customer or
    /// vendor — deactivate it instead so that data isn't silently lost.</summary>
    public async Task DeleteAsync(int id)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var def = await db.CustomFieldDefinitions.FirstOrDefaultAsync(f => f.Id == id)
            ?? throw new PostingException("Custom field not found.");
        if (await db.CustomerCustomFieldValues.AnyAsync(v => v.CustomFieldDefinitionId == id))
            throw new PostingException("This field has values recorded against customers — deactivate it instead of deleting.");
        if (await db.VendorCustomFieldValues.AnyAsync(v => v.CustomFieldDefinitionId == id))
            throw new PostingException("This field has values recorded against vendors — deactivate it instead of deleting.");
        db.CustomFieldDefinitions.Remove(def);
        await db.SaveChangesAsync();
    }

    private static void Validate(NewCustomFieldDefinitionInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Label)) throw new PostingException("Field label is required.");
        if (input.FieldType == CustomFieldType.Dropdown && string.IsNullOrWhiteSpace(input.DropdownOptionsCsv))
            throw new PostingException("A dropdown field needs at least one option.");
    }

    private static string? NormalizeOptions(NewCustomFieldDefinitionInput input) =>
        input.FieldType == CustomFieldType.Dropdown
            ? string.Join(", ", input.DropdownOptionsCsv!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : null;
}
