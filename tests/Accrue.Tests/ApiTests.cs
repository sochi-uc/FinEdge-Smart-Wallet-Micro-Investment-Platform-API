using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Accrue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Accrue.Tests;

/// <summary>Each test maps to a v1 finding (docs/REVIEW.md) or an OWASP API Top 10 item.</summary>
public sealed class ApiTests(AppFactory f) : IClassFixture<AppFactory>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------ authentication (v1 #2–#5)

    [Fact]
    public async Task Anonymous_requests_are_rejected() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/v1/wallet")).StatusCode);

    [Fact]
    public async Task Tampered_or_foreign_tokens_are_rejected()
    {
        var s = await f.RegisterAsync();
        var parts = s.AccessToken.Split('.');
        var forged = $"{parts[0]}.{parts[1]}.{new string('A', parts[2].Length)}";
        var none = $"{Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}""")).TrimEnd('=')}.{parts[1]}.";

        foreach (var token in new[] { forged, none, "garbage" })
        {
            var c = f.CreateClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/wallet")).StatusCode);
        }
    }

    [Fact]
    public async Task Register_rejects_mass_assignment_of_admin_or_balance()
    {
        var res = await f.CreateClient().PostAsync("/api/v1/auth/register", new StringContent(
            """{"email":"x@accrue.test","password":"correct horse battery staple","displayName":"X","isAdmin":true}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Passwords_are_hashed_not_stored_in_plain_text()
    {
        var s = await f.RegisterAsync();
        await using var scope = f.Services.CreateAsyncScope();
        var user = await scope.ServiceProvider.GetRequiredService<AccrueDbContext>().Users.SingleAsync(u => u.Id == s.UserId);
        Assert.DoesNotContain("correct horse", user.PasswordHash!, StringComparison.Ordinal);
        Assert.StartsWith("AQAAAA", user.PasswordHash!, StringComparison.Ordinal); // Identity v3 PBKDF2 format
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_reuse_revokes_the_whole_session()
    {
        var s = await f.RegisterAsync();
        var anon = f.CreateClient();
        var first = await (await anon.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = s.RefreshToken })).JsonAsync();
        var rotated = first.GetProperty("refreshToken").GetString()!;
        Assert.NotEqual(s.RefreshToken, rotated);

        // Replaying the old token = theft signal → 401 and the rotated one is dead too.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = s.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = rotated })).StatusCode);
    }

    [Fact]
    public async Task Login_errors_do_not_reveal_whether_an_account_exists()
    {
        var s = await f.RegisterAsync();
        var anon = f.CreateClient();
        var unknown = await anon.PostAsJsonAsync("/api/v1/auth/login", new { email = "nobody@accrue.test", password = "whatever password" });
        var wrong = await anon.PostAsJsonAsync("/api/v1/auth/login", new { email = s.Email, password = "wrong password!!" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(
            (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString(),
            (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
    }

    // ------------------------------------------------------------------ money integrity (v1 #6, #7, #12, #13)

    [Fact]
    public async Task Money_requests_require_an_idempotency_key()
    {
        var s = await f.RegisterAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync("/api/v1/wallet/deposits", new { amount = 100 })).StatusCode);
    }

    [Fact]
    public async Task Retried_deposit_with_same_key_is_applied_once()
    {
        var s = await f.RegisterAsync();
        var key = Guid.NewGuid().ToString("N");
        var a = await s.Client.PostMoneyAsync("/api/v1/wallet/deposits", new { amount = 5000 }, key);
        var b = await s.Client.PostMoneyAsync("/api/v1/wallet/deposits", new { amount = 5000 }, key);

        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.Equal(HttpStatusCode.OK, b.StatusCode);
        Assert.Equal("true", b.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(5000m, await s.Client.BalanceAsync());
    }

    [Fact]
    public async Task Same_key_with_a_different_body_is_rejected()
    {
        var s = await f.RegisterAsync();
        var key = Guid.NewGuid().ToString("N");
        await (await s.Client.PostMoneyAsync("/api/v1/wallet/deposits", new { amount = 100 }, key)).JsonAsync();
        Assert.Equal((HttpStatusCode)422, (await s.Client.PostMoneyAsync("/api/v1/wallet/deposits", new { amount = 999 }, key)).StatusCode);
    }

    [Theory]
    [InlineData(-500)]
    [InlineData(0)]
    [InlineData(0.001)]
    public async Task Negative_zero_or_sub_kobo_amounts_are_rejected(decimal amount)
    {
        var s = await f.RegisterAsync();
        var res = await s.Client.PostMoneyAsync("/api/v1/wallet/deposits", new { amount });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(0m, await s.Client.BalanceAsync());
    }

    [Fact]
    public async Task Withdrawals_cannot_overdraw()
    {
        var s = await f.RegisterAsync();
        await (await s.Client.PostMoneyAsync("/api/v1/wallet/deposits", new { amount = 100 })).JsonAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostMoneyAsync("/api/v1/wallet/withdrawals", new { amount = 100.01m })).StatusCode);
        Assert.Equal(100m, await s.Client.BalanceAsync());
    }

    [Fact]
    public async Task P2P_transfer_moves_money_between_users_and_both_histories_show_it()
    {
        var alice = await f.RegisterAsync(name: "Alice");
        var bob = await f.RegisterAsync(name: "Bob");
        await (await alice.Client.PostMoneyAsync("/api/v1/wallet/deposits", new { amount = 1000 })).JsonAsync();
        await (await alice.Client.PostMoneyAsync("/api/v1/transfers", new { toEmail = bob.Email, amount = 250.50m, note = "lunch" })).JsonAsync();

        Assert.Equal(749.50m, await alice.Client.BalanceAsync());
        Assert.Equal(250.50m, await bob.Client.BalanceAsync());
        var bobTx = await (await bob.Client.GetAsync("/api/v1/wallet/transactions")).JsonAsync();
        Assert.Equal(250.50m, bobTx.GetProperty("items")[0].GetProperty("balanceImpact").GetDecimal());
        Assert.Contains("Alice → Bob", bobTx.GetProperty("items")[0].GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cannot_transfer_to_yourself()
    {
        var s = await f.RegisterAsync();
        await (await s.Client.PostMoneyAsync("/api/v1/wallet/deposits", new { amount = 100 })).JsonAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostMoneyAsync("/api/v1/transfers", new { toEmail = s.Email, amount = 10 })).StatusCode);
    }

    [Fact]
    public async Task Users_cannot_redeem_someone_elses_investment()
    {
        var alice = await f.RegisterAsync();
        var mallory = await f.RegisterAsync();
        var plan = (await (await alice.Client.GetAsync("/api/v1/plans")).JsonAsync())[0].GetProperty("id").GetGuid();
        await (await alice.Client.PostMoneyAsync("/api/v1/wallet/deposits", new { amount = 5000 })).JsonAsync();
        var inv = await (await alice.Client.PostMoneyAsync("/api/v1/investments", new { planId = plan, amount = 2000 })).JsonAsync();

        var res = await mallory.Client.PostMoneyAsync($"/api/v1/investments/{inv.GetProperty("id").GetGuid()}/redeem", new { });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ------------------------------------------------------------------ authorization (v1 #8)

    [Fact]
    public async Task Only_admins_can_manage_plans()
    {
        var user = await f.RegisterAsync();
        var plan = new { name = "Moon", annualRatePct = 20, lockDays = 30 };
        Assert.Equal(HttpStatusCode.Forbidden, (await user.Client.PostAsJsonAsync("/api/v1/admin/plans", plan)).StatusCode);

        var admin = await f.RegisterAsync(AppFactory.AdminEmail);
        Assert.Equal(HttpStatusCode.Created, (await admin.Client.PostAsJsonAsync("/api/v1/admin/plans", plan)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.PostAsJsonAsync("/api/v1/admin/plans", new { name = "Ponzi", annualRatePct = 10000 })).StatusCode);
    }

    [Fact]
    public async Task The_ledger_always_balances_globally()
    {
        var a = await f.RegisterAsync();
        var b = await f.RegisterAsync();
        await (await a.Client.PostMoneyAsync("/api/v1/wallet/deposits", new { amount = 900 })).JsonAsync();
        await (await a.Client.PostMoneyAsync("/api/v1/transfers", new { toEmail = b.Email, amount = 400 })).JsonAsync();
        await (await b.Client.PostMoneyAsync("/api/v1/wallet/withdrawals", new { amount = 150 })).JsonAsync();

        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccrueDbContext>();
        var entries = await db.JournalEntries.AsNoTracking().ToListAsync();
        Assert.All(entries, e => Assert.True(e.IsBalanced));
        var accounts = await db.Accounts.AsNoTracking().ToListAsync();
        Assert.Equal(0m, accounts.Sum(x => x.Balance)); // money is only ever moved, never created
    }

    // ------------------------------------------------------------------ hardening

    [Fact]
    public async Task Security_headers_are_present()
    {
        var res = await f.CreateClient().GetAsync("/healthz");
        Assert.Equal("default-src 'none'; frame-ancestors 'none'", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.True(res.Headers.CacheControl?.NoStore);
        Assert.False(res.Headers.Contains("Server"));
    }

    [Fact]
    public async Task Malformed_json_is_a_400_not_a_500()
    {
        var s = await f.RegisterAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/wallet/deposits") { Content = new StringContent("{oops", Encoding.UTF8, "application/json") };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.SendAsync(req)).StatusCode);
    }
}
