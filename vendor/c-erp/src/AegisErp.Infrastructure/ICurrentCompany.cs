namespace AegisErp.Infrastructure;

/// <summary>
/// The company (tenant) the current user is working in. The DbContext reads this to filter
/// every query, so it is the single point that enforces data isolation between companies.
/// </summary>
public interface ICurrentCompany
{
    /// <summary>Active company id, or null for an unscoped context (seeding / firm-wide admin queries).</summary>
    int? CompanyId { get; }

    /// <summary>True when the signed-in user may post/void/approve financial documents in the
    /// active company. Mirrors <c>CompanySession.CanPost</c> — the Web layer computes it from the
    /// user's per-company role, this just carries the answer down to the services that actually
    /// save a document, so the permission is enforced where it matters, not only in the UI.</summary>
    bool CanPost { get; }

    /// <summary>True when the signed-in user may administer the active company's settings and
    /// team. Mirrors <c>CompanySession.CanAdminister</c> — see <see cref="CanPost"/> for why this
    /// is carried down to services rather than checked only in the UI.</summary>
    bool CanAdminister { get; }

    /// <summary>True when the signed-in user administers the firm and therefore reaches every
    /// company — not company-scoped, unlike the two flags above. Used for actions that are firm-wide
    /// by nature (e.g. creating a new company) rather than a permission on the active company.</summary>
    bool IsFirmAdmin { get; }
}

/// <summary>
/// Scoped, mutable holder for the active company. The web layer sets it from the signed-in
/// user's claims when a circuit starts, and updates it when the user switches company.
/// </summary>
public class CurrentCompany : ICurrentCompany
{
    public int? CompanyId { get; set; }

    /// <summary>True when the signed-in user may act across all companies (firm administrator).</summary>
    public bool IsFirmAdmin { get; set; }

    public bool CanPost { get; set; }

    public bool CanAdminister { get; set; }
}
