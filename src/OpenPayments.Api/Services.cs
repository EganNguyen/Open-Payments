using System.Security.Cryptography;
using System.Text;
using Npgsql;
using OpenPayments.Domain;
using OpenPayments.Transitions;

namespace OpenPayments.Api;

public sealed record ApiKeyIdentity(MerchantId MerchantId, string Mode, IReadOnlyList<string> Scopes);

public sealed class ApiKeyAuthenticator(TxRunner runner)
{
    public async Task<ApiKeyIdentity?> AuthenticateAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)
            || header.Length <= "Bearer ".Length)
        {
            return null;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(header["Bearer ".Length..]));
        await using var connection = await runner.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "select merchant_id, mode, scopes from api_keys where key_hash = @hash and revoked_at is null",
            connection);
        command.Parameters.AddWithValue("hash", hash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ApiKeyIdentity(new MerchantId(reader.GetGuid(0)), reader.GetString(1), reader.GetFieldValue<string[]>(2))
            : null;
    }
}

public abstract record FraudDecision
{
    public sealed record Allow : FraudDecision;
    public sealed record Block(string Code) : FraudDecision;
}

public interface IFraudCheck
{
    Task<FraudDecision> CheckAsync(
        MerchantId merchant,
        PaymentId payment,
        Money amount,
        CancellationToken ct);
}

public sealed class AllowAllFraudCheck : IFraudCheck
{
    public Task<FraudDecision> CheckAsync(
        MerchantId merchant,
        PaymentId payment,
        Money amount,
        CancellationToken ct) => Task.FromResult<FraudDecision>(new FraudDecision.Allow());
}

public sealed class InFlightLimiter(int permitLimit)
{
    private int _current;

    public int Current => Volatile.Read(ref _current);

    public void SetPermitLimit(int value) => permitLimit = value;

    public bool TryEnter()
    {
        if (Interlocked.Increment(ref _current) <= permitLimit)
        {
            return true;
        }

        Interlocked.Decrement(ref _current);
        return false;
    }

    public void Exit() => Interlocked.Decrement(ref _current);
}

public sealed class CanonicalLog(ILogger<CanonicalLog> logger)
{
    public CanonicalEntry? Last { get; private set; }

    public void Record(CanonicalEntry entry)
    {
        Last = entry;
        logger.LogInformation(
            "canonical merchant_id={MerchantId} payment_id={PaymentId} attempt_id={AttemptId} " +
            "state_from={StateFrom} state_to={StateTo} limiter={Limiter} request_class={RequestClass} " +
            "psp={Psp} breaker={Breaker} psp_wait_ms={PspWaitMs} db_wait_ms={DbWaitMs} " +
            "pool_wait_ms={PoolWaitMs} rule_version={RuleVersion}",
            entry.MerchantId,
            entry.PaymentId,
            entry.AttemptId,
            entry.StateFrom,
            entry.StateTo,
            entry.Limiter,
            entry.RequestClass,
            entry.Psp,
            entry.Breaker,
            entry.PspWaitMs,
            entry.DbWaitMs,
            entry.PoolWaitMs,
            entry.RuleVersion);
    }
}

public sealed record CanonicalEntry(
    string MerchantId,
    string? PaymentId,
    string? AttemptId,
    string? StateFrom,
    string? StateTo,
    string Limiter,
    string RequestClass,
    string Psp,
    string Breaker,
    long PspWaitMs,
    long DbWaitMs,
    long PoolWaitMs,
    string RuleVersion);
