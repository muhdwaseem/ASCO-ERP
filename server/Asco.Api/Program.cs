// ASCO API — a stateless JSON API over C-ERP's unmodified accounting core
// (AegisErp.Domain + AegisErp.Infrastructure). Stateless = any instance can serve any request,
// so capacity grows by adding instances behind a load balancer.

using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AegisErp.Infrastructure;
using AegisErp.Infrastructure.Identity;
using Asco.Api;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port)) builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
});

// ── Identity: same AppUser store as C-ERP, so the same people sign in to both apps ──
builder.Services.AddAuthentication(o =>
{
    o.DefaultScheme = IdentityConstants.ApplicationScheme;
    o.DefaultSignInScheme = IdentityConstants.ExternalScheme;
}).AddIdentityCookies();
builder.Services.AddAuthorization();
builder.Services.AddIdentityCore<AppUser>(o =>
{
    o.SignIn.RequireConfirmedAccount = false;
    o.Password.RequiredLength = 8;
    o.Lockout.MaxFailedAccessAttempts = 5;
    o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    o.Lockout.AllowedForNewUsers = true;
})
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AegisDbContext>()
    .AddSignInManager()
    .AddClaimsPrincipalFactory<AppUserClaimsPrincipalFactory>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "asco.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.SlidingExpiration = true;
    o.ExpireTimeSpan = TimeSpan.FromHours(10);
    // An API answers 401/403 — it never redirects to an HTML login page.
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});

// Cookies must decrypt on every instance: share the key ring when running more than one.
var dp = builder.Services.AddDataProtection().SetApplicationName("asco");
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath)) dp.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

// ── C-ERP accounting core (DbContext factory, company scoping, all domain services) ──
builder.Services.AddAegisInfrastructure(builder.Configuration);
// C-ERP's web app already runs the invoice-automation background job against the same database;
// running it here too would generate recurring invoices / reminders twice. Remove it.
foreach (var d in builder.Services.Where(d => d.ImplementationType?.Name == "InvoiceAutomationHostedService").ToList())
    builder.Services.Remove(d);

builder.Services.AddScoped<CompanyScopeFilter>();

// Brute-force protection on sign-in, per client IP.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);
builder.Services.AddProblemDetails();

var app = builder.Build();

await DatabaseStartup.RunAsync(app);

app.UseResponseCompression();
app.UseExceptionHandler();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Cache-Control"] = "no-store"; // financial data is never cached by browsers
    // CSRF guard for cookie auth: state-changing requests must carry a custom header, which a
    // cross-site form post cannot set without a CORS preflight (and we allow no cross-origin calls).
    if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)
        && ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Headers["X-ASCO"] != "1")
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { error = "Missing X-ASCO header." });
        return;
    }
    await next();
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })); // load-balancer probe
app.MapAuthEndpoints();
app.MapReadEndpoints();
app.MapWriteEndpoints();
app.MapEssEndpoints();

app.Run();

public partial class Program { }
