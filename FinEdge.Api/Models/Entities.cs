
namespace FinEdge.Api.Models;

public class User
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public bool IsAdmin { get; set; }
    public Wallet Wallet { get; set; } = new();
}

public class Wallet
{
    public int Id { get; set; }
    public decimal Balance { get; set; }
}

public class Transaction
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public decimal Amount { get; set; }
    public string Type { get; set; } = "";
    public DateTime Date { get; set; } = DateTime.UtcNow;
}

public class InvestmentPlan
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal RoiPercent { get; set; }
}

public class Investment
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int PlanId { get; set; }
    public decimal Amount { get; set; }
}
