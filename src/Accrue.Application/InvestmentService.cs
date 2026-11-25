using Accrue.Domain;
using Microsoft.EntityFrameworkCore;

namespace Accrue.Application;

public sealed class InvestmentService(IAppDbContext db, WalletService wallets, IDashboardCache cache, TimeProvider clock)
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<IReadOnlyList<PlanDto>> PlansAsync(bool includeInactive, CancellationToken ct)
    {
        var q = db.InvestmentPlans.AsNoTracking();
        if (!includeInactive) q = q.Where(p => p.IsActive);
        return (await q.ToListAsync(ct)).OrderBy(p => p.LockDays).ThenBy(p => p.Name).Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<InvestmentDto>> MineAsync(string ownerId, CancellationToken ct) =>
        (await db.Investments.AsNoTracking().Where(i => i.OwnerId == ownerId).ToListAsync(ct))
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => ToDto(i, Today))
            .ToList();

    public async Task<Result<InvestmentDto>> InvestAsync(string ownerId, InvestRequest r, CancellationToken ct)
    {
        if (r.PlanId is null) return Result<InvestmentDto>.Invalid("planId", "Choose a plan.");
        if (r.Amount is null) return Result<InvestmentDto>.Invalid("amount", "Amount is required.");
        var plan = await db.InvestmentPlans.SingleOrDefaultAsync(p => p.Id == r.PlanId, ct);
        if (plan is null) return Result<InvestmentDto>.Invalid("planId", "Unknown plan.");

        var wallet = await wallets.GetOrOpenWalletAsync(ownerId, ct);
        var now = clock.GetUtcNow();
        try
        {
            var account = Account.Open(ownerId, AccountKind.Investment, now);
            var investment = Investment.Open(ownerId, plan, account.Id, r.Amount.Value, now);
            db.Accounts.Add(account);
            db.JournalEntries.Add(JournalEntry.Move(wallet, account, r.Amount.Value, EntryType.Invest, $"Invested in {plan.Name}", ownerId, now));
            db.Investments.Add(investment);
            await db.SaveChangesAsync(ct);
            cache.Invalidate(ownerId);
            return Result<InvestmentDto>.Ok(ToDto(investment, Today));
        }
        catch (DomainException ex)
        {
            return Result<InvestmentDto>.Invalid(ex.Field, ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<InvestmentDto>.Conflict("Your balance changed while this was processing. Please retry.");
        }
    }

    /// <summary>Moves principal + earned interest back to the wallet once the lock period has passed.</summary>
    public async Task<Result<InvestmentDto>> RedeemAsync(string ownerId, Guid investmentId, CancellationToken ct)
    {
        // Scoped by owner: someone else's id is indistinguishable from a missing one (404, no BOLA leak).
        var inv = await db.Investments.SingleOrDefaultAsync(i => i.Id == investmentId && i.OwnerId == ownerId, ct);
        if (inv is null) return Result<InvestmentDto>.NotFound();

        var account = await db.Accounts.SingleAsync(a => a.Id == inv.AccountId, ct);
        var wallet = await wallets.GetOrOpenWalletAsync(ownerId, ct);
        var now = clock.GetUtcNow();
        try
        {
            inv.MarkRedeemed(Today, now);
            db.JournalEntries.Add(JournalEntry.Move(account, wallet, account.Balance, EntryType.Redeem, $"Redeemed {inv.PlanName}", ownerId, now));
            await db.SaveChangesAsync(ct);
            cache.Invalidate(ownerId);
            return Result<InvestmentDto>.Ok(ToDto(inv, Today));
        }
        catch (DomainException ex)
        {
            return Result<InvestmentDto>.Invalid(ex.Field, ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<InvestmentDto>.Conflict("This investment changed while processing. Please retry.");
        }
    }

    // ------------------------------------------------------------------ admin

    public async Task<Result<PlanDto>> CreatePlanAsync(PlanRequest r, CancellationToken ct)
    {
        try
        {
            var plan = InvestmentPlan.Create(r.Name, r.Description, r.AnnualRatePct ?? 0, r.MinAmount ?? 0, r.LockDays ?? 0, clock.GetUtcNow());
            db.InvestmentPlans.Add(plan);
            await db.SaveChangesAsync(ct);
            return Result<PlanDto>.Ok(ToDto(plan));
        }
        catch (DomainException ex)
        {
            return Result<PlanDto>.Invalid(ex.Field, ex.Message);
        }
    }

    public async Task<Result<PlanDto>> UpdatePlanAsync(Guid id, PlanRequest r, CancellationToken ct)
    {
        var plan = await db.InvestmentPlans.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null) return Result<PlanDto>.NotFound();
        try
        {
            plan.Update(r.Name ?? plan.Name, r.Description ?? plan.Description, r.AnnualRatePct ?? plan.AnnualRatePct,
                r.MinAmount ?? plan.MinAmount, r.LockDays ?? plan.LockDays);
            if (r.IsActive is { } active) plan.SetActive(active);
            await db.SaveChangesAsync(ct);
            return Result<PlanDto>.Ok(ToDto(plan));
        }
        catch (DomainException ex)
        {
            return Result<PlanDto>.Invalid(ex.Field, ex.Message);
        }
    }

    internal static PlanDto ToDto(InvestmentPlan p) => new(p.Id, p.Name, p.Description, p.AnnualRatePct, p.MinAmount, p.LockDays, p.IsActive);

    internal static InvestmentDto ToDto(Investment i, DateOnly today) => new(
        i.Id, i.PlanId, i.PlanName, i.Principal, i.InterestEarned, i.Principal + i.InterestEarned, i.AnnualRatePct, i.DailyInterest,
        i.StartDate, i.MaturesOn, i.CanRedeem(today), i.Status.ToString().ToLowerInvariant());
}
