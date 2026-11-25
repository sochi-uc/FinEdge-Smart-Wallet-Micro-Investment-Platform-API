using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Accrue.Tests;

/// <summary>
/// Own fixture (own app + clock): this test moves time forward, which must not leak into other tests.
/// </summary>
public sealed class InterestTests(AppFactory f) : IClassFixture<AppFactory>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------ interest (v1 #9)

    [Fact]
    public async Task Interest_accrues_daily_exactly_once_and_redemption_pays_principal_plus_interest()
    {
        // Sign the admin in before the clock moves so its access token is still valid afterwards.
        var root = await LoginOrRegisterAdminAsync();
        var investor = await f.RegisterAsync();
        var flex = (await (await investor.Client.GetAsync("/api/v1/plans")).JsonAsync()).EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "Flex"); // 8% p.a., no lock
        await (await investor.Client.PostMoneyAsync("/api/v1/wallet/deposits", new { amount = 36_500 })).JsonAsync();
        var inv = await (await investor.Client.PostMoneyAsync("/api/v1/investments", new { planId = flex.GetProperty("id").GetGuid(), amount = 36_500 })).JsonAsync();
        Assert.Equal(8.00m, inv.GetProperty("dailyInterest").GetDecimal());

        f.Clock.Advance(TimeSpan.FromDays(3)); // start day + 2 more days are now complete
        var run1 = await (await root.PostAsJsonAsync("/api/v1/admin/accrual/run", new { })).JsonAsync();
        var run2 = await (await root.PostAsJsonAsync("/api/v1/admin/accrual/run", new { })).JsonAsync();
        Assert.True(run1.GetProperty("daysPosted").GetInt32() >= 3);
        Assert.Equal(0, run2.GetProperty("daysPosted").GetInt32()); // idempotent

        var mine = (await (await investor.Client.GetAsync("/api/v1/investments")).JsonAsync())[0];
        Assert.Equal(24.00m, mine.GetProperty("interestEarned").GetDecimal());

        await (await investor.Client.PostMoneyAsync($"/api/v1/investments/{inv.GetProperty("id").GetGuid()}/redeem", new { })).JsonAsync();
        Assert.Equal(36_524.00m, await investor.Client.BalanceAsync());
    }

    private async Task<HttpClient> LoginOrRegisterAdminAsync()
    {
        var anon = f.CreateClient();
        var login = await anon.PostAsJsonAsync("/api/v1/auth/login", new { email = AppFactory.AdminEmail, password = "correct horse battery staple" });
        string token;
        if (login.IsSuccessStatusCode)
        {
            token = (await login.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("accessToken").GetString()!;
        }
        else
        {
            token = (await f.RegisterAsync(AppFactory.AdminEmail)).AccessToken;
        }

        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }
}
