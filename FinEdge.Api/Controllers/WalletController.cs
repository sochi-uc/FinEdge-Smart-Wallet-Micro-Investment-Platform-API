
using FinEdge.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinEdge.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/wallet")]
public class WalletController : ControllerBase
{
    private readonly AppDbContext _db;
    public WalletController(AppDbContext db) => _db = db;

    [HttpPost("deposit/{userId}/{amount}")]
    public IActionResult Deposit(int userId, decimal amount)
    {
        var u = _db.Users.Find(userId)!;
        u.Wallet.Balance += amount;
        _db.SaveChanges();
        return Ok(u.Wallet.Balance);
    }
}
