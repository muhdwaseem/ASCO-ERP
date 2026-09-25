using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AegisErp.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Asco.Api.Tests;

public class EssTests(AscoFactory factory) : IClassFixture<AscoFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient NewClient()
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        c.DefaultRequestHeaders.Add("X-ASCO", "1");
        return c;
    }

    private static HttpRequestMessage Req(HttpMethod m, string url, int company, object? body = null)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Add("X-Company-Id", company.ToString());
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    [Fact]
    public async Task Employee_portal_shows_only_own_data_and_leave_flows_to_manager()
    {
        var owner = NewClient();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync("/api/auth/login", new { email = SeedData.DemoAdminEmail, password = SeedData.DemoAdminPassword })).StatusCode);
        var me = await owner.GetFromJsonAsync<JsonElement>("/api/auth/me");
        var company = me.GetProperty("companies")[0].GetProperty("id").GetInt32();
        var lk = await (await owner.SendAsync(Req(HttpMethod.Get, "/api/lookups", company))).Content.ReadFromJsonAsync<JsonElement>(Json);
        var expense = lk.GetProperty("accounts").EnumerateArray().First(a => a.GetProperty("type").GetString() == "Expense").GetProperty("id").GetInt32();

        var created = await owner.SendAsync(Req(HttpMethod.Post, "/api/employees", company, new
        {
            fullName = "Portal Tester", designation = "Clerk", costCenterId = (int?)null, joiningDate = "2024-01-01",
            basicSalary = 5000m, housingAllowance = 2000m, transportAllowance = 500m, otherAllowance = 0m,
            mobile = (string?)null, email = "portal.tester@example.com", bankName = (string?)null, iban = (string?)null,
            employeeExpenseAccountId = expense, notes = (string?)null,
        }));
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var empId = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetInt32();

        var grant = await owner.SendAsync(Req(HttpMethod.Post, $"/api/employees/{empId}/portal-access", company, new { email = "portal.tester@example.com", password = "Portal#Test2026" }));
        Assert.True(grant.IsSuccessStatusCode, await grant.Content.ReadAsStringAsync());

        // Employee signs in → portal only
        var emp = NewClient();
        Assert.Equal(HttpStatusCode.NoContent, (await emp.PostAsJsonAsync("/api/auth/login", new { email = "portal.tester@example.com", password = "Portal#Test2026" })).StatusCode);
        var empMe = await emp.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("Portal Tester", empMe.GetProperty("employee").GetProperty("fullName").GetString());
        Assert.Equal(0, empMe.GetProperty("companies").GetArrayLength());
        Assert.Equal("Portal Tester", (await emp.GetFromJsonAsync<JsonElement>("/api/ess/profile")).GetProperty("fullName").GetString());
        Assert.Equal(HttpStatusCode.OK, (await emp.GetAsync("/api/ess/payslips")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.SendAsync(Req(HttpMethod.Get, "/api/accounts", company))).StatusCode);

        var leave = await emp.PostAsJsonAsync("/api/ess/leave", new { type = "Annual", startDate = "2026-12-01", endDate = "2026-12-03", reason = "Family" });
        Assert.True(leave.StatusCode == HttpStatusCode.Created, await leave.Content.ReadAsStringAsync());
        var leaveId = (await leave.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetInt32();

        // Manager approves
        var decide = await owner.SendAsync(Req(HttpMethod.Post, $"/api/leave-requests/{leaveId}/decide", company, new { approved = true }));
        Assert.True(decide.IsSuccessStatusCode, await decide.Content.ReadAsStringAsync());
        var mine = await emp.GetFromJsonAsync<JsonElement>("/api/ess/leave");
        Assert.Equal("Approved", mine.GetProperty("requests")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Staff_login_is_not_an_employee()
    {
        var c = NewClient();
        await c.PostAsJsonAsync("/api/auth/login", new { email = SeedData.DemoAdminEmail, password = SeedData.DemoAdminPassword });
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/ess/profile")).StatusCode);
    }
}
