using Accrue.Domain;
using Microsoft.EntityFrameworkCore;

namespace Accrue.Application;

/// <summary>
/// Wallet use cases. The owner id always comes from the authenticated token (never the request),
/// so one user can never touch another's wallet (OWASP API1: BOLA).
/// </summary>
public sealed class WalletService(IAppDbContext db, IUserDirectory users, IDashboardCache cache, TimeProvider clock)
{
    public const int PageMax = 100;

    public async Task<Account> GetOrOpenWalletAsync(string ownerId, CancellationToken ct)
    {
        var wallet = await db.Accounts.SingleOrDefaultAsync(a => a.OwnerId == ownerId && a.Kind == AccountKind.Wallet, ct);
        if (wallet is not null) return wallet;
        wallet = Account.Open(ownerId, AccountKind.Wallet, clock.GetUtcNow());
        db.Accounts.Add(wallet);
        await db.SaveChangesAsync(ct);
        return wallet;
    }

    public async Task<WalletDto> GetAsync(string ownerId, CancellationToken ct)
    {
        var w = await GetOrOpenWalletAsync(ownerId, ct);
        return new WalletDto(w.Id, w.Balance, "NGN");
    }

    /// <summary>Simulated card/bank funding: money enters from the system funding account.</summary>
    public async Task<Result<WalletDto>> DepositAsync(string ownerId, AmountRequest r, CancellationToken ct)
    {
        var wallet = await GetOrOpenWalletAsync(ownerId, ct);
        var source = await SystemAsync(SystemAccounts.Funding, ct);
        return await MoveAsync(ownerId, r.Amount, source, wallet, wallet, EntryType.Deposit, Label("Deposit", r.Reference), ct);
    }

    public async Task<Result<WalletDto>> WithdrawAsync(string ownerId, AmountRequest r, CancellationToken ct)
    {
        var wallet = await GetOrOpenWalletAsync(ownerId, ct);
        var sink = await SystemAsync(SystemAccounts.Payout, ct);
        return await MoveAsync(ownerId, r.Amount, wallet, sink, wallet, EntryType.Withdrawal, Label("Withdrawal", r.Reference), ct);
    }

    public async Task<Result<WalletDto>> TransferAsync(string ownerId, string ownerName, TransferRequest r, CancellationToken ct)
    {
        var email = (r.ToEmail ?? string.Empty).Trim();
        if (email.Length is 0 or > 254) return Result<WalletDto>.Invalid("toEmail", "Enter the recipient's email.");
        var recipient = await users.FindByEmailAsync(email, ct);
        if (recipient is null) return Result<WalletDto>.Invalid("toEmail", "No Accrue user has that email.");
        if (recipient.Id == ownerId) return Result<WalletDto>.Invalid("toEmail", "You can't send money to yourself.");

        var wallet = await GetOrOpenWalletAsync(ownerId, ct);
        var theirs = await GetOrOpenWalletAsync(recipient.Id, ct);
        var note = Trim(r.Note, 100);
        var result = await MoveAsync(ownerId, r.Amount, wallet, theirs, wallet, EntryType.Transfer,
            $"{ownerName} → {recipient.DisplayName}{(note.Length > 0 ? $" · {note}" : string.Empty)}", ct);
        if (result.Error == ErrorKind.None) cache.Invalidate(recipient.Id);
        return result;
    }

    /// <summary>Wallet history, newest first, keyset-paginated by time (stable under inserts).</summary>
    public async Task<PageDto<TransactionDto>> HistoryAsync(string ownerId, DateTimeOffset? before, int limit, CancellationToken ct)
    {
        var w = await GetOrOpenWalletAsync(ownerId, ct);
        limit = Math.Clamp(limit, 1, PageMax);
        var q = db.JournalEntries.AsNoTracking().Where(e => e.Postings.Any(p => p.AccountId == w.Id));
        if (before is { } b) q = q.Where(e => e.At < b);
        var rows = await q.OrderByDescending(e => e.At).Take(limit + 1).ToListAsync(ct);
        var items = rows.Take(limit).Select(e => ToDto(e, w.Id)).ToList();
        return new PageDto<TransactionDto>(items, rows.Count > limit ? items[^1].At : null);
    }

    internal static TransactionDto ToDto(JournalEntry e, Guid accountId)
    {
        var impact = e.Postings.Where(p => p.AccountId == accountId).Sum(p => p.Amount);
        return new TransactionDto(e.Id, e.At, e.Type.ToString().ToLowerInvariant(), Math.Abs(impact), impact, e.Description);
    }

    private async Task<Result<WalletDto>> MoveAsync(
        string ownerId, decimal? amount, Account from, Account to, Account wallet, EntryType type, string description, CancellationToken ct)
    {
        if (amount is null) return Result<WalletDto>.Invalid("amount", "Amount is required.");
        try
        {
            db.JournalEntries.Add(JournalEntry.Move(from, to, amount.Value, type, description, ownerId, clock.GetUtcNow()));
            await db.SaveChangesAsync(ct);
        }
        catch (DomainException ex)
        {
            return Result<WalletDto>.Invalid(ex.Field, ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<WalletDto>.Conflict("Your balance changed while this was processing. Please retry.");
        }

        cache.Invalidate(ownerId);
        return Result<WalletDto>.Ok(new WalletDto(wallet.Id, wallet.Balance, "NGN"));
    }

    internal Task<Account> SystemAsync(Guid id, CancellationToken ct) => db.Accounts.SingleAsync(a => a.Id == id, ct);

    private static string Label(string kind, string? reference) =>
        Trim(reference, 60) is { Length: > 0 } r ? $"{kind} · {r}" : kind;

    private static string Trim(string? s, int max)
    {
        var t = (s ?? string.Empty).Trim();
        return t.Length > max ? t[..max] : t;
    }
}
