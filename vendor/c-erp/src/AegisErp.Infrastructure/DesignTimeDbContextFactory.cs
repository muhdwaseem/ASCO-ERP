using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AegisErp.Infrastructure;

/// <summary>
/// Used by "dotnet ef" at design time (migrations). Reads the provider/connection from
/// environment variables so migrations can be generated for any database:
///   AEGIS_PROVIDER = Sqlite | Postgres | SqlServer   (default Sqlite)
///   AEGIS_CONNECTION = connection string (default matches the provider)
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AegisDbContext>
{
    public AegisDbContext CreateDbContext(string[] args)
    {
        var provider = Environment.GetEnvironmentVariable("AEGIS_PROVIDER") ?? DatabaseProvider.Sqlite;
        var conn = Environment.GetEnvironmentVariable("AEGIS_CONNECTION")
                   ?? provider switch
                   {
                       DatabaseProvider.Postgres => "Host=localhost;Database=aegis_erp;Username=postgres;Password=postgres",
                       DatabaseProvider.SqlServer => "Server=localhost;Database=aegis_erp;Trusted_Connection=True;TrustServerCertificate=True",
                       _ => "Data Source=aegis_erp.db"
                   };

        var options = new DbContextOptionsBuilder<AegisDbContext>();
        DatabaseProvider.Configure(options, provider, conn);
        return new AegisDbContext(options.Options);
    }
}
