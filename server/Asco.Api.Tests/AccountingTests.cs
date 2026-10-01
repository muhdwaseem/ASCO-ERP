using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AegisErp.Infrastructure;
using Asco.Api.Modules;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Asco.Api.Tests;

public class AccountingTests(AscoFactory factory) : IClassFixture<AscoFactory>
{
    private async Task<HttpClient> Owner()
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        c.DefaultRequestHeaders.Add("X-ASCO", "1");
        await c.PostAsJsonAsync("/api/auth/login", new { email = SeedData.DemoAdminEmail, password = SeedData.DemoAdminPassword });
        var me = await c.GetFromJsonAsync<JsonElement>("/api/auth/me");
        c.DefaultRequestHeaders.Add("X-Company-Id", me.GetProperty("companies")[0].GetProperty("id").GetInt32().ToString());
        return c;
    }

    [Theory]
    [InlineData(100_000, 3)]
    [InlineData(1_000, 7)]
    [InlineData(120_000, 12)]
    public void Prepayment_months_add_up_to_the_exact_total(decimal amount, int months)
    {
        var p = new Prepayment { Amount = amount, Months = months };
        Assert.Equal(amount, Enumerable.Range(1, months).Sum(p.MonthAmount));
    }

    [Fact]
    public async Task Accounting_reports_and_lists_load()
    {
        var c = await Owner();
        foreach (var path in new[] { "/api/reports/cost-centre-pnl", "/api/gratuity", "/api/prepayments" })
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Report_menu_endpoints_load()
    {
        var c = await Owner();
        foreach (var path in new[] { "sales-by-item", "purchases-by-item", "vat-return", "ratios", "equity-movement", "monthly-pnl", "bank-book" })
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/reports/{path}")).StatusCode);
    }

    [Fact]
    public async Task Movement_of_equity_closes_at_balance_sheet_equity()
    {
        var c = await Owner();
        var eq = await c.GetFromJsonAsync<JsonElement>("/api/reports/equity-movement");
        var bs = await c.GetFromJsonAsync<JsonElement>("/api/reports/balance-sheet");
        var bsEquity = bs.GetProperty("equity").EnumerateArray().Sum(l => l.GetProperty("amount").GetDecimal()) + bs.GetProperty("currentYearEarnings").GetDecimal();
        Assert.Equal(bsEquity, eq.GetProperty("totalClosing").GetDecimal());
    }

    [Fact]
    public async Task Prepayment_rejects_a_non_expense_account()
    {
        var c = await Owner();
        var lookups = await c.GetFromJsonAsync<JsonElement>("/api/lookups");
        var accounts = lookups.GetProperty("accounts").EnumerateArray().ToList();
        var asset = accounts.First(a => a.GetProperty("type").ToString() == "Asset").GetProperty("id").GetInt32();
        var res = await c.PostAsJsonAsync("/api/prepayments", new
        {
            description = "Insurance", vendorId = (int?)null, startDate = "2026-01-01", months = 12, amount = 1200m,
            prepaidAccountId = asset, expenseAccountId = asset, costCenterId = (int?)null, paidFromAccountId = (int?)null, paidDate = (string?)null,
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
