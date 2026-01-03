
using FinEdge.Api.Data;
using Microsoft.Extensions.DependencyInjection;

namespace FinEdge.Api.Services;

public class InvestmentEarningsService : BackgroundService
{
    private readonly IServiceScopeFactory _factory;
    public InvestmentEarningsService(IServiceScopeFactory factory) => _factory = factory;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = _factory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            foreach (var inv in db.Investments)
            {
                var plan = db.Plans.First(p => p.Id == inv.PlanId);
                var user = db.Users.First(u => u.Id == inv.UserId);
                var earnings = inv.Amount * plan.RoiPercent / 100m;
                user.Wallet.Balance += earnings;
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
