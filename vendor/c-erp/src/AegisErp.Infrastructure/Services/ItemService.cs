using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Everything the "New Item" form collects.</summary>
public record NewItemInput(
    string Name, ItemKind Kind, string Unit,
    decimal SellingPrice, int? SalesAccountId, string? SalesDescription,
    decimal? CostPrice, int? PurchaseAccountId, string? PurchaseDescription,
    int? TaxCodeId, int? CategoryId = null);

public class ItemService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;
    public ItemService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    public async Task<List<Item>> GetAllAsync(bool activeOnly = false)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.Items.AsNoTracking().Include(i => i.SalesAccount).Include(i => i.PurchaseAccount)
            .Include(i => i.TaxCode).Include(i => i.Category).OrderBy(i => i.Code).AsQueryable();
        if (activeOnly) q = q.Where(i => i.IsActive);
        return await q.ToListAsync();
    }

    /// <summary>The next item code that will be assigned (for display on the New form).</summary>
    public async Task<string> PeekNextCodeAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var codes = await db.Items.Select(i => i.Code).ToListAsync();
        return $"ITM-{NextSuffix(codes):0000}";
    }

    private static int NextSuffix(List<string> codes)
    {
        var max = 0;
        foreach (var code in codes)
            if (code.StartsWith("ITM-") && int.TryParse(code.AsSpan(4), out var n) && n > max)
                max = n;
        return max + 1;
    }

    // Items.razor gates New/Edit/Delete/(de)activate behind Session.CanPost, not CanAdminister —
    // nothing here enforced it server-side until now.
    public async Task<Item> CreateAsync(NewItemInput input)
    {
        if (!_current.CanPost)
            throw new PostingException("You don't have permission to do this.");

        var name = input.Name.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new PostingException("Item name is required.");
        if (input.SellingPrice < 0) throw new PostingException("Selling price cannot be negative.");
        if (input.CostPrice is < 0) throw new PostingException("Cost price cannot be negative.");

        await using var db = await _dbf.CreateDbContextAsync();
        var codes = await db.Items.Select(i => i.Code).ToListAsync();

        var item = new Item
        {
            Code = $"ITM-{NextSuffix(codes):0000}",
            Name = name,
            Kind = input.Kind,
            Unit = string.IsNullOrWhiteSpace(input.Unit) ? "unit" : input.Unit.Trim(),
            SellingPrice = input.SellingPrice,
            SalesAccountId = input.SalesAccountId,
            SalesDescription = string.IsNullOrWhiteSpace(input.SalesDescription) ? null : input.SalesDescription.Trim(),
            CostPrice = input.CostPrice,
            PurchaseAccountId = input.PurchaseAccountId,
            PurchaseDescription = string.IsNullOrWhiteSpace(input.PurchaseDescription) ? null : input.PurchaseDescription.Trim(),
            TaxCodeId = input.TaxCodeId,
            CategoryId = input.CategoryId,
        };
        db.Items.Add(item);
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return item;
    }

    public async Task UpdateAsync(int id, NewItemInput input)
    {
        if (!_current.CanPost)
            throw new PostingException("You don't have permission to do this.");

        var name = input.Name.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new PostingException("Item name is required.");
        if (input.SellingPrice < 0) throw new PostingException("Selling price cannot be negative.");
        if (input.CostPrice is < 0) throw new PostingException("Cost price cannot be negative.");

        await using var db = await _dbf.CreateDbContextAsync();
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id)
            ?? throw new PostingException("Item not found.");

        item.Name = name;
        item.Kind = input.Kind;
        item.Unit = string.IsNullOrWhiteSpace(input.Unit) ? "unit" : input.Unit.Trim();
        item.SellingPrice = input.SellingPrice;
        item.SalesAccountId = input.SalesAccountId;
        item.SalesDescription = string.IsNullOrWhiteSpace(input.SalesDescription) ? null : input.SalesDescription.Trim();
        item.CostPrice = input.CostPrice;
        item.PurchaseAccountId = input.PurchaseAccountId;
        item.PurchaseDescription = string.IsNullOrWhiteSpace(input.PurchaseDescription) ? null : input.PurchaseDescription.Trim();
        item.TaxCodeId = input.TaxCodeId;
        item.CategoryId = input.CategoryId;
        await JournalPoster.SaveChangesTranslatedAsync(db);
    }

    public async Task SetActiveAsync(int id, bool isActive)
    {
        if (!_current.CanPost)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id)
            ?? throw new PostingException("Item not found.");
        item.IsActive = isActive;
        await JournalPoster.SaveChangesTranslatedAsync(db);
    }

    public async Task DeleteAsync(int id)
    {
        if (!_current.CanPost)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id)
            ?? throw new PostingException("Item not found.");
        db.Items.Remove(item);
        await db.SaveChangesAsync();
    }

    /// <summary>The fixed list every company effectively had before units became an editable
    /// per-company list — used only to backfill a company that has none yet (new or pre-existing),
    /// so nobody sees an empty Unit dropdown where these used to always be available.</summary>
    private static readonly string[] DefaultUnitNames = { "unit", "hrs", "pcs", "box", "kg", "ltr", "day", "month" };

    /// <summary>Units of measure offered on the Item form's Unit picker — see <see cref="UnitOfMeasure"/>.
    /// Backfills <see cref="DefaultUnitNames"/> the first time a company has none, so this is never
    /// empty for a company that simply hasn't added its own units yet.</summary>
    public async Task<List<UnitOfMeasure>> GetUnitsAsync(bool activeOnly = false)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        if (!await db.UnitsOfMeasure.AnyAsync())
        {
            db.UnitsOfMeasure.AddRange(DefaultUnitNames.Select(n => new UnitOfMeasure { Name = n }));
            await db.SaveChangesAsync();
        }

        var q = db.UnitsOfMeasure.AsNoTracking().OrderBy(u => u.Name).AsQueryable();
        if (activeOnly) q = q.Where(u => u.IsActive);
        return await q.ToListAsync();
    }

    // UnitsPage.razor gates itself behind Session.CanAdminister (distinct from Items above).
    public async Task<UnitOfMeasure> CreateUnitAsync(string name)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new PostingException("Unit name is required.");

        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.UnitsOfMeasure.AnyAsync(u => u.Name.ToLower() == name.ToLower()))
            throw new PostingException($"A unit named '{name}' already exists.");

        var unit = new UnitOfMeasure { Name = name };
        db.UnitsOfMeasure.Add(unit);
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return unit;
    }

    public async Task SetUnitActiveAsync(int id, bool isActive)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var unit = await db.UnitsOfMeasure.FirstOrDefaultAsync(u => u.Id == id)
            ?? throw new PostingException("Unit not found.");
        unit.IsActive = isActive;
        await db.SaveChangesAsync();
    }

    /// <summary>Deletes a unit, unless some item still uses its name (deactivate instead).</summary>
    public async Task DeleteUnitAsync(int id)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var unit = await db.UnitsOfMeasure.FirstOrDefaultAsync(u => u.Id == id)
            ?? throw new PostingException("Unit not found.");
        if (await db.Items.AnyAsync(i => i.Unit == unit.Name))
            throw new PostingException("This unit is used by one or more items and cannot be deleted — deactivate it instead.");
        db.UnitsOfMeasure.Remove(unit);
        await db.SaveChangesAsync();
    }

    /// <summary>Categories offered on the Item form's Category picker, and the key the Commission
    /// Workflow's category rate table maps a base commission percentage onto — see
    /// <see cref="ItemCategory"/>. Unlike <see cref="GetUnitsAsync"/> this starts empty; there's no
    /// fixed default list to backfill.</summary>
    public async Task<List<ItemCategory>> GetCategoriesAsync(bool activeOnly = false)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.ItemCategories.AsNoTracking().OrderBy(c => c.Name).AsQueryable();
        if (activeOnly) q = q.Where(c => c.IsActive);
        return await q.ToListAsync();
    }

    // Mirrors CreateUnitAsync — ItemsPage.razor gates this behind Session.CanAdminister.
    public async Task<ItemCategory> CreateCategoryAsync(string name)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new PostingException("Category name is required.");

        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.ItemCategories.AnyAsync(c => c.Name.ToLower() == name.ToLower()))
            throw new PostingException($"A category named '{name}' already exists.");

        var category = new ItemCategory { Name = name };
        db.ItemCategories.Add(category);
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return category;
    }

    public async Task SetCategoryActiveAsync(int id, bool isActive)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var category = await db.ItemCategories.FirstOrDefaultAsync(c => c.Id == id)
            ?? throw new PostingException("Category not found.");
        category.IsActive = isActive;
        await db.SaveChangesAsync();
    }

    /// <summary>Deletes a category, unless some item still uses it (deactivate instead).</summary>
    public async Task DeleteCategoryAsync(int id)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var category = await db.ItemCategories.FirstOrDefaultAsync(c => c.Id == id)
            ?? throw new PostingException("Category not found.");
        if (await db.Items.AnyAsync(i => i.CategoryId == id))
            throw new PostingException("This category is used by one or more items and cannot be deleted — deactivate it instead.");
        db.ItemCategories.Remove(category);
        await db.SaveChangesAsync();
    }
}
