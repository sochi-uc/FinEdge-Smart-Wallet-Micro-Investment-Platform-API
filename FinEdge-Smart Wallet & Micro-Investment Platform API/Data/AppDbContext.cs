using Microsoft.EntityFrameworkCore;
using FinEdge_Smart_Wallet___Micro_Investment_Platform_API.Models;

namespace FinEdge_Smart_Wallet___Micro_Investment_Platform_API.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<User> Users { get; set; }
        public DbSet<Wallet> Wallets { get; set; }
        public DbSet<Transaction> Transactions { get; set; }
        public DbSet<InvestmentPlan> InvestmentPlans { get; set; }
        public DbSet<UserInvestment> UserInvestments { get; set; }
    }
}