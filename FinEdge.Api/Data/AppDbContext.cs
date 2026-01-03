
using FinEdge.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace FinEdge.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> o) : base(o) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<InvestmentPlan> Plans => Set<InvestmentPlan>();
    public DbSet<Investment> Investments => Set<Investment>();
}
