using Accrue.Domain;

namespace Accrue.Tests;

public sealed class DomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Every_journal_entry_balances_to_zero()
    {
        var funding = Account.System(Guid.NewGuid(), AccountKind.SystemFunding, Now);
        var wallet = Account.Open("u1", AccountKind.Wallet, Now);
        var entry = JournalEntry.Move(funding, wallet, 250.75m, EntryType.Deposit, "d", "u1", Now);

        Assert.True(entry.IsBalanced);
        Assert.Equal(250.75m, wallet.Balance);
        Assert.Equal(-250.75m, funding.Balance);
    }

    [Fact]
    public void User_accounts_can_never_go_negative_and_a_failed_move_changes_nothing()
    {
        var a = Account.Open("u1", AccountKind.Wallet, Now);
        var b = Account.Open("u2", AccountKind.Wallet, Now);
        var ex = Assert.Throws<DomainException>(() => JournalEntry.Move(a, b, 1m, EntryType.Transfer, "t", "u1", Now));
        Assert.Equal("Insufficient funds.", ex.Message);
        Assert.Equal(0m, a.Balance);
        Assert.Equal(0m, b.Balance);
        Assert.Equal(0, a.Version);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("10.001")]
    [InlineData("100000000.01")]
    public void Invalid_amounts_are_rejected(string amount) =>
        Assert.Throws<DomainException>(() => Money.Amount(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Daily_interest_is_simple_and_rounded_to_kobo()
    {
        var plan = InvestmentPlan.Create("Flex", "", 8m, 0, 0, Now);
        var inv = Investment.Open("u1", plan, Guid.NewGuid(), 36_500m, Now);
        Assert.Equal(8.00m, inv.DailyInterest); // 36,500 × 8% ÷ 365

        var days = inv.DaysToAccrue(DateOnly.FromDateTime(Now.UtcDateTime).AddDays(2)).ToList();
        Assert.Equal(3, days.Count); // start day, +1, +2
    }

    [Fact]
    public void Accrual_days_must_be_posted_in_order_and_never_twice()
    {
        var plan = InvestmentPlan.Create("Flex", "", 10m, 0, 0, Now);
        var inv = Investment.Open("u1", plan, Guid.NewGuid(), 1000m, Now);
        var start = inv.StartDate;
        inv.MarkAccrued(start, 0.27m);
        Assert.Throws<InvalidOperationException>(() => inv.MarkAccrued(start, 0.27m));
        Assert.Empty(inv.DaysToAccrue(start));
    }

    [Fact]
    public void Locked_investments_cannot_be_redeemed_early()
    {
        var plan = InvestmentPlan.Create("Target 90", "", 14m, 0, 90, Now);
        var inv = Investment.Open("u1", plan, Guid.NewGuid(), 5000m, Now);
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        Assert.False(inv.CanRedeem(today));
        Assert.Throws<DomainException>(() => inv.MarkRedeemed(today, Now));
        Assert.True(inv.CanRedeem(today.AddDays(90)));
    }

    [Fact]
    public void Plan_minimum_and_rate_cap_are_enforced()
    {
        var plan = InvestmentPlan.Create("Fixed", "", 18m, 10_000m, 365, Now);
        Assert.Equal("amount", Assert.Throws<DomainException>(() => Investment.Open("u1", plan, Guid.NewGuid(), 500m, Now)).Field);
        Assert.Equal("annualRatePct", Assert.Throws<DomainException>(() => InvestmentPlan.Create("Ponzi", "", 10_000m, 0, 0, Now)).Field);
    }
}
