namespace AegisErp.Domain.Entities;

/// <summary>A chart-of-accounts account. Non-postable accounts act as headers/roll-ups.</summary>
public class Account : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>Owning company. Each company keeps its own chart of accounts.</summary>
    public int CompanyId { get; set; }

    /// <summary>Human-facing account number, e.g. "11020". Unique within the company.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public AccountType Type { get; set; }

    /// <summary>False for header accounts that only aggregate children.</summary>
    public bool IsPostable { get; set; } = true;

    public int? ParentId { get; set; }
    public Account? Parent { get; set; }

    /// <summary>Optional sub-classification, e.g. "Cash and cash equivalents", "Accounts receivable".</summary>
    public string? Category { get; set; }

    /// <summary>Which section of the P&amp;L this Income/Expense account rolls into (null for
    /// Asset/Liability/Equity accounts, where it has no meaning).</summary>
    public PnlSection? PnlSection { get; set; }

    /// <summary>Account currency. AED-only for now (stored, no FX logic).</summary>
    public string Currency { get; set; } = "AED";

    /// <summary>Optional free-text notes.</summary>
    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;

    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    /// <summary>Optimistic-concurrency token. Regenerated on every update; the save fails if the
    /// value the editor started from no longer matches what's in the database.</summary>
    public Guid RowVersion { get; set; } = Guid.NewGuid();

    public NormalBalance NormalBalance => Type.NormalBalance();

    public ICollection<JournalLine> Lines { get; set; } = new List<JournalLine>();
}
