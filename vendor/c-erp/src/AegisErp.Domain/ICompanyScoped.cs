namespace AegisErp.Domain;

/// <summary>
/// Marks an entity as belonging to exactly one company (tenant). Every such entity carries a
/// <see cref="CompanyId"/>, and the DbContext applies a global query filter on it — so a query
/// can never return another company's rows even if a service forgets to filter explicitly.
/// </summary>
public interface ICompanyScoped
{
    /// <summary>The owning company (<c>CompanySetup.Id</c>).</summary>
    int CompanyId { get; set; }
}
