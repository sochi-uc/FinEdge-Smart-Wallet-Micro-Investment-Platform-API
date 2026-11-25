using Accrue.Domain;
using Microsoft.EntityFrameworkCore;

namespace Accrue.Application;

/// <summary>
/// Posts daily interest for every active investment, catching up any missed days. Each (investment, day)
/// is written once — enforced by a unique key and by <see cref="Investment.AccruedThrough"/> — so the job
/// can run as often as you like (startup, hourly, manual) without ever double-paying.
/// v1 instead paid the full ROI every minute and never saved it.
/// </summary>
public sealed class AccrualService(IAppDbContext db, IDashboardCache cache, TimeProvider clock)
{
    public async Task<AccrualRunDto> RunAsync(DateOnly? through, CancellationToken ct)
    {
        // Only complete days accrue: up to yesterday (UTC) unless an earlier date is requested.
        var yesterday = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(-1);
        var until = through is { } t && t < yesterday ? t : yesterday;

        var active = await db.Investments.Where(i => i.Status == InvestmentStatus.Active).ToListAsync(ct);
        var interest = await db.Accounts.SingleAsync(a => a.Id == SystemAccounts.Interest, ct);
        int processed = 0, days = 0;
        decimal paid = 0;

        foreach (var inv in active)
        {
            var pending = inv.DaysToAccrue(until).ToList();
            if (pending.Count == 0) continue;
            var account = await db.Accounts.SingleAsync(a => a.Id == inv.AccountId, ct);
            var daily = inv.DailyInterest;
            if (daily <= 0) continue; // tiny principal: rounds to zero kobo per day

            foreach (var day in pending)
            {
                var at = new DateTimeOffset(day.ToDateTime(new TimeOnly(23, 59, 59)), TimeSpan.Zero);
                var entry = JournalEntry.Move(interest, account, daily, EntryType.Interest, $"Interest · {inv.PlanName} · {day:yyyy-MM-dd}", null, at);
                db.JournalEntries.Add(entry);
                db.InterestAccruals.Add(InterestAccrual.Create(inv.Id, day, daily, entry.Id));
                inv.MarkAccrued(day, daily);
                days++;
                paid += daily;
            }

            // Commit per investment: a failure on one never blocks or half-pays another.
            await db.SaveChangesAsync(ct);
            cache.Invalidate(inv.OwnerId);
            processed++;
        }

        return new AccrualRunDto(until, processed, days, paid);
    }
}
