using FinEdge_Smart_Wallet___Micro_Investment_Platform_API.Models;

public class User
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "User"; // Default role
    public Wallet? Wallet { get; set; }
}