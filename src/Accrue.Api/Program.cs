using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Accrue.Api;
using Accrue.Application;
using Accrue.Infrastructure;
using Accrue.Infrastructure.Identity;
using Accrue.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var isDev = builder.Environment.IsDevelopment();

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 64 * 1024; // every request body here is tiny
});

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// --- JWT configuration: fail fast on a missing or weak key outside Development (v1 hard-coded two keys) ---
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32)
{
    if (!isDev) throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes. Set it via environment variable or a secret store.");
    jwt.SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)); // ephemeral dev key: tokens die on restart
    builder.Services.PostConfigure<JwtOptions>(o => o.SigningKey = jwt.SigningKey);
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false; // keep "sub"/"role" as issued
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256], // no "alg: none", no algorithm confusion
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
            RoleClaimType = "role",
        };
    });

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(Endpoints.AdminPolicy, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin));

// --- JSON: reject unknown properties (mass assignment — v1 let clients send "isAdmin": true) ------------
builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    o.SerializerOptions.MaxDepth = 16;
});
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(o => o.ThrowOnBadRequest = false);
builder.Services.AddScoped<IdempotencyFilter>();

// --- Rate limiting -----------------------------------------------------------------------------------------
var authPermit = builder.Configuration.GetValue("RateLimiting:AuthPermitPerMinute", 10);
var moneyPermit = builder.Configuration.GetValue("RateLimiting:MoneyPermitPerMinute", 30);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(Endpoints.AuthRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authPermit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy(Endpoints.MoneyRateLimit, ctx => RateLimitPartition.GetSlidingWindowLimiter(
        ctx.User.FindFirst("sub")?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new SlidingWindowRateLimiterOptions { PermitLimit = moneyPermit, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6, QueueLimit = 0 }));
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => RateLimitPartition.GetTokenBucketLimiter(
        ctx.User.FindFirst("sub")?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new TokenBucketRateLimiterOptions { TokenLimit = 120, TokensPerPeriod = 60, ReplenishmentPeriod = TimeSpan.FromSeconds(10), QueueLimit = 0 }));
});

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi();
if (!isDev) builder.Services.AddHsts(o => o.MaxAge = TimeSpan.FromDays(365));

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AccrueDbContext>();
    if (app.Configuration.GetValue("Database:MigrateOnStartup", true)) await db.Database.MigrateAsync();
    await DbInitializer.SeedAsync(db, TimeProvider.System);
    var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    if (!await roles.RoleExistsAsync(Roles.Admin)) await roles.CreateAsync(new IdentityRole(Roles.Admin));
}

app.UseExceptionHandler();
app.UseStatusCodePages();
if (!isDev)
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// API-only security headers: nothing here should ever be rendered or framed by a browser.
app.Use((ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        var h = ctx.Response.Headers;
        h.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
        h.XContentTypeOptions = "nosniff";
        h.XFrameOptions = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h.CacheControl = "no-store";
        return Task.CompletedTask;
    });
    return next(ctx);
});

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

// OpenAPI document only in Development (v1 exposed Swagger UI in every environment).
if (isDev) app.MapOpenApi().AllowAnonymous();
app.MapHealthChecks("/healthz").AllowAnonymous().DisableRateLimiting();
app.MapAccrue();

await app.RunAsync();

public partial class Program;
