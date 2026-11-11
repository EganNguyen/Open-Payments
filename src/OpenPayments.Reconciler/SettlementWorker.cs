using Microsoft.Extensions.Options;
using OpenPayments.Domain;
using OpenPayments.Psp;
using OpenPayments.Transitions;

namespace OpenPayments.Reconciler;

public sealed record SettlementSummary(
    int Files,
    int Matched,
    int Unmatched,
    int Mismatch,
    int Duplicate,
    int Ignored,
    int Missing);

/// <summary>
/// D18/D63: ingest each account's settlement files, match their lines, and record an operation
/// that has no line past the adapter's settlement lag. A file's progress is durable, so a killed
/// ingest resumes at the next tick; a repeated line changes nothing.
/// </summary>
public sealed class SettlementWorker(
    SettlementStore store,
    ISettlementSource source,
    IPspAdapterFactory adapters,
    IOptions<ReconcilerOptions> options,
    ILogger<SettlementWorker> logger) : BackgroundService
{
    private readonly ReconcilerOptions _options = options.Value;

    public long MatchedTotal { get; private set; }

    public long MissingTotal { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.Tick);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
#pragma warning disable CA1031 // A failed tick is retried; file progress is durable.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                logger.LogError(exception, "The settlement tick failed.");
            }
        }
    }

    public async Task<SettlementSummary> RunOnceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var accounts = await store.AccountsAsync(cancellationToken);
        var from = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-7);
        var files = 0;
        var matched = 0;
        var unmatched = 0;
        var mismatch = 0;
        var duplicate = 0;
        var ignored = 0;
        var missing = 0;
        foreach (var account in accounts)
        {
            var pspAccount = new PspAccount(
                account.Id,
                account.MerchantId,
                account.Psp,
                account.Mode,
                account.CredentialRef,
                account.RateLimitPerSecond,
                account.RecoveryShare);
            var capabilities = adapters.Capabilities(pspAccount);
            IReadOnlyList<SettlementFile> due;
            try
            {
                due = await source.ListFilesAsync(pspAccount, from, cancellationToken);
            }
#pragma warning disable CA1031 // One account's source failure leaves the rest alone.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                logger.LogWarning(exception, "Listing settlement files failed for {Account}.", account.Id);
                continue;
            }

            foreach (var file in due)
            {
                try
                {
                    if (!await store.StartFileAsync(account, file, cancellationToken))
                    {
                        continue;
                    }

                    var lines = 0;
                    await foreach (var line in source.ReadAsync(pspAccount, file.FileId, cancellationToken))
                    {
                        await store.IngestLineAsync(account, file.FileId, line, cancellationToken);
                        lines = Math.Max(lines, line.LineNo);
                    }

                    await store.CompleteFileAsync(account, file.FileId, lines, cancellationToken);
                    var result = await store.MatchPendingAsync(account, file.FileId, cancellationToken);
                    await store.FinishFileAsync(account, file.FileId, cancellationToken);
                    files++;
                    matched += result.Matched;
                    unmatched += result.Unmatched;
                    mismatch += result.Mismatch;
                    duplicate += result.Duplicate;
                    ignored += result.Ignored;
                }
#pragma warning disable CA1031 // The file stays receiving and resumes next tick.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    logger.LogWarning(
                        exception,
                        "Ingesting settlement file {File} for {Account} failed; it will resume.",
                        file.FileId,
                        account.Id);
                }
            }

            var absent = await store.FindMissingAsync(
                account, now, capabilities.SettlementLag, cancellationToken);
            missing += absent.Attempts + absent.Refunds;
        }

        MatchedTotal += matched;
        MissingTotal += missing;
        if (files > 0 || missing > 0)
        {
            logger.LogInformation(
                "Settlement: {Files} files, {Matched} matched, {Unmatched} unmatched, {Mismatch} mismatched, {Missing} missing.",
                files,
                matched,
                unmatched,
                mismatch,
                missing);
        }

        return new SettlementSummary(files, matched, unmatched, mismatch, duplicate, ignored, missing);
    }
}
