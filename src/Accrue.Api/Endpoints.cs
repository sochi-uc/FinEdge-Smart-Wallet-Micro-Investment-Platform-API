using System.Security.Claims;
using Accrue.Application;
using Accrue.Infrastructure.Identity;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Accrue.Api;

internal static class PrincipalExtensions
{
    public static string OwnerId(this ClaimsPrincipal user) =>
        user.FindFirstValue("sub") ?? throw new InvalidOperationException("Token has no subject.");

    public static string DisplayName(this ClaimsPrincipal user) => user.FindFirstValue("name") ?? "Someone";
}

internal static class Endpoints
{
    public const string AuthRateLimit = "auth";
    public const string MoneyRateLimit = "money";
    public const string AdminPolicy = "admin";

    public sealed record RegisterRequest(string? Email, string? Password, string? DisplayName);
    public sealed record LoginRequest(string? Email, string? Password);
    public sealed record RefreshRequest(string? RefreshToken);
    public sealed record AccrualRequest(DateOnly? Through);

    public static void MapAccrue(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1");

        // ---------------------------------------------------------------- auth (anonymous, rate limited)
        var auth = v1.MapGroup("/auth").WithTags("Auth").AllowAnonymous().RequireRateLimiting(AuthRateLimit);
        auth.MapPost("/register", async Task<Results<Ok<AuthTokens>, ValidationProblem>> (RegisterRequest b, AuthService s, CancellationToken ct) =>
        {
            var (tokens, errors) = await s.RegisterAsync(b.Email, b.Password, b.DisplayName, ct);
            return tokens is null ? TypedResults.ValidationProblem(errors!) : TypedResults.Ok(tokens);
        });
        auth.MapPost("/login", async Task<Results<Ok<AuthTokens>, ProblemHttpResult>> (LoginRequest b, AuthService s, HttpContext h, CancellationToken ct) =>
            await s.LoginAsync(b.Email, b.Password, h.Connection.RemoteIpAddress?.ToString(), ct) is { } t
                ? TypedResults.Ok(t)
                : TypedResults.Problem(statusCode: 401, title: "Sign-in failed", detail: "Invalid email or password."));
        auth.MapPost("/refresh", async Task<Results<Ok<AuthTokens>, ProblemHttpResult>> (RefreshRequest b, AuthService s, CancellationToken ct) =>
            await s.RefreshAsync(b.RefreshToken, ct) is { } t
                ? TypedResults.Ok(t)
                : TypedResults.Problem(statusCode: 401, title: "Session expired", detail: "Sign in again."));
        auth.MapPost("/logout", async (RefreshRequest b, AuthService s, CancellationToken ct) =>
        {
            await s.LogoutAsync(b.RefreshToken, ct);
            return TypedResults.NoContent();
        });

        // ---------------------------------------------------------------- user (JWT required by fallback policy)
        var me = v1.MapGroup("").WithTags("Me");
        me.MapGet("/me", async Task<Results<Ok<UserDto>, NotFound>> (ClaimsPrincipal u, AuthService s) =>
            await s.MeAsync(u.OwnerId()) is { } dto ? TypedResults.Ok(dto) : TypedResults.NotFound());
        me.MapGet("/dashboard", (ClaimsPrincipal u, DashboardService s, CancellationToken ct) => s.GetAsync(u.OwnerId(), ct));

        var wallet = v1.MapGroup("/wallet").WithTags("Wallet");
        wallet.MapGet("/", (ClaimsPrincipal u, WalletService s, CancellationToken ct) => s.GetAsync(u.OwnerId(), ct));
        wallet.MapGet("/transactions", (ClaimsPrincipal u, WalletService s, DateTimeOffset? before, int? limit, CancellationToken ct) =>
            s.HistoryAsync(u.OwnerId(), before, limit ?? 25, ct));

        var money = v1.MapGroup("").WithTags("Money").AddEndpointFilter<IdempotencyFilter>().RequireRateLimiting(MoneyRateLimit);
        money.MapPost("/wallet/deposits", async (AmountRequest b, ClaimsPrincipal u, WalletService s, CancellationToken ct) =>
            ToHttp(await s.DepositAsync(u.OwnerId(), b, ct)));
        money.MapPost("/wallet/withdrawals", async (AmountRequest b, ClaimsPrincipal u, WalletService s, CancellationToken ct) =>
            ToHttp(await s.WithdrawAsync(u.OwnerId(), b, ct)));
        money.MapPost("/transfers", async (TransferRequest b, ClaimsPrincipal u, WalletService s, CancellationToken ct) =>
            ToHttp(await s.TransferAsync(u.OwnerId(), u.DisplayName(), b, ct)));
        money.MapPost("/investments", async (InvestRequest b, ClaimsPrincipal u, InvestmentService s, CancellationToken ct) =>
            ToHttp(await s.InvestAsync(u.OwnerId(), b, ct), created: true));
        money.MapPost("/investments/{id:guid}/redeem", async (Guid id, ClaimsPrincipal u, InvestmentService s, CancellationToken ct) =>
            ToHttp(await s.RedeemAsync(u.OwnerId(), id, ct)));

        var invest = v1.MapGroup("").WithTags("Investments");
        invest.MapGet("/plans", (InvestmentService s, CancellationToken ct) => s.PlansAsync(false, ct));
        invest.MapGet("/investments", (ClaimsPrincipal u, InvestmentService s, CancellationToken ct) => s.MineAsync(u.OwnerId(), ct));

        // ---------------------------------------------------------------- admin (role claim from the token)
        var admin = v1.MapGroup("/admin").WithTags("Admin").RequireAuthorization(AdminPolicy);
        admin.MapGet("/plans", (InvestmentService s, CancellationToken ct) => s.PlansAsync(true, ct));
        admin.MapPost("/plans", async (PlanRequest b, InvestmentService s, CancellationToken ct) => ToHttp(await s.CreatePlanAsync(b, ct), created: true));
        admin.MapPut("/plans/{id:guid}", async (Guid id, PlanRequest b, InvestmentService s, CancellationToken ct) => ToHttp(await s.UpdatePlanAsync(id, b, ct)));
        admin.MapPost("/accrual/run", (AccrualRequest b, AccrualService s, CancellationToken ct) => s.RunAsync(b.Through, ct));
    }

    private static IResult ToHttp<T>(Result<T> r, bool created = false) => r.Error switch
    {
        ErrorKind.None when created => TypedResults.Json(r.Value, statusCode: StatusCodes.Status201Created),
        ErrorKind.None => TypedResults.Ok(r.Value),
        ErrorKind.NotFound => TypedResults.NotFound(),
        ErrorKind.Conflict => TypedResults.Problem(statusCode: 409, title: "Conflict", detail: r.Detail),
        _ => TypedResults.ValidationProblem(r.Errors!),
    };
}
