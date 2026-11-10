using System.Text;
using OpenPayments.Psp;
using OpenPayments.Transitions;

namespace OpenPayments.Api.Endpoints;

/// <summary>Reads a callback's signing secret by its <c>psp_accounts.callback_secret_ref</c>.</summary>
public interface ICallbackSecretProvider
{
    string? Get(string? reference);
}

/// <summary>Local/secret-store provider: one env var per reference, or one shared fallback.</summary>
public sealed class EnvironmentCallbackSecretProvider : ICallbackSecretProvider
{
    public string? Get(string? reference)
    {
        var specific = reference is null
            ? null
            : Environment.GetEnvironmentVariable(
                "PSP_CALLBACK_SECRET_" + reference.ToUpperInvariant().Replace('-', '_').Replace('.', '_'));
        return specific ?? Environment.GetEnvironmentVariable("PSP_CALLBACK_SECRET");
    }
}

/// <summary>
/// D30/D59: the PSP-facing callback route. It verifies the raw body before any write, applies
/// the event and its parked state in one transaction, and answers 2xx only after the commit.
/// </summary>
public static class PspEventsEndpoint
{
    private const int MaxBodyBytes = 64 * 1024;
    private static readonly SemaphoreSlim Concurrency = new(100);

    public static async Task<IResult> HandleAsync(
        HttpContext context,
        PaymentStore store,
        IPspEventParser parser,
        ICallbackSecretProvider secrets,
        Guid pspAccountId,
        CancellationToken cancellationToken)
    {
        if (!await Concurrency.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            context.Response.Headers.RetryAfter = "1";
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            var account = await store.GetCallbackAccountAsync(pspAccountId, cancellationToken);
            if (account is null || account.Disabled)
            {
                return Results.NotFound();
            }

            var secret = secrets.Get(account.SecretRef);
            if (secret is null)
            {
                return Results.NotFound();
            }

            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, cancellationToken);
            if (buffer.Length > MaxBodyBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var raw = buffer.ToArray();
            var headers = context.Request.Headers
                .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            var parsed = parser.Parse(raw, headers, secret, DateTimeOffset.UtcNow);
            if (parsed.IsFailure)
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            await store.Events.ReceiveAsync(
                pspAccountId, parsed.Value, Encoding.UTF8.GetString(raw), cancellationToken);
            return Results.Ok();
        }
        finally
        {
            Concurrency.Release();
        }
    }
}
