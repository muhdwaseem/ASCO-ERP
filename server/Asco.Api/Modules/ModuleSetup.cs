using AegisErp.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Asco.Api.Modules;

public static class ModuleSetup
{
    public static IServiceCollection AddAscoModules(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["Database:Provider"] ?? DatabaseProvider.Sqlite;
        var conn = config.GetConnectionString(provider)
                   ?? (provider == DatabaseProvider.Postgres ? "Host=localhost;Database=aegis_erp;Username=postgres;Password=postgres" : "Data Source=aegis_erp.db");
        // Same database and provider settings as C-ERP's context; ASCO's tables are the asco_* ones.
        services.AddDbContext<ModulesDbContext>(o => DatabaseProvider.Configure(o, provider, conn));
        services.AddScoped<GlBridge>();
        services.AddScoped<StockEngine>();
        return services;
    }

    public static async Task EnsureModuleTablesAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        await ModulesDbContext.EnsureTablesAsync(scope.ServiceProvider.GetRequiredService<ModulesDbContext>());
    }

    public static void MapModuleEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter<CompanyScopeFilter>().AddEndpointFilter(WriteEndpoints.TranslateErrors);
        api.MapIndustryEndpoints();
        api.MapInventoryEndpoints();
        api.MapManufacturingEndpoints();
        api.MapJobsEndpoints();
        api.MapAccountingEndpoints();
        api.MapReportEndpoints();
    }
}
