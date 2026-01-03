
using FinEdge.Api.Data;
using FinEdge.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinEdge.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/invest")]
public class InvestmentController : ControllerBase
{
    private readonly AppDbContext _db;
    public InvestmentController(AppDbContext db) => _db = db;

    [HttpPost("plan")]
    public IActionResult CreatePlan(InvestmentPlan p)
    {
        _db.Plans.Add(p);
        _db.SaveChanges();
        return Ok();
    }

    [HttpPost]
    public IActionResult Invest(Investment i)
    {
        var u = _db.Users.Find(i.UserId)!;
        if (u.Wallet.Balance < i.Amount) return BadRequest();
        u.Wallet.Balance -= i.Amount;
        _db.Investments.Add(i);
        _db.SaveChanges();
        return Ok();
    }
}
