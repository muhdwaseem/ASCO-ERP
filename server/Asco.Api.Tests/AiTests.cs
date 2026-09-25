using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AegisErp.Infrastructure;
using Asco.Api.Ai;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Asco.Api.Tests;

public class AiTests(AscoFactory factory) : IClassFixture<AscoFactory>
{
    private async Task<(HttpClient c, int company)> Owner()
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        c.DefaultRequestHeaders.Add("X-ASCO", "1");
        await c.PostAsJsonAsync("/api/auth/login", new { email = SeedData.DemoAdminEmail, password = SeedData.DemoAdminPassword });
        var me = await c.GetFromJsonAsync<JsonElement>("/api/auth/me");
        var company = me.GetProperty("companies")[0].GetProperty("id").GetInt32();
        c.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        return (c, company);
    }

    [Fact]
    public async Task Every_ai_tool_reads_the_active_company_without_error()
    {
        var (_, company) = await Owner(); // ensures the app (and seed) is up
        using var scope = factory.Services.CreateScope();
        var current = scope.ServiceProvider.GetRequiredService<CurrentCompany>();
        current.CompanyId = company;
        var tools = scope.ServiceProvider.GetRequiredService<AiTools>();
        var empty = JsonDocument.Parse("{}").RootElement;
        foreach (var spec in AiTools.Specs)
        {
            var input = spec.Name == "search_general_ledger" ? JsonDocument.Parse("""{"from":"2000-01-01","to":"2100-12-31"}""").RootElement : empty;
            var (result, isError) = await tools.ExecuteAsync(spec.Name, input);
            Assert.False(isError, $"{spec.Name}: {result}");
            Assert.False(string.IsNullOrWhiteSpace(result));
        }
        var (tb, _) = await tools.ExecuteAsync("get_trial_balance", JsonDocument.Parse("""{"from":"2000-01-01","to":"2100-12-31"}""").RootElement);
        Assert.Contains("\"isBalanced\":true", tb);
        var (unknown, err) = await tools.ExecuteAsync("delete_everything", empty);
        Assert.True(err);
        Assert.Contains("Unknown tool", unknown);
    }

    [Fact]
    public async Task Insights_flag_overdue_receivables()
    {
        var (c, _) = await Owner();
        var list = await c.GetFromJsonAsync<JsonElement>("/api/ai/insights");
        Assert.Contains(list.EnumerateArray(), x => x.GetProperty("area").GetString() == "Receivables");
    }

    [Fact]
    public async Task Ask_reports_unconfigured_instead_of_failing()
    {
        var (c, _) = await Owner();
        var status = await c.GetFromJsonAsync<JsonElement>("/api/ai/status");
        if (status.GetProperty("askConfigured").GetBoolean()) return; // a real key is set on this machine: don't spend money in tests
        var r = await c.PostAsJsonAsync("/api/ai/ask", new { question = "What is my net profit this year?" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
    }

    [Fact]
    public async Task Bill_scan_without_scanner_key_is_a_clear_error_not_a_crash()
    {
        var (c, _) = await Owner();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent("%PDF-1.4 fake"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "bill.pdf");
        var r = await c.PostAsync("/api/ai/scan-bill", form);
        Assert.True(r.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.OK, $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
    }
}
