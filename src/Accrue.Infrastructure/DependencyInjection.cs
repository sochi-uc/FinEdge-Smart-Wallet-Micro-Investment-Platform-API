using Accrue.Application;
using Accrue.Infrastructure.Identity;
using Accrue.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Accrue.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Accrue")
            ?? throw new InvalidOperationException("Connection string 'Accrue' is not configured.");

        services.AddDbContext<AccrueDbContext>(o => o.UseSqlite(connection));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AccrueDbContext>());
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddMemoryCache(o => o.SizeLimit = 10_000);
        services.AddSingleton<IDashboardCache, MemoryDashboardCache>();
        services.AddScoped<AuthService>();
        services.AddHostedService<AccrualWorker>();

        services
            .AddIdentityCore<AppUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 12;
                o.Password.RequireDigit = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequiredUniqueChars = 4;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AccrueDbContext>();

        return services;
    }
}

internal sealed class UserDirectory(UserManager<AppUser> users) : IUserDirectory
{
    public async Task<UserRef?> FindByEmailAsync(string email, CancellationToken ct)
    {
        var u = await users.FindByEmailAsync(email);
        return u is null ? null : new UserRef(u.Id, u.DisplayName);
    }
}

internal sealed class MemoryDashboardCache(IMemoryCache cache) : IDashboardCache
{
    private static string Key(string id) => $"dash:{id}";

    public bool TryGet(string userId, out DashboardDto? value) => cache.TryGetValue(Key(userId), out value);

    public void Set(string userId, DashboardDto value) =>
        cache.Set(Key(userId), value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30), Size = 1 });

    public void Invalidate(params string[] userIds)
    {
        foreach (var id in userIds) cache.Remove(Key(id));
    }
}

/// <summary>Runs interest accrual shortly after startup and then hourly. Idempotent, so overlap is harmless.</summary>
internal sealed class AccrualWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<AccrualWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Accrual:Enabled", true)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var run = await scope.ServiceProvider.GetRequiredService<AccrualService>().RunAsync(null, stoppingToken);
                if (run.DaysPosted > 0)
                    log.LogInformation("Accrual through {Through}: {Days} day(s) on {Count} investment(s), {Paid} paid", run.Through, run.DaysPosted, run.InvestmentsProcessed, run.InterestPaid);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Interest accrual run failed; will retry next hour");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
