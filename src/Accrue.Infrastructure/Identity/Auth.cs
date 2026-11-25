using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Accrue.Application;
using Accrue.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Accrue.Infrastructure.Identity;

public sealed class AppUser : IdentityUser
{
    public string DisplayName { get; set; } = string.Empty;
}

public static class Roles
{
    public const string Admin = "admin";
}

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "accrue";
    public string Audience { get; set; } = "accrue-clients";

    /// <summary>HMAC-SHA256 key, at least 32 bytes. Comes from user-secrets or env vars — never source control.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>Emails that become admins when they register (bootstrap only).</summary>
    public string[] AdminEmails { get; set; } = [];
}

/// <summary>
/// Opaque refresh token, stored only as a SHA-256 hash. Rotated on every use; tokens in one login chain
/// share a <see cref="FamilyId"/>. Presenting an already-used token means it was stolen, so the whole
/// family is revoked (OAuth 2.0 Security BCP refresh-token reuse detection).
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public Guid FamilyId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed record AuthTokens(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt, UserDto User);

public sealed record UserDto(string Id, string Email, string DisplayName, bool IsAdmin);

public sealed class AuthService(
    UserManager<AppUser> users,
    AccrueDbContext db,
    WalletService wallets,
    IOptions<JwtOptions> options,
    IPasswordHasher<AppUser> hasher,
    TimeProvider clock,
    ILogger<AuthService> log)
{
    public const int PasswordMax = 128;
    private const string InvalidCredentials = "Invalid email or password.";
    private static readonly AppUser Dummy = new() { UserName = "dummy" };
    private static string? _dummyHash;
    private readonly JwtOptions _o = options.Value;

    public async Task<(AuthTokens? Tokens, Dictionary<string, string[]>? Errors)> RegisterAsync(string? email, string? password, string? displayName, CancellationToken ct)
    {
        var e = (email ?? string.Empty).Trim();
        var n = (displayName ?? string.Empty).Trim();
        var p = password ?? string.Empty;
        var errors = new Dictionary<string, string[]>();
        if (e.Length is 0 or > 254 || !new EmailAddressAttribute().IsValid(e)) errors["email"] = ["Enter a valid email address."];
        if (n.Length is 0 or > 60) errors["displayName"] = ["Name must be 1–60 characters."];
        if (p.Length > PasswordMax) errors["password"] = [$"Password must be at most {PasswordMax} characters."];
        if (errors.Count > 0) return (null, errors);

        var user = new AppUser { UserName = e, Email = e, DisplayName = n };
        var result = await users.CreateAsync(user, p);
        if (!result.Succeeded)
        {
            // Don't reveal whether the email is taken (enumeration).
            var msgs = result.Errors.Select(x => x.Code is "DuplicateUserName" or "DuplicateEmail" ? "Unable to register with these details." : x.Description).Distinct().ToArray();
            return (null, new() { ["password"] = msgs });
        }

        if (_o.AdminEmails.Contains(e, StringComparer.OrdinalIgnoreCase)) await users.AddToRoleAsync(user, Roles.Admin);
        await wallets.GetOrOpenWalletAsync(user.Id, ct);
        log.LogInformation("User {UserId} registered", user.Id);
        return (await IssueAsync(user, Guid.NewGuid(), ct), null);
    }

    public async Task<AuthTokens?> LoginAsync(string? email, string? password, string? ip, CancellationToken ct)
    {
        var e = (email ?? string.Empty).Trim();
        var p = password ?? string.Empty;
        if (e.Length is 0 or > 254 || p.Length is 0 or > PasswordMax) return null;

        var user = await users.FindByEmailAsync(e);
        if (user is null)
        {
            hasher.VerifyHashedPassword(Dummy, _dummyHash ??= hasher.HashPassword(Dummy, Guid.NewGuid().ToString()), p); // equalise timing
            log.LogWarning("Failed login (unknown account) from {Ip}", ip);
            return null;
        }

        if (await users.IsLockedOutAsync(user))
        {
            log.LogWarning("Login attempt on locked account {UserId} from {Ip}", user.Id, ip);
            return null;
        }

        if (!await users.CheckPasswordAsync(user, p))
        {
            await users.AccessFailedAsync(user);
            log.LogWarning("Failed login for {UserId} from {Ip}", user.Id, ip);
            return null;
        }

        await users.ResetAccessFailedCountAsync(user);
        log.LogInformation("User {UserId} signed in", user.Id);
        return await IssueAsync(user, Guid.NewGuid(), ct);
    }

    public async Task<AuthTokens?> RefreshAsync(string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(refreshToken) || refreshToken.Length > 200) return null;
        var hash = Hash(refreshToken);
        var token = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        var now = clock.GetUtcNow();
        if (token is null || token.RevokedAt is not null || token.ExpiresAt <= now) return null;

        if (token.UsedAt is not null)
        {
            // Reuse of a rotated token: assume theft, kill the whole session chain.
            await RevokeFamilyAsync(token.FamilyId, ct);
            log.LogWarning("Refresh token reuse detected for {UserId}; family revoked", token.UserId);
            return null;
        }

        var user = await users.FindByIdAsync(token.UserId);
        if (user is null || await users.IsLockedOutAsync(user)) return null;
        token.UsedAt = now;
        return await IssueAsync(user, token.FamilyId, ct);
    }

    public async Task LogoutAsync(string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(refreshToken) || refreshToken.Length > 200) return;
        var hash = Hash(refreshToken);
        var token = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is not null) await RevokeFamilyAsync(token.FamilyId, ct);
    }

    public async Task<UserDto?> MeAsync(string userId)
    {
        var u = await users.FindByIdAsync(userId);
        return u is null ? null : new UserDto(u.Id, u.Email!, u.DisplayName, await users.IsInRoleAsync(u, Roles.Admin));
    }

    private async Task RevokeFamilyAsync(Guid familyId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        foreach (var t in await db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null).ToListAsync(ct)) t.RevokedAt = now;
        await db.SaveChangesAsync(ct);
    }

    private async Task<AuthTokens> IssueAsync(AppUser user, Guid familyId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var isAdmin = await users.IsInRoleAsync(user, Roles.Admin);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email!),
            new("name", user.DisplayName),
        };
        if (isAdmin) claims.Add(new Claim("role", Roles.Admin));

        var accessExpires = now.AddMinutes(_o.AccessTokenMinutes);
        var jwt = new JwtSecurityToken(
            _o.Issuer, _o.Audience, claims, now.UtcDateTime, accessExpires.UtcDateTime,
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.SigningKey)), SecurityAlgorithms.HmacSha256));

        var refresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var refreshExpires = now.AddDays(_o.RefreshTokenDays);
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            FamilyId = familyId,
            TokenHash = Hash(refresh),
            CreatedAt = now,
            ExpiresAt = refreshExpires,
        });
        await db.SaveChangesAsync(ct);

        return new AuthTokens(new JwtSecurityTokenHandler().WriteToken(jwt), accessExpires, refresh, refreshExpires,
            new UserDto(user.Id, user.Email!, user.DisplayName, isAdmin));
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
