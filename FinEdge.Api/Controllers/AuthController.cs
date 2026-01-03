
using FinEdge.Api.Data;
using FinEdge.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace FinEdge.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    public AuthController(AppDbContext db) => _db = db;

    [HttpPost("register")]
    public IActionResult Register(User u)
    {
        _db.Users.Add(u);
        _db.SaveChanges();
        return Ok();
    }

    [HttpPost("login")]
    public IActionResult Login(User u)
    {
        var user = _db.Users.FirstOrDefault(x => x.Email == u.Email && x.Password == u.Password);
        if (user == null) return Unauthorized();

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("SUPER_SECRET_FINEDGE_KEY_12345"));
        var token = new JwtSecurityToken(signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return Ok(new { token = new JwtSecurityTokenHandler().WriteToken(token) });
    }
}
