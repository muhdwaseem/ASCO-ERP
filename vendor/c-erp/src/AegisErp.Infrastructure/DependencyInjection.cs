using AegisErp.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AegisErp.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the DbContext factory (provider chosen by config) and the application services.
    /// Config keys: "Database:Provider" (Sqlite|Postgres) and "ConnectionStrings:{Provider}".
    /// </summary>
    public static IServiceCollection AddAegisInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["Database:Provider"] ?? DatabaseProvider.Sqlite;
        var conn = config.GetConnectionString(provider)
                   ?? (provider == DatabaseProvider.Postgres
                       ? "Host=localhost;Database=aegis_erp;Username=postgres;Password=postgres"
                       : "Data Source=aegis_erp.db");

        // Scoped lifetime so the factory can inject the scoped ICurrentCompany into each context —
        // that is what lets the global query filters know which company the user is working in.
        services.AddScoped<CurrentCompany>();
        services.AddScoped<ICurrentCompany>(sp => sp.GetRequiredService<CurrentCompany>());

        services.AddDbContextFactory<AegisDbContext>(options =>
            DatabaseProvider.Configure(options, provider, conn), ServiceLifetime.Scoped);

        services.AddScoped<CompanyAccessService>();

        services.AddScoped<ChartOfAccountsService>();
        services.AddScoped<JournalService>();
        services.AddScoped<LedgerService>();
        services.AddScoped<ReportsService>();
        services.AddScoped<CustomerService>();
        services.AddScoped<SalesInvoiceService>();
        services.AddScoped<ReceiptService>();
        services.AddScoped<CreditNoteService>();
        services.AddScoped<EstimateService>();
        services.AddScoped<DeliveryNoteService>();
        services.AddScoped<VendorService>();
        services.AddScoped<PurchaseInvoiceService>();
        services.AddScoped<InvoiceImportService>();
        services.AddScoped<VendorPaymentService>();
        services.AddScoped<DebitNoteService>();
        services.AddScoped<DirectExpenseService>();
        services.AddScoped<DirectExpensePaymentService>();
        services.AddScoped<CompanyService>();
        services.AddScoped<SubscriptionService>();
        services.AddScoped<CurrencyService>();
        services.AddScoped<TaxCodeService>();
        services.AddScoped<ItemService>();
        services.AddScoped<ItemKitService>();
        services.AddScoped<ServiceKitService>();
        services.AddScoped<CustomFieldService>();
        services.AddScoped<TagService>();
        services.AddScoped<RecurringInvoiceService>();
        services.AddScoped<TransactionService>();
        services.AddScoped<ExpenseTransactionService>();
        services.AddScoped<FixedAssetService>();
        services.AddScoped<EmployeeService>();
        services.AddScoped<PayrollService>();
        services.AddScoped<LeadService>();
        services.AddScoped<GratuityService>();
        services.AddScoped<SalaryAdvanceService>();
        services.AddScoped<LeaveService>();
        services.AddScoped<WpsFileService>();
        services.AddScoped<FiscalPeriodService>();
        services.AddScoped<AgentService>();
        services.AddScoped<CommissionConfigService>();
        services.AddScoped<CommissionService>();
        services.Configure<SmtpOptions>(config.GetSection("Smtp"));
        services.AddScoped<EmailService>();
        services.Configure<GeminiOptions>(config.GetSection("Gemini"));
        services.AddHttpClient<BillScanningService>();
        services.AddScoped<AiQueryService>();
        services.AddHostedService<InvoiceAutomationHostedService>();
        return services;
    }
}
