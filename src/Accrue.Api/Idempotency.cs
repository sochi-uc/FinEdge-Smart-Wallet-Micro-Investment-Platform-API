using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Accrue.Application;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Accrue.Api;

/// <summary>
/// Makes money endpoints safe to retry (a dropped mobile connection must never charge twice).
/// Clients send <c>Idempotency-Key</c>; the first request reserves the key, later requests with the same
/// key and body get the stored response; the same key with a different body is rejected (422).
/// Keys are scoped per user, so one user can't probe or replay another's.
/// </summary>
internal sealed partial class IdempotencyFilter(IAppDbContext db, TimeProvider clock, IOptions<JsonOptions> json) : IEndpointFilter
{
    public const string Header = "Idempotency-Key";

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex KeyPattern();

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var key = http.Request.Headers[Header].ToString();
        if (!KeyPattern().IsMatch(key))
        {
            return TypedResults.Problem(statusCode: 400, title: "Idempotency-Key required",
                detail: "Send a unique Idempotency-Key header (8–64 letters, digits, '-' or '_') with every money request.");
        }

        var owner = http.User.OwnerId();
        var hash = Hash(http, ctx.Arguments);
        var existing = await db.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(r => r.OwnerId == owner && r.Key == key, http.RequestAborted);
        if (existing is not null) return Replay(http, existing, hash);

        var record = new IdempotencyRecord { OwnerId = owner, Key = key, RequestHash = hash, CreatedAt = clock.GetUtcNow() };
        db.IdempotencyRecords.Add(record);
        try
        {
            await db.SaveChangesAsync(http.RequestAborted); // reserve the key before doing any work
        }
        catch (DbUpdateException)
        {
            return TypedResults.Problem(statusCode: 409, title: "Request in progress", detail: "A request with this Idempotency-Key is already being processed.");
        }

        object? result;
        try
        {
            result = await next(ctx);
        }
        catch
        {
            await ReleaseAsync(record);
            throw;
        }

        var status = (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
        if (status >= 500 || status == 409)
        {
            await ReleaseAsync(record); // transient: let the client retry with the same key
            return result;
        }

        record.StatusCode = status;
        record.ResponseJson = result is IValueHttpResult v && v.Value is not null ? JsonSerializer.Serialize(v.Value, v.Value.GetType(), json.Value.SerializerOptions) : null;
        await db.SaveChangesAsync(CancellationToken.None);
        return result;
    }

    private static object Replay(HttpContext http, IdempotencyRecord r, string hash)
    {
        if (r.RequestHash != hash)
            return TypedResults.Problem(statusCode: 422, title: "Idempotency-Key reused", detail: "This key was already used for a different request.");
        if (r.StatusCode == 0)
            return TypedResults.Problem(statusCode: 409, title: "Request in progress", detail: "A request with this Idempotency-Key is still being processed.");
        http.Response.Headers["Idempotent-Replayed"] = "true";
        return r.ResponseJson is null ? Results.StatusCode(r.StatusCode) : Results.Content(r.ResponseJson, "application/json", Encoding.UTF8, r.StatusCode);
    }

    private async Task ReleaseAsync(IdempotencyRecord record)
    {
        db.IdempotencyRecords.Remove(record);
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException)
        {
            // best effort; the record would simply block this key
        }
    }

    /// <summary>Fingerprint of the operation: method, route and the bound request payload (not services).</summary>
    private string Hash(HttpContext http, IList<object?> args)
    {
        var payload = args.Where(a => a is not null && (a.GetType().Namespace == typeof(AmountRequest).Namespace || a is Guid)).ToList();
        var text = $"{http.Request.Method} {http.Request.Path}|{JsonSerializer.Serialize(payload, json.Value.SerializerOptions)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
