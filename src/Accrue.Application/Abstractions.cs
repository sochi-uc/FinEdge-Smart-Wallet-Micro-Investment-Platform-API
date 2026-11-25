using Accrue.Domain;
using Microsoft.EntityFrameworkCore;

namespace Accrue.Application;

/// <summary>
/// Persistence port. Application depends only on EF Core's abstractions (DbSet/IQueryable), the
/// pragmatic Clean Architecture choice: use cases can compose queries without a repository per table,
/// while the provider, mappings and migrations stay in Infrastructure.
/// </summary>
public interface IAppDbContext
{
    DbSet<Account> Accounts { get; }
    DbSet<JournalEntry> JournalEntries { get; }
    DbSet<InvestmentPlan> InvestmentPlans { get; }
    DbSet<Investment> Investments { get; }
    DbSet<InterestAccrual> InterestAccruals { get; }
    DbSet<IdempotencyRecord> IdempotencyRecords { get; }

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>Looks up other users for P2P transfers without exposing the identity store.</summary>
public interface IUserDirectory
{
    Task<UserRef?> FindByEmailAsync(string email, CancellationToken ct);
}

public sealed record UserRef(string Id, string DisplayName);

/// <summary>Per-user read-model cache (dashboard). Invalidated on every money movement.</summary>
public interface IDashboardCache
{
    bool TryGet(string userId, out DashboardDto? value);
    void Set(string userId, DashboardDto value);
    void Invalidate(params string[] userIds);
}

/// <summary>Stored outcome of a money request, keyed by (owner, Idempotency-Key).</summary>
public sealed class IdempotencyRecord
{
    public string OwnerId { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public int StatusCode { get; set; } // 0 = in progress
    public string? ResponseJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Well-known system accounts: the "other side" of money entering, leaving, or being paid as interest.</summary>
public static class SystemAccounts
{
    public static readonly Guid Funding = new("00000000-0000-0000-0000-00000000f001");
    public static readonly Guid Payout = new("00000000-0000-0000-0000-00000000f002");
    public static readonly Guid Interest = new("00000000-0000-0000-0000-00000000f003");
}

public enum ErrorKind
{
    None,
    NotFound,
    Validation,
    Conflict,
}

public sealed record Result<T>(T? Value, ErrorKind Error, IReadOnlyDictionary<string, string[]>? Errors = null, string? Detail = null)
{
    public static Result<T> Ok(T v) => new(v, ErrorKind.None);
    public static Result<T> NotFound() => new(default, ErrorKind.NotFound);
    public static Result<T> Conflict(string d) => new(default, ErrorKind.Conflict, Detail: d);
    public static Result<T> Invalid(string f, string m) => new(default, ErrorKind.Validation, new Dictionary<string, string[]> { [f] = [m] });
}
