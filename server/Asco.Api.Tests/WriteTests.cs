using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AegisErp.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Asco.Api.Tests;

public class WriteTests(AscoFactory factory) : IClassFixture<AscoFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed class Session(HttpClient http, int company)
    {
        public HttpClient Http => http;
        public int Company => company;
        public async Task<HttpResponseMessage> Send(HttpMethod m, string url, object? body = null)
        {
            var req = new HttpRequestMessage(m, url);
            req.Headers.Add("X-Company-Id", company.ToString());
            if (body is not null) req.Content = JsonContent.Create(body);
            return await http.SendAsync(req);
        }
        public async Task<JsonElement> Get(string url)
        {
            var r = await Send(HttpMethod.Get, url);
            Assert.True(r.IsSuccessStatusCode, $"{url} → {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
            return await r.Content.ReadFromJsonAsync<JsonElement>(Json);
        }
        public Task<HttpResponseMessage> Post(string url, object body) => Send(HttpMethod.Post, url, body);
    }

    private async Task<Session> SignIn(string email, string password, int companyIndex = 0)
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        c.DefaultRequestHeaders.Add("X-ASCO", "1");
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync("/api/auth/login", new { email, password })).StatusCode);
        var me = await c.GetFromJsonAsync<JsonElement>("/api/auth/me");
        return new Session(c, me.GetProperty("companies")[companyIndex].GetProperty("id").GetInt32());
    }

    private Task<Session> Owner() => SignIn(SeedData.DemoAdminEmail, SeedData.DemoAdminPassword);

    /// <summary>A date inside an open period, plus the ids the forms would pick.</summary>
    private static async Task<(string date, int customer, int revenue, int bank, int expense, int vendor)> Setup(Session s)
    {
        var lk = await s.Get("/api/lookups");
        var period = lk.GetProperty("openPeriods").EnumerateArray().Last();
        var accounts = lk.GetProperty("accounts").EnumerateArray().ToList();
        int Acc(string type) => accounts.First(a => a.GetProperty("type").GetString() == type).GetProperty("id").GetInt32();
        return (period.GetProperty("startDate").GetString()!,
            lk.GetProperty("customers")[0].GetProperty("id").GetInt32(), Acc("Income"),
            lk.GetProperty("bankAccounts")[0].GetProperty("id").GetInt32(), Acc("Expense"),
            lk.GetProperty("vendors")[0].GetProperty("id").GetInt32());
    }

    private static async Task AssertBooksBalance(Session s)
    {
        var tb = await s.Get("/api/reports/trial-balance?from=2000-01-01&to=2100-12-31");
        Assert.True(tb.GetProperty("isBalanced").GetBoolean(), "trial balance out of balance");
        var bs = await s.Get("/api/reports/balance-sheet?asOf=2100-12-31");
        Assert.True(bs.GetProperty("isBalanced").GetBoolean(), "balance sheet out of balance");
    }

    [Fact]
    public async Task Invoice_then_receipt_posts_through_cerp_and_books_stay_balanced()
    {
        var s = await Owner();
        var (date, customer, revenue, bank, _, _) = await Setup(s);

        var r = await s.Post("/api/sales-invoices", new
        {
            customerId = customer, date, narration = "ASCO API test",
            lines = new[] { new { description = "Consulting", revenueAccountId = revenue, quantity = 2, unitPrice = 1000m, vatRate = 0.05m } },
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var inv = await r.Content.ReadFromJsonAsync<JsonElement>(Json);
        var id = inv.GetProperty("id").GetInt32();
        Assert.StartsWith("INV-", inv.GetProperty("number").GetString());

        var detail = await s.Get($"/api/sales-invoices/{id}");
        Assert.Equal(2100m, detail.GetProperty("gross").GetDecimal());
        Assert.Equal(2100m, detail.GetProperty("balance").GetDecimal());
        await AssertBooksBalance(s);

        // Over-receipt is refused by C-ERP's allocation rule…
        var over = await s.Post("/api/receipts", new { partyId = customer, invoiceId = id, date, bankAccountId = bank, amount = 5000m, narration = "too much" });
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);

        // …the exact amount settles it.
        var ok = await s.Post("/api/receipts", new { partyId = customer, invoiceId = id, date, bankAccountId = bank, amount = 2100m, narration = "settle" });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(0m, (await s.Get($"/api/sales-invoices/{id}")).GetProperty("balance").GetDecimal());
        await AssertBooksBalance(s);
    }

    [Fact]
    public async Task Journal_voucher_must_balance_and_draft_workflow_posts()
    {
        var s = await Owner();
        var (date, _, _, bank, expense, _) = await Setup(s);

        var bad = await s.Post("/api/vouchers", new { date, narration = "bad", lines = new[] {
            new { accountId = expense, debit = 100m, credit = 0m }, new { accountId = bank, debit = 0m, credit = 90m } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var draft = await s.Post("/api/vouchers", new { date, narration = "draft", draft = true, lines = new[] {
            new { accountId = expense, debit = 250m, credit = 0m }, new { accountId = bank, debit = 0m, credit = 250m } } });
        Assert.Equal(HttpStatusCode.Created, draft.StatusCode);
        var id = (await draft.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetInt32();
        var posted = await s.Post($"/api/vouchers/{id}/post", new { });
        Assert.True(posted.IsSuccessStatusCode, await posted.Content.ReadAsStringAsync());
        await AssertBooksBalance(s);
    }

    [Fact]
    public async Task Purchase_bill_payment_and_expense_post()
    {
        var s = await Owner();
        var (date, _, _, bank, expense, vendor) = await Setup(s);

        var bill = await s.Post("/api/purchase-invoices", new { vendorId = vendor, vendorRef = "V-INV-1", date, narration = "bill",
            lines = new[] { new { description = "Supplies", expenseAccountId = expense, quantity = 1, unitPrice = 400m, vatRate = 0.05m } } });
        Assert.Equal(HttpStatusCode.Created, bill.StatusCode);
        var billId = (await bill.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetInt32();

        var pay = await s.Post("/api/vendor-payments", new { partyId = vendor, invoiceId = billId, date, bankAccountId = bank, amount = 420m, narration = "pay" });
        Assert.Equal(HttpStatusCode.Created, pay.StatusCode);

        var exp = await s.Post("/api/expenses", new { date, bankAccountId = bank, narration = "Taxi",
            lines = new[] { new { expenseAccountId = expense, amount = 60m, vatRate = 0m } } });
        Assert.True(exp.StatusCode == HttpStatusCode.Created, await exp.Content.ReadAsStringAsync());
        await AssertBooksBalance(s);
    }

    [Fact]
    public async Task Viewer_cannot_post()
    {
        var owner = await Owner();
        var (date, customer, revenue, _, _, _) = await Setup(owner);
        var viewer = await SignIn(SeedData.DemoViewerEmail, SeedData.DemoViewerPassword);
        var r = await viewer.Post("/api/sales-invoices", new { customerId = customer, date, lines = new[] { new { description = "x", revenueAccountId = revenue, quantity = 1, unitPrice = 1m, vatRate = 0m } } });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Closed_period_refuses_postings()
    {
        var s = await Owner();
        var (_, customer, revenue, _, _, _) = await Setup(s);
        var periods = await s.Get("/api/fiscal-periods");
        var p = periods.EnumerateArray().First(x => !x.GetProperty("isClosed").GetBoolean());
        var pid = p.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await s.Post($"/api/fiscal-periods/{pid}/close", new { })).StatusCode);
        try
        {
            var r = await s.Post("/api/sales-invoices", new { customerId = customer, date = p.GetProperty("startDate").GetString(),
                lines = new[] { new { description = "x", revenueAccountId = revenue, quantity = 1, unitPrice = 10m, vatRate = 0m } } });
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains("closed", await r.Content.ReadAsStringAsync());
        }
        finally { await s.Post($"/api/fiscal-periods/{pid}/reopen", new { }); }
    }

    [Fact]
    public async Task Customer_can_be_created()
    {
        var s = await Owner();
        var r = await s.Post("/api/customers", new { name = "ASCO Test Customer LLC", group = "Test", currency = "AED", creditLimit = 1000m, paymentTermsDays = 30, trn = (string?)null, email = "t@example.com", phone = (string?)null, address = (string?)null });
        Assert.True(r.StatusCode == HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
    }
}
