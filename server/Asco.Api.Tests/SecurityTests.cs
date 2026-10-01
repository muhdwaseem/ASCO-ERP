using System.Net;
using System.Net.Http.Json;
using AegisErp.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Asco.Api.Tests;

/// <summary>An internet-facing (Production) instance on its own throwaway database.</summary>
public sealed class ProductionFactory(string? demoPassword) : WebApplicationFactory<Program>
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"asco_prod_{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_db}");
        builder.UseSetting("Seed:DemoData", "true");
        if (demoPassword is not null) builder.UseSetting("Security:DemoUsersPassword", demoPassword);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" }) try { File.Delete(f); } catch { /* best effort */ }
    }
}

public class SecurityTests
{
    private static async Task<HttpStatusCode> Login(HttpClient c, string email, string password)
    {
        c.DefaultRequestHeaders.Remove("X-ASCO");
        c.DefaultRequestHeaders.Add("X-ASCO", "1");
        return (await c.PostAsJsonAsync("/api/auth/login", new { email, password })).StatusCode;
    }

    [Fact]
    public void Hosted_instance_refuses_to_start_with_published_demo_passwords()
    {
        using var f = new ProductionFactory(null);
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("demo password", ex.ToString());
    }

    [Fact]
    public async Task Hosted_instance_rotates_demo_passwords_and_sends_security_headers()
    {
        const string fresh = "Client#Test2026!x";
        using var f = new ProductionFactory(fresh);
        var c = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://asco.example.com") }); // HSTS is never sent to localhost
        Assert.Equal(HttpStatusCode.Unauthorized, await Login(c, SeedData.DemoAdminEmail, SeedData.DemoAdminPassword));
        Assert.Equal(HttpStatusCode.NoContent, await Login(c, SeedData.DemoAdminEmail, fresh));

        var res = await c.GetAsync("/health");
        Assert.Contains("default-src 'self'", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.True(res.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Api_rejects_cross_site_posts_and_anonymous_reads()
    {
        using var f = new ProductionFactory("Client#Test2026!x");
        var c = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/auth/login", new { email = "x", password = "y" })).StatusCode); // no X-ASCO header
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/reports/profit-and-loss")).StatusCode);
    }
}
