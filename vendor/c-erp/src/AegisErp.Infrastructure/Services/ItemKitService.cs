using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>One component line of an Item Kit being created/edited — an item plus how many of it.</summary>
public record ItemKitLineInput(int ItemId, decimal Quantity);

/// <summary>
/// Admin CRUD for Item Kits (reusable bundles of catalog Items with their own code) and their
/// lines — a kit is defined once here, then "Insert Kit" on Sales Invoice expands its lines onto
/// the document being edited, one invoice line per component item.
/// </summary>
public class ItemKitService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;
    public ItemKitService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    public async Task<List<ItemKit>> GetAllAsync(bool activeOnly = false)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.ItemKits.AsNoTracking()
            .Include(k => k.Lines).ThenInclude(l => l.Item)
            .OrderBy(k => k.Code).AsQueryable();
        if (activeOnly) q = q.Where(k => k.IsActive);
        return await q.ToListAsync();
    }

    /// <summary>The next kit code that will be assigned (for display on the New form).</summary>
    public async Task<string> PeekNextCodeAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var codes = await db.ItemKits.Select(k => k.Code).ToListAsync();
        return $"KIT-{NextSuffix(codes):0000}";
    }

    private static int NextSuffix(List<string> codes)
    {
        var max = 0;
        foreach (var code in codes)
            if (code.StartsWith("KIT-") && int.TryParse(code.AsSpan(4), out var n) && n > max)
                max = n;
        return max + 1;
    }

    public async Task<ItemKit> CreateAsync(string name, IEnumerable<ItemKitLineInput>? lines = null)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new PostingException("Kit name is required.");

        await using var db = await _dbf.CreateDbContextAsync();
        var codes = await db.ItemKits.Select(k => k.Code).ToListAsync();

        var kit = new ItemKit { Code = $"KIT-{NextSuffix(codes):0000}", Name = name };
        foreach (var l in await BuildLinesAsync(db, lines ?? Enumerable.Empty<ItemKitLineInput>()))
            kit.Lines.Add(l);
        if (kit.Lines.Count == 0) throw new PostingException("Kit needs at least one item.");

        db.ItemKits.Add(kit);
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return kit;
    }

    /// <summary>Updates the kit's name and fully replaces its lines with what's submitted.</summary>
    public async Task UpdateAsync(int id, string name, IEnumerable<ItemKitLineInput>? lines = null)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new PostingException("Kit name is required.");

        await using var db = await _dbf.CreateDbContextAsync();
        var kit = await db.ItemKits.Include(k => k.Lines).FirstOrDefaultAsync(k => k.Id == id)
            ?? throw new PostingException("Kit not found.");

        kit.Name = name;
        kit.Lines.Clear();
        foreach (var l in await BuildLinesAsync(db, lines ?? Enumerable.Empty<ItemKitLineInput>()))
            kit.Lines.Add(l);
        if (kit.Lines.Count == 0) throw new PostingException("Kit needs at least one item.");

        await JournalPoster.SaveChangesTranslatedAsync(db);
    }

    private static async Task<List<ItemKitLine>> BuildLinesAsync(AegisDbContext db, IEnumerable<ItemKitLineInput> inputs)
    {
        var list = new List<ItemKitLine>();
        var no = 0;
        foreach (var i in inputs)
        {
            if (i.ItemId == 0) continue; // a stray blank row — ignore, not an error
            if (i.Quantity <= 0) throw new PostingException("Each kit line's quantity must be positive.");
            if (!await db.Items.AnyAsync(it => it.Id == i.ItemId))
                throw new PostingException("One of the selected items was not found.");
            list.Add(new ItemKitLine { SortOrder = no++, ItemId = i.ItemId, Quantity = i.Quantity });
        }
        return list;
    }

    public async Task SetActiveAsync(int id, bool isActive)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var kit = await db.ItemKits.FirstOrDefaultAsync(k => k.Id == id)
            ?? throw new PostingException("Kit not found.");
        kit.IsActive = isActive;
        await db.SaveChangesAsync();
    }

    /// <summary>Deletes a kit and its lines. A kit is just a template — nothing else references it,
    /// since "Insert Kit" copies its lines onto the document rather than linking back to the kit.</summary>
    public async Task DeleteAsync(int id)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var kit = await db.ItemKits.FirstOrDefaultAsync(k => k.Id == id)
            ?? throw new PostingException("Kit not found.");
        db.ItemKits.Remove(kit);
        await db.SaveChangesAsync();
    }
}
