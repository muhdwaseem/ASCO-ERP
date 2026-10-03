using AegisErp.Infrastructure;
using AegisErp.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Asco.Api;

internal static class DatabaseStartup
{
    /// <summary>
    /// Sqlite (local dev): create ASCO's own dev database and seed C-ERP's demo data.
    /// Postgres / SQL Server (shared with the live C-ERP app): C-ERP owns the schema and applies
    /// migrations on its own deploy — the API never migrates or seeds a shared database. It only
    /// refuses to start if the schema is behind the code it was built against.
    /// </summary>
    public static async Task RunAsync(WebApplication app)
    {
        var config = app.Configuration;
        var provider = config["Database:Provider"] ?? DatabaseProvider.Sqlite;
        using var scope = app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await using var db = await sp.GetRequiredService<IDbContextFactory<AegisDbContext>>().CreateDbContextAsync();

        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Asco.Startup");
        if (provider == DatabaseProvider.Sqlite)
        {
            // WAL needs shared-memory mapping, which some container filesystems refuse; the hosted image
            // sets Database:SqliteJournalMode=DELETE. Local dev keeps WAL (faster, readers don't block).
            var journal = config["Database:SqliteJournalMode"] is { Length: > 0 } j && new[] { "WAL", "DELETE", "TRUNCATE", "MEMORY" }.Contains(j.ToUpperInvariant()) ? j.ToUpperInvariant() : "WAL";
            log.LogInformation("Startup 1/3: opening the SQLite database (journal mode {Mode})", journal);
            await db.Database.ExecuteSqlRawAsync($"PRAGMA journal_mode={journal};");
            log.LogInformation("Startup 2/3: creating tables if missing");
            await db.Database.EnsureCreatedAsync();
            log.LogInformation("Startup 3/3: seeding demo users and data if empty (slow on a small server)");
            await SeedData.EnsureSeededAsync(db,
                sp.GetRequiredService<UserManager<AppUser>>(),
                sp.GetRequiredService<RoleManager<IdentityRole>>(),
                seedDemoData: config.GetValue<bool?>("Seed:DemoData") ?? false);
            return;
        }

        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count > 0 && !config.GetValue<bool>("Database:AllowPendingMigrations"))
            throw new InvalidOperationException(
                $"Database schema is behind by {pending.Count} migration(s) ({string.Join(", ", pending)}). " +
                "Deploy C-ERP first (it applies migrations), then start the ASCO API.");
    }
}
