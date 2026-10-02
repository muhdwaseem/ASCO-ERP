namespace AegisErp.Domain.Entities;

/// <summary>
/// A reusable bundle of catalog <see cref="Item"/>s (e.g. "Starter Office Kit") with its own code,
/// so it can be picked on a Sales Invoice like any other item. Unlike <see cref="ServiceKit"/>
/// (free-text PRO-service fee-line templates), every line here is a real Item with a quantity —
/// picking a kit doesn't post as one line itself; it expands into one invoice line per component
/// item (see <c>NewSalesInvoiceDialog.InsertItemKit</c>), each carrying that item's own price,
/// revenue account and tax code exactly as if it had been picked individually.
/// </summary>
public class ItemKit : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    public List<ItemKitLine> Lines { get; set; } = new();
}

/// <summary>One component of an <see cref="ItemKit"/> — a catalog item plus how many of it the kit
/// contains.</summary>
public class ItemKitLine
{
    public int Id { get; set; }

    public int ItemKitId { get; set; }
    public ItemKit ItemKit { get; set; } = null!;

    public int SortOrder { get; set; }

    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;

    public decimal Quantity { get; set; } = 1;
}
