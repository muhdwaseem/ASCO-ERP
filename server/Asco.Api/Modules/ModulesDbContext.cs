using AegisErp.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Asco.Api.Modules;

/// <summary>
/// ASCO's own DbContext for industry modules. Same database/connection as C-ERP, separate asco_*
/// tables. Company isolation copies C-ERP's approach: a global query filter on CompanyId driven by
/// the request's CurrentCompany, and SaveChanges refuses to write another company's rows.
/// </summary>
public class ModulesDbContext(DbContextOptions<ModulesDbContext> options, ICurrentCompany current) : DbContext(options)
{
    private readonly ICurrentCompany _current = current;
    private int? CompanyId => _current.CompanyId;

    public DbSet<CompanyProfile> CompanyProfiles => Set<CompanyProfile>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<StockItemSetting> StockItemSettings => Set<StockItemSetting>();
    public DbSet<StockMove> StockMoves => Set<StockMove>();
    public DbSet<StockMoveLine> StockMoveLines => Set<StockMoveLine>();
    public DbSet<Bom> Boms => Set<Bom>();
    public DbSet<BomLine> BomLines => Set<BomLine>();
    public DbSet<ProductionOrder> ProductionOrders => Set<ProductionOrder>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<Trip> Trips => Set<Trip>();
    public DbSet<AiLog> AiLogs => Set<AiLog>();
    public DbSet<Prepayment> Prepayments => Set<Prepayment>();
    public DbSet<PrepaymentRelease> PrepaymentReleases => Set<PrepaymentRelease>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<CompanyProfile>().ToTable("asco_company_profiles").HasIndex(x => x.CompanyId).IsUnique();
        b.Entity<Warehouse>().ToTable("asco_warehouses").HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        b.Entity<StockItemSetting>().ToTable("asco_stock_item_settings").HasIndex(x => new { x.CompanyId, x.ItemId }).IsUnique();
        b.Entity<StockMove>().ToTable("asco_stock_moves").HasIndex(x => new { x.CompanyId, x.MoveNo }).IsUnique();
        b.Entity<StockMoveLine>().ToTable("asco_stock_move_lines").HasIndex(x => new { x.CompanyId, x.ItemId, x.WarehouseId });
        b.Entity<Bom>().ToTable("asco_boms").HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        b.Entity<BomLine>().ToTable("asco_bom_lines");
        b.Entity<ProductionOrder>().ToTable("asco_production_orders").HasIndex(x => new { x.CompanyId, x.OrderNo }).IsUnique();
        b.Entity<Job>().ToTable("asco_jobs").HasIndex(x => new { x.CompanyId, x.JobNo }).IsUnique();
        b.Entity<Vehicle>().ToTable("asco_vehicles").HasIndex(x => new { x.CompanyId, x.PlateNo }).IsUnique();
        b.Entity<Trip>().ToTable("asco_trips").HasIndex(x => new { x.CompanyId, x.TripNo }).IsUnique();
        b.Entity<AiLog>().ToTable("asco_ai_logs");
        b.Entity<Prepayment>().ToTable("asco_prepayments").HasIndex(x => new { x.CompanyId, x.PrepaymentNo }).IsUnique();
        b.Entity<PrepaymentRelease>().ToTable("asco_prepayment_releases").HasIndex(x => new { x.PrepaymentId, x.MonthNo }).IsUnique();

        foreach (var p in b.Model.GetEntityTypes().SelectMany(t => t.GetProperties()).Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
            p.SetPrecision(18); // scale set below per meaning
        foreach (var p in b.Model.GetEntityTypes().SelectMany(t => t.GetProperties()).Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
            p.SetScale(p.Name.Contains("Quantity") || p.Name.Contains("Level") || p.Name.Contains("Litres") || p.Name.Contains("Km") ? 4 : 2);
        foreach (var e in b.Model.GetEntityTypes())
            foreach (var p in e.GetProperties().Where(p => p.ClrType.IsEnum || Nullable.GetUnderlyingType(p.ClrType)?.IsEnum == true))
                p.SetProviderClrType(typeof(string));
        b.Entity<StockMoveLine>().Property(x => x.UnitCost).HasPrecision(18, 4);

        Filter<CompanyProfile>(b); Filter<Warehouse>(b); Filter<StockItemSetting>(b); Filter<StockMove>(b); Filter<StockMoveLine>(b);
        Filter<Bom>(b); Filter<BomLine>(b); Filter<ProductionOrder>(b); Filter<Job>(b); Filter<Vehicle>(b); Filter<Trip>(b); Filter<AiLog>(b);
        Filter<Prepayment>(b); Filter<PrepaymentRelease>(b);
    }

    private void Filter<T>(ModelBuilder b) where T : class, IModuleScoped =>
        b.Entity<T>().HasQueryFilter(e => e.CompanyId == CompanyId);

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        foreach (var entry in ChangeTracker.Entries<IModuleScoped>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.CompanyId == 0)
                    entry.Entity.CompanyId = CompanyId ?? throw new InvalidOperationException("No active company for module write.");
            }
            if ((entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted) && entry.Entity.CompanyId != CompanyId)
                throw new InvalidOperationException("Cross-company write blocked.");
        }
        return base.SaveChangesAsync(ct);
    }

    /// <summary>Creates the asco_* tables when missing (dev Sqlite and first run on a shared DB).
    /// C-ERP's own schema is never touched. Production should move to versioned migrations.</summary>
    public static async Task EnsureTablesAsync(ModulesDbContext db)
    {
        try
        {
            await db.CompanyProfiles.IgnoreQueryFilters().AnyAsync();
        }
        catch
        {
            var creator = db.Database.GetService<IRelationalDatabaseCreator>();
            await creator.CreateTablesAsync();
            return;
        }
        // Tables added after a database was first created: run just their part of the create script.
        await AddMissingAsync(db, "asco_prepayment", () => db.Prepayments.IgnoreQueryFilters().AnyAsync());
    }

    private static async Task AddMissingAsync(ModulesDbContext db, string tablePrefix, Func<Task<bool>> probe)
    {
        try { await probe(); return; }
        catch { /* table missing */ }
        var statements = db.Database.GenerateCreateScript()
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(sql => sql.Contains(tablePrefix, StringComparison.Ordinal) && sql.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase));
        foreach (var sql in statements)
            await db.Database.ExecuteSqlRawAsync(sql);
    }
}
