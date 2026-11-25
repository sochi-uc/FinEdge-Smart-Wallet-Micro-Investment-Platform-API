namespace Accrue.Domain;

public sealed class DomainException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}

/// <summary>Money rules. NGN only, kept to kobo (2 dp), bounded (OWASP A04).</summary>
public static class Money
{
    public const decimal Max = 100_000_000m;

    public static decimal Amount(decimal value, string field = "amount")
    {
        if (value <= 0) throw new DomainException(field, "Amount must be greater than zero.");
        if (value > Max) throw new DomainException(field, $"Amount must be at most {Max:N0}.");
        if (decimal.Round(value, 2) != value) throw new DomainException(field, "Amount can have at most 2 decimal places.");
        return value;
    }
}

public enum AccountKind
{
    Wallet = 0,
    Investment = 1,

    // System accounts are the "other side" of money entering or leaving the platform.
    // Their balances are naturally negative (a source) or positive (a sink).
    SystemFunding = 10,
    SystemPayout = 11,
    SystemInterest = 12,
}

public enum EntryType
{
    Deposit = 0,
    Withdrawal = 1,
    Transfer = 2,
    Invest = 3,
    Redeem = 4,
    Interest = 5,
}

/// <summary>
/// A ledger account. <see cref="Balance"/> is a cached sum of its postings, guarded by an optimistic
/// concurrency token so two concurrent debits can never both pass the same balance check.
/// </summary>
public sealed class Account
{
    private Account() { }

    public Guid Id { get; private set; }
    public string? OwnerId { get; private set; }
    public AccountKind Kind { get; private set; }
    public decimal Balance { get; private set; }
    public int Version { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsSystem => Kind >= AccountKind.SystemFunding;

    public static Account Open(string? ownerId, AccountKind kind, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), OwnerId = ownerId, Kind = kind, CreatedAt = now };

    /// <summary>System account with a fixed, well-known id so there is exactly one of each.</summary>
    public static Account System(Guid id, AccountKind kind, DateTimeOffset now) =>
        new() { Id = id, Kind = kind, CreatedAt = now };

    internal void Apply(decimal delta)
    {
        if (!IsSystem && Balance + delta < 0) throw new DomainException("amount", "Insufficient funds.");
        Balance += delta;
        Version++;
    }
}

public sealed class Posting
{
    private Posting() { }

    public Guid AccountId { get; private set; }

    /// <summary>Signed: negative debits the account, positive credits it.</summary>
    public decimal Amount { get; private set; }

    internal static Posting Create(Guid accountId, decimal amount) => new() { AccountId = accountId, Amount = amount };
}

/// <summary>
/// One balanced double-entry transaction: postings always sum to zero, so money is never created or
/// destroyed, only moved. Entries are immutable once written; corrections are new entries.
/// </summary>
public sealed class JournalEntry
{
    private readonly List<Posting> _postings = [];

    private JournalEntry() { }

    public Guid Id { get; private set; }
    public DateTimeOffset At { get; private set; }
    public EntryType Type { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public string? InitiatedBy { get; private set; }
    public IReadOnlyList<Posting> Postings => _postings;

    /// <summary>Moves <paramref name="amount"/> from one account to another and updates both balances atomically.</summary>
    public static JournalEntry Move(Account from, Account to, decimal amount, EntryType type, string description, string? initiatedBy, DateTimeOffset now)
    {
        Money.Amount(amount);
        if (from.Id == to.Id) throw new DomainException("amount", "Source and destination must differ.");

        from.Apply(-amount); // throws before anything changes if funds are insufficient
        to.Apply(amount);

        var entry = new JournalEntry
        {
            Id = Guid.NewGuid(),
            At = now,
            Type = type,
            Description = description.Length > 200 ? description[..200] : description,
            InitiatedBy = initiatedBy,
        };
        entry._postings.Add(Posting.Create(from.Id, -amount));
        entry._postings.Add(Posting.Create(to.Id, amount));
        return entry;
    }

    public bool IsBalanced => _postings.Sum(p => p.Amount) == 0;
}
