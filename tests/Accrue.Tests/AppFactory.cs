using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Accrue.Tests;

/// <summary>Controllable clock so daily interest can be tested across simulated days.</summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

public sealed class AppFactory : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin@accrue.test";
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"accrue-test-{Guid.NewGuid():N}.db");

    public TestClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Accrue", $"Data Source={_db};Pooling=False");
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-that-is-at-least-32-bytes-long!!");
        builder.UseSetting("Jwt:AdminEmails:0", AdminEmail);
        builder.UseSetting("Accrual:Enabled", "false");
        builder.UseSetting("RateLimiting:AuthPermitPerMinute", "1000");
        builder.UseSetting("RateLimiting:MoneyPermitPerMinute", "1000");
        builder.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(Clock));
    }

    public record Session(HttpClient Client, string AccessToken, string RefreshToken, string UserId, string Email);

    public async Task<Session> RegisterAsync(string? email = null, string name = "Test User")
    {
        email ??= $"u-{Guid.NewGuid():N}@accrue.test";
        var anon = CreateClient();
        var res = await anon.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "correct horse battery staple", displayName = name });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var access = body.GetProperty("accessToken").GetString()!;
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return new Session(client, access, body.GetProperty("refreshToken").GetString()!, body.GetProperty("user").GetProperty("id").GetString()!, email);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { File.Delete(_db); } catch (IOException) { }
    }
}

internal static class HttpExtensions
{
    /// <summary>POST with a fresh (or given) Idempotency-Key, as a well-behaved client would.</summary>
    public static Task<HttpResponseMessage> PostMoneyAsync(this HttpClient c, string url, object body, string? key = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return c.SendAsync(req);
    }

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<decimal> BalanceAsync(this HttpClient c) =>
        (await (await c.GetAsync("/api/v1/wallet")).JsonAsync()).GetProperty("balance").GetDecimal();
}
