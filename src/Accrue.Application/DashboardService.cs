using Accrue.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Accrue.Application;

/// <summary>Per-user analytics, cached briefly and invalidated by every money movement.</summary>
public sealed class DashboardService(IAppDbContext db, WalletService wallets, IDashboardCache cache, TimeProvider clock)
{
    public async Task<DashboardDto> GetAsync(string ownerId, CancellationToken ct)
    {
        if (cache.TryGet(ownerId, out var cached) && cached is not null) return cached;

        var wallet = await wallets.GetOrOpenWalletAsync(ownerId, ct);
        var investments = await db.Investments.AsNoTracking()
            .Where(i => i.OwnerId == ownerId && i.Status == InvestmentStatus.Active).ToListAsync(ct);

        var since = clock.GetUtcNow().AddDays(-30);
        var recentEntries = await db.JournalEntries.AsNoTracking()
            .Where(e => e.At >= since && e.Postings.Any(p => p.AccountId == wallet.Id))
            .OrderByDescending(e => e.At)
            .ToListAsync(ct);
        var recent = recentEntries.Select(e => WalletService.ToDto(e, wallet.Id)).ToList();

        var invested = investments.Sum(i => i.Principal);
        var earned = investments.Sum(i => i.InterestEarned);
        var dto = new DashboardDto(
            wallet.Balance,
            invested,
            earned,
            wallet.Balance + invested + earned,
            investments.Count,
            investments.Sum(i => i.DailyInterest) * 30,
            recent.Where(t => t.BalanceImpact > 0).Sum(t => t.BalanceImpact),
            -recent.Where(t => t.BalanceImpact < 0).Sum(t => t.BalanceImpact),
            recent.Take(10).ToList(),
            clock.GetUtcNow());
        cache.Set(ownerId, dto);
        return dto;
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<WalletService>();
        services.AddScoped<InvestmentService>();
        services.AddScoped<AccrualService>();
        services.AddScoped<DashboardService>();
        return services;
    }
}
