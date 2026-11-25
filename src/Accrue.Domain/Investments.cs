namespace Accrue.Domain;

public sealed class InvestmentPlan
{
    public const decimal MaxRatePct = 50m;
    public const int MaxLockDays = 3650;

    private InvestmentPlan() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;

    /// <summary>Simple annual rate, e.g. 12.5 for 12.5% p.a.</summary>
    public decimal AnnualRatePct { get; private set; }

    public decimal MinAmount { get; private set; }
    public int LockDays { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static InvestmentPlan Create(string? name, string? description, decimal annualRatePct, decimal minAmount, int lockDays, DateTimeOffset now)
    {
        var plan = new InvestmentPlan { Id = Guid.NewGuid(), CreatedAt = now, IsActive = true };
        plan.Update(name, description, annualRatePct, minAmount, lockDays);
        return plan;
    }

    public void Update(string? name, string? description, decimal annualRatePct, decimal minAmount, int lockDays)
    {
        var n = (name ?? string.Empty).Trim();
        if (n.Length is 0 or > 60) throw new DomainException("name", "Name must be 1–60 characters.");
        var d = (description ?? string.Empty).Trim();
        if (d.Length > 300) throw new DomainException("description", "Description must be at most 300 characters.");
        if (annualRatePct <= 0 || annualRatePct > MaxRatePct) throw new DomainException("annualRatePct", $"Rate must be between 0 and {MaxRatePct}% a year.");
        if (decimal.Round(annualRatePct, 2) != annualRatePct) throw new DomainException("annualRatePct", "Rate can have at most 2 decimal places.");
        if (lockDays < 0 || lockDays > MaxLockDays) throw new DomainException("lockDays", $"Lock period must be 0–{MaxLockDays} days.");
        Name = n;
        Description = d;
        AnnualRatePct = annualRatePct;
        MinAmount = minAmount == 0 ? 0 : Money.Amount(minAmount, "minAmount");
        LockDays = lockDays;
    }

    public void SetActive(bool active) => IsActive = active;
}

public enum InvestmentStatus
{
    Active = 0,
    Redeemed = 1,
}

/// <summary>
/// A position in a plan. Principal and earned interest sit in the investment's own ledger account;
/// interest accrues daily (simple, on principal) and is posted once per day per investment.
/// </summary>
public sealed class Investment
{
    private Investment() { }

    public Guid Id { get; private set; }
    public string OwnerId { get; private set; } = string.Empty;
    public Guid PlanId { get; private set; }
    public string PlanName { get; private set; } = string.Empty;
    public Guid AccountId { get; private set; }
    public decimal Principal { get; private set; }

    /// <summary>Rate captured at purchase — later plan edits never change existing positions.</summary>
    public decimal AnnualRatePct { get; private set; }

    public DateOnly StartDate { get; private set; }
    public DateOnly MaturesOn { get; private set; }

    /// <summary>Last day whose interest has been posted. Starts the day before <see cref="StartDate"/>.</summary>
    public DateOnly AccruedThrough { get; private set; }

    public decimal InterestEarned { get; private set; }
    public InvestmentStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RedeemedAt { get; private set; }

    public decimal DailyInterest => decimal.Round(Principal * AnnualRatePct / 100m / 365m, 2, MidpointRounding.ToEven);

    public static Investment Open(string ownerId, InvestmentPlan plan, Guid accountId, decimal principal, DateTimeOffset now)
    {
        if (!plan.IsActive) throw new DomainException("planId", "This plan is not open for new investments.");
        Money.Amount(principal);
        if (principal < plan.MinAmount) throw new DomainException("amount", $"The minimum for {plan.Name} is {plan.MinAmount:N2}.");
        var start = DateOnly.FromDateTime(now.UtcDateTime);
        return new Investment
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            PlanId = plan.Id,
            PlanName = plan.Name,
            AccountId = accountId,
            Principal = principal,
            AnnualRatePct = plan.AnnualRatePct,
            StartDate = start,
            MaturesOn = start.AddDays(plan.LockDays),
            AccruedThrough = start.AddDays(-1),
            Status = InvestmentStatus.Active,
            CreatedAt = now,
        };
    }

    /// <summary>Days still to accrue, up to and including <paramref name="through"/> (never past redemption).</summary>
    public IEnumerable<DateOnly> DaysToAccrue(DateOnly through)
    {
        if (Status != InvestmentStatus.Active) yield break;
        for (var d = AccruedThrough.AddDays(1); d <= through; d = d.AddDays(1)) yield return d;
    }

    public void MarkAccrued(DateOnly day, decimal amount)
    {
        if (day != AccruedThrough.AddDays(1)) throw new InvalidOperationException("Accrual days must be posted in order.");
        AccruedThrough = day;
        InterestEarned += amount;
    }

    public bool CanRedeem(DateOnly today) => Status == InvestmentStatus.Active && today >= MaturesOn;

    public void MarkRedeemed(DateOnly today, DateTimeOffset now)
    {
        if (Status != InvestmentStatus.Active) throw new DomainException("id", "This investment has already been redeemed.");
        if (today < MaturesOn) throw new DomainException("id", $"Locked until {MaturesOn:yyyy-MM-dd}.");
        Status = InvestmentStatus.Redeemed;
        RedeemedAt = now;
    }
}

/// <summary>One row per investment per day. The unique key makes accrual idempotent: re-running the job can't pay twice.</summary>
public sealed class InterestAccrual
{
    private InterestAccrual() { }

    public Guid InvestmentId { get; private set; }
    public DateOnly Day { get; private set; }
    public decimal Amount { get; private set; }
    public Guid JournalEntryId { get; private set; }

    public static InterestAccrual Create(Guid investmentId, DateOnly day, decimal amount, Guid entryId) =>
        new() { InvestmentId = investmentId, Day = day, Amount = amount, JournalEntryId = entryId };
}
