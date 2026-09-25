using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AegisErp.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Asco.Api.Tests;

/// <summary>Industry modules end-to-end on a fresh database per test class.</summary>
public class ModuleTests(AscoFactory factory) : IClassFixture<AscoFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed class S(HttpClient http, int company)
    {
        public int Company => company;
        public async Task<HttpResponseMessage> Send(HttpMethod m, string url, object? body = null, int? companyOverride = null)
        {
            var req = new HttpRequestMessage(m, url);
            req.Headers.Add("X-Company-Id", (companyOverride ?? company).ToString());
            if (body is not null) req.Content = JsonContent.Create(body);
            return await http.SendAsync(req);
        }
        public async Task<JsonElement> Get(string url)
        {
            var r = await Send(HttpMethod.Get, url);
            Assert.True(r.IsSuccessStatusCode, $"GET {url} → {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
            return await r.Content.ReadFromJsonAsync<JsonElement>(Json);
        }
        public async Task<JsonElement> Ok(HttpMethod m, string url, object body)
        {
            var r = await Send(m, url, body);
            Assert.True(r.IsSuccessStatusCode, $"{m} {url} → {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
            var text = await r.Content.ReadAsStringAsync();
            return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        }
        public Task<JsonElement> Post(string url, object body) => Ok(HttpMethod.Post, url, body);
    }

    private async Task<S> Owner()
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        c.DefaultRequestHeaders.Add("X-ASCO", "1");
        await c.PostAsJsonAsync("/api/auth/login", new { email = SeedData.DemoAdminEmail, password = SeedData.DemoAdminPassword });
        var me = await c.GetFromJsonAsync<JsonElement>("/api/auth/me");
        return new S(c, me.GetProperty("companies")[0].GetProperty("id").GetInt32());
    }

    private sealed record Ctx(string Date, int Bank, int Revenue, int Expense, int Customer, int Vendor);

    private static async Task<Ctx> Configure(S s, string industry, string[] modules)
    {
        var lk = await s.Get("/api/lookups");
        var acc = lk.GetProperty("accounts").EnumerateArray().ToList();
        int[] Of(string type) => acc.Where(a => a.GetProperty("type").GetString() == type).Select(a => a.GetProperty("id").GetInt32()).ToArray();
        var assets = Of("Asset"); var liab = Of("Liability"); var exp = Of("Expense"); var inc = Of("Income");
        var bank = lk.GetProperty("bankAccounts")[0].GetProperty("id").GetInt32();
        // A dedicated inventory control account, so nothing else posts to it during the test.
        var code = "139" + Random.Shared.Next(10, 99);
        var inventory = (await s.Post("/api/accounts", new { code, name = "Inventory (ASCO test " + code + ")", type = "Asset", isPostable = true, category = "Current Assets", currency = "AED", parentId = (int?)null, description = (string?)null, openingBalance = 0m })).GetProperty("id").GetInt32();
        await s.Ok(HttpMethod.Put, "/api/modules/profile", new
        {
            industry, modules, inventoryAccountId = inventory, cogsAccountId = exp[0], stockClearingAccountId = liab[0],
            stockAdjustmentAccountId = exp[^1], conversionCostAccountId = exp.Length > 1 ? exp[1] : exp[0], fleetExpenseAccountId = exp[0],
        });
        var period = lk.GetProperty("openPeriods").EnumerateArray().Last();
        return new Ctx(period.GetProperty("startDate").GetString()!, bank, inc[0], exp[0],
            lk.GetProperty("customers")[0].GetProperty("id").GetInt32(), lk.GetProperty("vendors")[0].GetProperty("id").GetInt32());
    }

    private static async Task<int> Item(S s, string name, int revenue)
    {
        var r = await s.Post("/api/items", new { name, kind = "Goods", unit = "Pcs", sellingPrice = 100m, salesAccountId = revenue, salesDescription = (string?)null, costPrice = (decimal?)null, purchaseAccountId = (int?)null, purchaseDescription = (string?)null, taxCodeId = (int?)null });
        return r.GetProperty("id").GetInt32();
    }

    private static async Task AssertBalanced(S s)
    {
        Assert.True((await s.Get("/api/reports/trial-balance?from=2000-01-01&to=2100-12-31")).GetProperty("isBalanced").GetBoolean(), "TB out of balance");
    }

    [Fact]
    public async Task Modules_are_off_until_enabled_and_isolated_per_company()
    {
        var s = await Owner();
        // Company 2 is never configured by these tests: its modules are off.
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Send(HttpMethod.Get, "/api/inventory/warehouses", companyOverride: s.Company + 1)).StatusCode);
        var catalog = await s.Get("/api/modules/catalog");
        Assert.True(catalog.GetProperty("industries").GetArrayLength() >= 7);
    }

    [Fact]
    public async Task Manufacturing_flow_costs_finished_goods_and_keeps_ledger_reconciled()
    {
        var s = await Owner();
        var c = await Configure(s, "Manufacturing", ["inventory", "manufacturing"]);
        var steel = await Item(s, "Steel sheet", c.Revenue);
        var bolt = await Item(s, "Bolt M8", c.Revenue);
        var cabinet = await Item(s, "Steel cabinet", c.Revenue);
        var raw = (await s.Post("/api/inventory/warehouses", new { code = "RAW", name = "Raw materials" })).GetProperty("id").GetInt32();
        var fg = (await s.Post("/api/inventory/warehouses", new { code = "FG", name = "Finished goods" })).GetProperty("id").GetInt32();

        var diffBefore = (await s.Get("/api/inventory/valuation")).GetProperty("difference").GetDecimal();

        var rec = await s.Post("/api/inventory/receipts", new { date = c.Date, warehouseId = raw, reference = "GRN-1", lines = new object[] {
            new { itemId = steel, quantity = 100m, unitCost = 10m }, new { itemId = bolt, quantity = 400m, unitCost = 0.5m } } });
        Assert.Equal(1200m, rec.GetProperty("value").GetDecimal());
        Assert.StartsWith("JV-", rec.GetProperty("voucherNo").GetString());

        var over = await s.Send(HttpMethod.Post, "/api/inventory/issues", new { date = c.Date, warehouseId = raw, lines = new[] { new { itemId = steel, quantity = 1000m } } });
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        Assert.Contains("Insufficient stock", await over.Content.ReadAsStringAsync());

        var bom = (await s.Post("/api/manufacturing/boms", new { code = "CAB-1", name = "Cabinet", outputItemId = cabinet, outputQuantity = 1m, conversionCost = 30m,
            lines = new[] { new { componentItemId = steel, quantity = 5m }, new { componentItemId = bolt, quantity = 20m } } })).GetProperty("id").GetInt32();
        var mo = (await s.Post("/api/manufacturing/orders", new { bomId = bom, quantity = 4m, plannedDate = c.Date, sourceWarehouseId = raw, outputWarehouseId = fg })).GetProperty("id").GetInt32();

        var req = await s.Get("/api/manufacturing/requirements");
        Assert.Contains(req.EnumerateArray(), r => r.GetProperty("required").GetDecimal() == 20m && r.GetProperty("shortage").GetDecimal() == 0m);

        var done = await s.Post($"/api/manufacturing/orders/{mo}/complete", new { date = c.Date });
        Assert.Equal(240m, done.GetProperty("materialCost").GetDecimal());
        Assert.Equal(120m, done.GetProperty("conversionCost").GetDecimal());
        Assert.Equal(90m, done.GetProperty("unitCost").GetDecimal());
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Send(HttpMethod.Post, $"/api/manufacturing/orders/{mo}/complete", new { date = c.Date })).StatusCode);

        var val = await s.Get("/api/inventory/valuation");
        decimal Qty(int id) => val.GetProperty("rows").EnumerateArray().First(r => r.GetProperty("itemId").GetInt32() == id).GetProperty("quantity").GetDecimal();
        Assert.Equal(80m, Qty(steel));
        Assert.Equal(320m, Qty(bolt));
        Assert.Equal(4m, Qty(cabinet));
        Assert.Equal(1200m + 120m, val.GetProperty("totalValue").GetDecimal());
        Assert.Equal(diffBefore, val.GetProperty("difference").GetDecimal()); // stock ledger == GL inventory movement

        // Transfer (no GL), sale issue once only, adjustment
        await s.Post("/api/inventory/transfers", new { date = c.Date, fromWarehouseId = fg, toWarehouseId = raw, lines = new[] { new { itemId = cabinet, quantity = 1m } } });
        var inv = await s.Post("/api/sales-invoices", new { customerId = c.Customer, date = c.Date, lines = new[] { new { description = "Cabinet", revenueAccountId = c.Revenue, quantity = 1m, unitPrice = 250m, vatRate = 0.05m, itemId = cabinet } } });
        var invId = inv.GetProperty("id").GetInt32();
        var sale = await s.Post($"/api/inventory/issue-for-invoice/{invId}", new { warehouseId = fg });
        Assert.Equal(90m, sale.GetProperty("value").GetDecimal());
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Send(HttpMethod.Post, $"/api/inventory/issue-for-invoice/{invId}", new { warehouseId = fg })).StatusCode);
        await s.Post("/api/inventory/adjustments", new { date = c.Date, warehouseId = raw, reason = "Count", lines = new[] { new { itemId = bolt, quantity = -2m } } });

        var after = await s.Get("/api/inventory/valuation");
        Assert.Equal(1320m - 90m - 1m, after.GetProperty("totalValue").GetDecimal());
        Assert.Equal(diffBefore, after.GetProperty("difference").GetDecimal());
        await AssertBalanced(s);

        // Company 2 cannot see company 1's stock or BOMs (and hasn't enabled the modules).
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Send(HttpMethod.Get, "/api/inventory/stock", companyOverride: s.Company + 1)).StatusCode);
    }

    [Fact]
    public async Task Logistics_job_profitability_comes_from_the_ledger_including_trips()
    {
        var s = await Owner();
        var c = await Configure(s, "Logistics", ["jobs", "fleet"]);
        var job = await s.Post("/api/jobs", new { title = "DXB → LHR air freight", type = "Shipment", customerId = c.Customer, budget = 1500m, openedDate = c.Date,
            mode = "Air", direction = "Export", origin = "DXB", destination = "LHR", awbBl = "176-12345675", packages = 12, weightKg = 340m });
        var jobId = job.GetProperty("id").GetInt32();
        Assert.StartsWith("SHP-", job.GetProperty("jobNo").GetString());
        var cc = (await s.Get("/api/cost-centers")).EnumerateArray().First(x => x.GetProperty("code").GetString() == job.GetProperty("jobNo").GetString()).GetProperty("id").GetInt32();

        await s.Post("/api/sales-invoices", new { customerId = c.Customer, date = c.Date, lines = new[] { new { description = "Air freight", revenueAccountId = c.Revenue, costCenterId = cc, quantity = 1m, unitPrice = 2000m, vatRate = 0m } } });
        await s.Post("/api/purchase-invoices", new { vendorId = c.Vendor, vendorRef = "AWB cost", date = c.Date, lines = new[] { new { description = "Airline charges", expenseAccountId = c.Expense, costCenterId = cc, quantity = 1m, unitPrice = 1200m, vatRate = 0m } } });

        var truck = (await s.Post("/api/fleet/vehicles", new { plateNo = "DXB-A-12345", type = "Truck", capacityKg = 3000m })).GetProperty("id").GetInt32();
        var trip = await s.Post("/api/fleet/trips", new { vehicleId = truck, jobId, driver = "Ali", date = c.Date, from = "Warehouse", to = "DXB Cargo", distanceKm = 42m, fuelLitres = 12m, fuelCost = 36m, tolls = 8m, otherCost = 6m, paidFromAccountId = c.Bank });
        Assert.StartsWith("JV-", trip.GetProperty("voucherNo").GetString());

        var j = (await s.Get("/api/jobs")).EnumerateArray().First(x => x.GetProperty("id").GetInt32() == jobId);
        Assert.Equal(2000m, j.GetProperty("revenue").GetDecimal());
        Assert.Equal(1250m, j.GetProperty("cost").GetDecimal());
        Assert.Equal(750m, j.GetProperty("margin").GetDecimal());
        Assert.False(j.GetProperty("overBudget").GetBoolean());

        var v = (await s.Get("/api/fleet/vehicles")).EnumerateArray().First();
        Assert.Equal(50m, v.GetProperty("runningCost").GetDecimal());
        Assert.Equal(3.5m, v.GetProperty("kmPerLitre").GetDecimal());
        await AssertBalanced(s);
    }
}
