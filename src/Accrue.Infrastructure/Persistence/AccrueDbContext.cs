using Accrue.Application;
using Accrue.Domain;
using Accrue.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Accrue.Infrastructure.Persistence;

public sealed class AccrueDbContext(DbContextOptions<AccrueDbContext> options) : IdentityDbContext<AppUser>(options), IAppDbContext
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<InvestmentPlan> InvestmentPlans => Set<InvestmentPlan>();
    public DbSet<Investment> Investments => Set<Investment>();
    public DbSet<InterestAccrual> InterestAccruals => Set<InterestAccrual>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite can't ORDER BY or compare DateTimeOffset; store as sortable UTC ticks.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        var b = builder;

        b.Entity<AppUser>(u => u.Property(x => x.DisplayName).HasMaxLength(60));

        b.Entity<Account>(e =>
        {
            e.ToTable("Accounts");
            e.HasKey(x => x.Id);
            e.Property(x => x.OwnerId).HasMaxLength(450);
            e.HasIndex(x => new { x.OwnerId, x.Kind });
            e.Property(x => x.Version).IsConcurrencyToken(); // two debits can't both pass one balance check
            e.Ignore(x => x.IsSystem);
        });

        b.Entity<JournalEntry>(e =>
        {
            e.ToTable("JournalEntries");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.At);
            e.Property(x => x.Description).IsRequired().HasMaxLength(200);
            e.Property(x => x.InitiatedBy).HasMaxLength(450);
            e.Ignore(x => x.IsBalanced);
            e.OwnsMany(x => x.Postings, p =>
            {
                p.ToTable("Postings");
                p.WithOwner().HasForeignKey("JournalEntryId");
                p.Property<int>("Id").ValueGeneratedOnAdd();
                p.HasKey("Id");
                p.HasIndex(x => x.AccountId);
            });
            e.Navigation(x => x.Postings).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        b.Entity<InvestmentPlan>(e =>
        {
            e.ToTable("InvestmentPlans");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(60);
            e.Property(x => x.Description).IsRequired().HasMaxLength(300);
        });

        b.Entity<Investment>(e =>
        {
            e.ToTable("Investments");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.OwnerId);
            e.HasIndex(x => x.Status);
            e.Property(x => x.OwnerId).IsRequired().HasMaxLength(450);
            e.Property(x => x.PlanName).IsRequired().HasMaxLength(60);
            e.Property(x => x.AccruedThrough).IsConcurrencyToken();
            e.Property(x => x.Status).IsConcurrencyToken();
            e.Ignore(x => x.DailyInterest);
        });

        b.Entity<InterestAccrual>(e =>
        {
            e.ToTable("InterestAccruals");
            e.HasKey(x => new { x.InvestmentId, x.Day }); // one payment per investment per day, enforced by the DB
        });

        b.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("IdempotencyRecords");
            e.HasKey(x => new { x.OwnerId, x.Key });
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.RequestHash).HasMaxLength(64);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("RefreshTokens");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.FamilyId);
            e.Property(x => x.TokenHash).IsRequired().HasMaxLength(64);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

/// <summary>Creates the three system accounts and starter plans on first run.</summary>
public static class DbInitializer
{
    public static async Task SeedAsync(AccrueDbContext db, TimeProvider clock, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        foreach (var (id, kind) in new[] { (SystemAccounts.Funding, AccountKind.SystemFunding), (SystemAccounts.Payout, AccountKind.SystemPayout), (SystemAccounts.Interest, AccountKind.SystemInterest) })
        {
            if (!await db.Accounts.AnyAsync(a => a.Id == id, ct)) db.Accounts.Add(Account.System(id, kind, now));
        }

        if (!await db.InvestmentPlans.AnyAsync(ct))
        {
            db.InvestmentPlans.Add(InvestmentPlan.Create("Flex", "Withdraw any time. Interest accrues daily.", 8m, 1_000m, 0, now));
            db.InvestmentPlans.Add(InvestmentPlan.Create("Target 90", "Locked for 90 days for a better rate.", 14m, 5_000m, 90, now));
            db.InvestmentPlans.Add(InvestmentPlan.Create("Fixed 1 Year", "Locked for 365 days. Highest rate.", 18m, 10_000m, 365, now));
        }

        await db.SaveChangesAsync(ct);
    }
}
