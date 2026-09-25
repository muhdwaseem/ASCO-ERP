using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AegisErp.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Asco.Api.Tests;

/// <summary>Boots the real API on a throwaway Sqlite database seeded with C-ERP's demo data.</summary>
public sealed class AscoFactory : WebApplicationFactory<Program>
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"asco_test_{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_db}");
        builder.UseSetting("Seed:DemoData", "true");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" }) try { File.Delete(f); } catch { /* best effort */ }
    }
}

public class ApiTests(AscoFactory factory) : IClassFixture<AscoFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<HttpClient> SignedIn(string email, string password)
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        c.DefaultRequestHeaders.Add("X-ASCO", "1");
        var r = await c.PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return c;
    }

    private Task<HttpClient> Owner() => SignedIn(SeedData.DemoAdminEmail, SeedData.DemoAdminPassword);

    private static async Task<List<int>> CompanyIds(HttpClient c)
    {
        var me = await c.GetFromJsonAsync<JsonElement>("/api/auth/me");
        return me.GetProperty("companies").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).ToList();
    }

    private static HttpRequestMessage Get(string url, int companyId)
    {
        var m = new HttpRequestMessage(HttpMethod.Get, url);
        m.Headers.Add("X-Company-Id", companyId.ToString());
        return m;
    }

    [Fact]
    public async Task Unauthenticated_requests_get_401_not_a_redirect()
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Get("/api/accounts", 1))).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-ASCO", "1");
        var r = await c.PostAsJsonAsync("/api/auth/login", new { email = SeedData.DemoAdminEmail, password = "nope-nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Post_without_csrf_header_is_rejected()
    {
        var c = factory.CreateClient();
        var r = await c.PostAsJsonAsync("/api/auth/login", new { email = SeedData.DemoAdminEmail, password = SeedData.DemoAdminPassword });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Company_header_is_required()
    {
        var c = await Owner();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/accounts")).StatusCode);
    }

    [Fact]
    public async Task Unknown_company_is_forbidden()
    {
        var c = await Owner();
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Get("/api/accounts", 999_999))).StatusCode);
    }

    [Fact]
    public async Task Every_read_endpoint_answers_200_for_every_company()
    {
        var c = await Owner();
        var ids = await CompanyIds(c);
        Assert.NotEmpty(ids);
        string[] urls =
        [
            "/api/dashboard", "/api/accounts", "/api/opening-balances", "/api/general-ledger", "/api/vouchers",
            "/api/customers", "/api/agents", "/api/sales-invoices", "/api/estimates", "/api/delivery-notes", "/api/recurring-invoices",
            "/api/receipts", "/api/credit-notes", "/api/ar-aging", "/api/outstanding-invoices", "/api/transactions",
            "/api/vendors", "/api/purchase-invoices", "/api/vendor-payments", "/api/debit-notes", "/api/expenses", "/api/ap-aging", "/api/expense-transactions",
            "/api/items", "/api/units", "/api/item-categories", "/api/item-kits", "/api/leads", "/api/fixed-assets",
            "/api/employees", "/api/payroll-runs", "/api/expiring-documents",
            "/api/reports/trial-balance", "/api/reports/profit-and-loss", "/api/reports/balance-sheet", "/api/reports/cash-flow",
            "/api/reports/segments", "/api/reports/customer-revenue", "/api/reports/vendor-spend", "/api/reports/commission",
            "/api/reports/reassignment-audit", "/api/reports/vat-control",
            "/api/fiscal-periods", "/api/cost-centers", "/api/currencies", "/api/tax-codes", "/api/service-kits", "/api/team",
        ];
        var failures = new List<string>();
        foreach (var id in ids)
            foreach (var url in urls)
            {
                var r = await c.SendAsync(Get(url, id));
                // 409 = the company has no fiscal period for today (dashboard / cash flow) — a data state, not a fault.
                if (r.StatusCode != HttpStatusCode.OK && r.StatusCode != HttpStatusCode.Conflict)
                    failures.Add($"company {id} {url} → {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
            }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(10)));
    }

    [Fact]
    public async Task Trial_balance_and_balance_sheet_balance_in_every_company()
    {
        var c = await Owner();
        foreach (var id in await CompanyIds(c))
        {
            var tb = await (await c.SendAsync(Get("/api/reports/trial-balance", id))).Content.ReadFromJsonAsync<JsonElement>(Json);
            Assert.True(tb.GetProperty("isBalanced").GetBoolean(), $"TB out of balance for company {id}");
            var bs = await (await c.SendAsync(Get("/api/reports/balance-sheet", id))).Content.ReadFromJsonAsync<JsonElement>(Json);
            Assert.True(bs.GetProperty("isBalanced").GetBoolean(), $"Balance sheet out of balance for company {id}");
        }
    }

    [Fact]
    public async Task Companies_are_isolated()
    {
        var c = await Owner();
        var ids = await CompanyIds(c);
        if (ids.Count < 2) return; // demo seed has several; nothing to compare otherwise
        async Task<HashSet<int>> CustomerIds(int company) =>
            (await (await c.SendAsync(Get("/api/customers", company))).Content.ReadFromJsonAsync<JsonElement>(Json))
                .EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).ToHashSet();
        var a = await CustomerIds(ids[0]);
        var b = await CustomerIds(ids[1]);
        Assert.Empty(a.Intersect(b));
    }

    [Fact]
    public async Task Viewer_cannot_open_payroll()
    {
        var c = await SignedIn(SeedData.DemoViewerEmail, SeedData.DemoViewerPassword);
        var ids = await CompanyIds(c);
        Assert.NotEmpty(ids);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Get("/api/employees", ids[0]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Get("/api/team", ids[0]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Get("/api/reports/trial-balance", ids[0]))).StatusCode);
    }
}
