using FinEdge_Smart_Wallet___Micro_Investment_Platform_API.Data;
using FinEdge_Smart_Wallet___Micro_Investment_Platform_API.Models;
using Microsoft.EntityFrameworkCore;

namespace FinEdge_Smart_Wallet___Micro_Investment_Platform_API.Services
{
    public class AuthService
    {
        private readonly AppDbContext _context;
        private readonly Helpers.JwtHelper _jwtHelper;

        public AuthService(AppDbContext context, Helpers.JwtHelper jwtHelper)
        {
            _context = context;
            _jwtHelper = jwtHelper;
        }

        public async Task<string> RegisterUser(RegisterRequest request)
        {
            if (await _context.Users.AnyAsync(u => u.Email == request.Email))
                return "User already exists";

            var hash = BCrypt.Net.BCrypt.HashPassword(request.Password);
            var user = new User
            {
                Email = request.Email,
                FullName = request.FullName,
                PasswordHash = hash
            };
            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            // create wallet
            var wallet = new Wallet { UserId = user.Id, Balance = 0 };
            _context.Wallets.Add(wallet);
            await _context.SaveChangesAsync();

            return "User registered successfully";
        }

        public async Task<string?> LoginUser(LoginRequest request)
        {
            var user = await _context.Users
                .Include(u => u.Wallet)
                .FirstOrDefaultAsync(u => u.Email == request.Email);

            if (user == null) return null;

            if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
                return null;

            return _jwtHelper.GenerateToken(user);
        }
    }
}