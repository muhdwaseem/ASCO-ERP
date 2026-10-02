using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

public record NewTagGroupInput(string Module, string Name, int SortOrder);
public record NewTagInput(string Name, int SortOrder);

/// <summary>
/// Admin CRUD for Tag Groups and their Tags (Zoho's "Reporting Tags") — a group is defined once
/// here, then a customer may pick at most one tag from each active group.
/// </summary>
public class TagService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;
    public TagService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    public async Task<List<TagGroup>> GetGroupsAsync(string module, bool activeOnly = false)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.TagGroups.AsNoTracking().Include(g => g.Tags)
            .Where(g => g.Module == module).OrderBy(g => g.SortOrder).ThenBy(g => g.Name).AsQueryable();
        if (activeOnly) q = q.Where(g => g.IsActive);
        return await q.ToListAsync();
    }

    public async Task<TagGroup> CreateGroupAsync(NewTagGroupInput input)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");
        if (string.IsNullOrWhiteSpace(input.Name)) throw new PostingException("Tag group name is required.");
        await using var db = await _dbf.CreateDbContextAsync();
        var group = new TagGroup { Module = input.Module.Trim(), Name = input.Name.Trim(), SortOrder = input.SortOrder };
        db.TagGroups.Add(group);
        await db.SaveChangesAsync();
        return group;
    }

    public async Task UpdateGroupAsync(int id, string name, int sortOrder)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");
        if (string.IsNullOrWhiteSpace(name)) throw new PostingException("Tag group name is required.");
        await using var db = await _dbf.CreateDbContextAsync();
        var group = await db.TagGroups.FirstOrDefaultAsync(g => g.Id == id) ?? throw new PostingException("Tag group not found.");
        group.Name = name.Trim();
        group.SortOrder = sortOrder;
        await db.SaveChangesAsync();
    }

    public async Task SetGroupActiveAsync(int id, bool isActive)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var group = await db.TagGroups.FirstOrDefaultAsync(g => g.Id == id) ?? throw new PostingException("Tag group not found.");
        group.IsActive = isActive;
        await db.SaveChangesAsync();
    }

    /// <summary>Refuses to delete a group with any recorded customer or vendor selections —
    /// deactivate instead.</summary>
    public async Task DeleteGroupAsync(int id)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var group = await db.TagGroups.FirstOrDefaultAsync(g => g.Id == id) ?? throw new PostingException("Tag group not found.");
        if (await db.CustomerTags.AnyAsync(t => t.Tag.TagGroupId == id))
            throw new PostingException("This tag group has selections recorded against customers — deactivate it instead of deleting.");
        if (await db.VendorTags.AnyAsync(t => t.Tag.TagGroupId == id))
            throw new PostingException("This tag group has selections recorded against vendors — deactivate it instead of deleting.");
        db.TagGroups.Remove(group); // cascades to its Tags
        await db.SaveChangesAsync();
    }

    public async Task<Tag> AddTagAsync(int tagGroupId, NewTagInput input)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");
        if (string.IsNullOrWhiteSpace(input.Name)) throw new PostingException("Tag name is required.");
        await using var db = await _dbf.CreateDbContextAsync();
        _ = await db.TagGroups.FindAsync(tagGroupId) ?? throw new PostingException("Tag group not found.");
        var tag = new Tag { TagGroupId = tagGroupId, Name = input.Name.Trim(), SortOrder = input.SortOrder };
        db.Tags.Add(tag);
        await db.SaveChangesAsync();
        return tag;
    }

    public async Task UpdateTagAsync(int id, NewTagInput input)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");
        if (string.IsNullOrWhiteSpace(input.Name)) throw new PostingException("Tag name is required.");
        await using var db = await _dbf.CreateDbContextAsync();
        var tag = await FindTagAsync(db, id) ?? throw new PostingException("Tag not found.");
        tag.Name = input.Name.Trim();
        tag.SortOrder = input.SortOrder;
        await db.SaveChangesAsync();
    }

    public async Task SetTagActiveAsync(int id, bool isActive)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var tag = await FindTagAsync(db, id) ?? throw new PostingException("Tag not found.");
        tag.IsActive = isActive;
        await db.SaveChangesAsync();
    }

    /// <summary>Refuses to delete a tag already selected on at least one customer or vendor —
    /// deactivate instead.</summary>
    public async Task DeleteTagAsync(int id)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var tag = await FindTagAsync(db, id) ?? throw new PostingException("Tag not found.");
        if (await db.CustomerTags.AnyAsync(t => t.TagId == id))
            throw new PostingException("This tag is selected on at least one customer — deactivate it instead of deleting.");
        if (await db.VendorTags.AnyAsync(t => t.TagId == id))
            throw new PostingException("This tag is selected on at least one vendor — deactivate it instead of deleting.");
        db.Tags.Remove(tag);
        await db.SaveChangesAsync();
    }

    /// <summary>Tag has no CompanyId/query filter of its own — route through the company-scoped
    /// TagGroups so a tagId belonging to another company is never found, let alone modified.</summary>
    private static Task<Tag?> FindTagAsync(AegisDbContext db, int id) =>
        db.TagGroups.SelectMany(g => g.Tags).FirstOrDefaultAsync(t => t.Id == id);
}
