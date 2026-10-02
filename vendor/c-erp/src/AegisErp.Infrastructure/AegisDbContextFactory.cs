using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AegisErp.Infrastructure;

/// <summary>
/// Design-time factory used only by <c>dotnet ef migrations</c>/<c>database update</c>. Without
/// this, the EF tools fall back to invoking the Web project's <c>Program.cs</c> top-level
/// statements to build the host — which would run <see cref="SeedData.EnsureSeededAsync"/> and
/// try to open a real database connection just to generate a migration file.
///
/// Production runs on Postgres (see README "Migrations"), so migrations are authored against
/// Npgsql; the connection string here is never actually opened by `migrations add`, only used to
/// pick column type mappings.
/// </summary>
public class AegisDbContextFactory : IDesignTimeDbContextFactory<AegisDbContext>
{
    public AegisDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<AegisDbContext>();
        DatabaseProvider.Configure(builder, DatabaseProvider.Postgres,
            "Host=localhost;Database=aegis_erp;Username=postgres;Password=postgres");
        return new AegisDbContext(builder.Options);
    }
}
