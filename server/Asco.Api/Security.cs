using System.Security.Claims;
using System.Threading.RateLimiting;
using AegisErp.Infrastructure;
using AegisErp.Infrastructure.Identity;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Identity;

namespace Asco.Api;

/// <summary>
/// Hardening for an internet-facing ASCO (anything that isn't the Development environment):
///  â€¢ refuses to start while a seeded demo login still has its published password (C-ERP's seed
///    data is in source) â€” set Security:DemoUsersPassword to rotate them on start;
///  â€¢ secure-only cookies, HSTS and a strict Content-Security-Policy;
///  â€¢ X-Forwarded-For/Proto from the reverse proxy / tunnel when Security:BehindProxy=true, so HTTPS
///    is detected and rate limits see the real client IP;
///  â€¢ a per-user/IP request budget on the whole API, on top of the sign-in limiter.
/// Configure with environment variables, e.g. Security__DemoUsersPassword, Security__BehindProxy.
/// </summary>
internal static class Security
{
    private static readonly (string Email, string Password)[] PublishedLogins =
    [
        (SeedData.DemoAdminEmail, SeedData.DemoAdminPassword),
        (SeedData.DemoFinanceEmail, SeedData.DemoFinancePassword),
        (SeedData.DemoViewerEmail, SeedData.DemoViewerPassword),
    ];

    public static void AddAscoSecurity(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
        var dev = builder.Environment.IsDevelopment();

        if (cfg.GetValue<bool>("Security:BehindProxy"))
            builder.Services.Configure<ForwardedHeadersOptions>(o =>
            {
                o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                // The app is only reachable through the proxy/tunnel, so trust what it forwards.
                o.KnownNetworks.Clear();
                o.KnownProxies.Clear();
            });

        builder.Services.PostConfigure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, o =>
        {
            if (!dev) o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.ExpireTimeSpan = TimeSpan.FromHours(cfg.GetValue<double?>("Security:SessionHours") ?? 8);
        });

        builder.Services.AddHsts(o => { o.MaxAge = TimeSpan.FromDays(180); o.IncludeSubDomains = true; });

        builder.Services.Configure<RateLimiterOptions>(o =>
        {
            var perMinute = cfg.GetValue<int?>("Security:ApiRequestsPerMinute") ?? 600;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                !ctx.Request.Path.StartsWithSegments("/api")
                    ? RateLimitPartition.GetNoLimiter("static")
                    : RateLimitPartition.GetFixedWindowLimiter(
                        ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1) }));
        });
    }

    /// <summary>Run before anything else so forwarded scheme/IP are known to every later step.</summary>
    public static void UseAscoSecurity(this WebApplication app)
    {
        if (app.Configuration.GetValue<bool>("Security:BehindProxy")) app.UseForwardedHeaders();
        if (!app.Environment.IsDevelopment()) app.UseHsts();
        var csp = !app.Environment.IsDevelopment();
        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            if (csp)
                h["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
            // Financial data is never cached; the app's hashed static files may be.
            if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path == "/" || ctx.Request.Path == "/index.html") h["Cache-Control"] = "no-store";
            await next();
        });
    }

    /// <summary>Outside Development, never run with C-ERP's published demo passwords.</summary>
    public static async Task GuardPublishedPasswordsAsync(WebApplication app)
    {
        if (app.Environment.IsDevelopment()) return;
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Asco.Security");
        var replacement = app.Configuration["Security:DemoUsersPassword"];
        foreach (var (email, published) in PublishedLogins)
        {
            var user = await users.FindByEmailAsync(email);
            if (user is null || !await users.CheckPasswordAsync(user, published)) continue;
            if (string.IsNullOrWhiteSpace(replacement) || replacement == published)
                throw new InvalidOperationException(
                    $"The login {email} still has the demo password published in the source code. " +
                    "Set Security__DemoUsersPassword (environment variable) to a new strong password before hosting.");
            var token = await users.GeneratePasswordResetTokenAsync(user);
            var res = await users.ResetPasswordAsync(user, token, replacement);
            if (!res.Succeeded)
                throw new InvalidOperationException($"Security__DemoUsersPassword was rejected: {string.Join(" ", res.Errors.Select(e => e.Description))}");
            log.LogWarning("Rotated the published demo password for {Email}.", email);
        }
    }
}
