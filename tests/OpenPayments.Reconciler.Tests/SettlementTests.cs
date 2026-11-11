using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenPayments.Domain;
using OpenPayments.Psp;
using OpenPayments.Reconciler;
using OpenPayments.Transitions;
using Xunit;

namespace OpenPayments.Reconciler.Tests;

[Collection("reconciler-postgres")]
public sealed class SettlementTests(ReconcilerFixture db)
{
    private const string FileId = "2026-11-10";

    [Fact]
    public async Task C40_duplicate_unmatched_wrong_amount_and_missing_lines_each_end_in_defined_state()
    {
        await db.ResetAsync();
        var (merchant, account, payment, attempt) = await CapturedAsync();
        await SeedSecondCaptureAsync(merchant, account);
        var source = new FakeSettlementSource();
        source.Add(FileId, Lines(
            (1, "sale", "psp_cap", 1000),
            (2, "sale", "psp_cap", 1000),
            (3, "sale", "psp_absent", 1000),
            (4, "sale", "psp_cap", 999)));
        var summary = await RunAsync(source);

        Assert.Equal(1, summary.Matched);
        Assert.Equal(1, summary.Duplicate);
        Assert.Equal(1, summary.Unmatched);
        Assert.Equal(1, summary.Mismatch);
        Assert.Equal(1, summary.Missing);
        Assert.Equal(
            ["MATCHED", "DUPLICATE", "UNMATCHED", "MISMATCH"],
            await LineStatesAsync(account));
        Assert.Equal(1, await db.CountAsync(
            $"select count(*) from ledger_transactions where payment_id = '{payment}' and kind = 'settlement'"));
        Assert.Equal(1, await db.CountAsync(
            $"select count(*) from settlement_missing where psp_account_id = '{account}'"));
    }

    [Fact]
    public async Task C40_ingesting_same_file_twice_posts_nothing_new()
    {
        await db.ResetAsync();
        var (_, account, payment, _) = await CapturedAsync();
        var source = new FakeSettlementSource();
        source.Add(FileId, Lines((1, "sale", "psp_cap", 1000)));
        await RunAsync(source);
        var second = await RunAsync(source);

        Assert.Equal(0, second.Matched);
        Assert.Equal(1, await db.CountAsync(
            $"select count(*) from ledger_transactions where payment_id = '{payment}' and kind = 'settlement'"));
        Assert.Equal("done", await db.TextAsync(
            $"select state from settlement_files where psp_account_id = '{account}' and file_id = '{FileId}'"));
    }

    [Fact]
    public async Task C40_killed_ingest_resumes_and_posts_each_line_once()
    {
        await db.ResetAsync();
        var (merchant, account, _, _) = await CapturedAsync();
        await SeedSecondCaptureAsync(merchant, account, reference: "psp_cap2");
        var source = new FakeSettlementSource { FailAfterLine = 1 };
        source.Add(FileId, Lines(
            (1, "sale", "psp_cap", 1000),
            (2, "sale", "psp_cap2", 1000)));
        var first = await RunAsync(source);
        Assert.Equal(0, first.Files);

        source.FailAfterLine = null;
        var resumed = await RunAsync(source);

        Assert.Equal(1, resumed.Files);
        Assert.Equal(2, resumed.Matched);
        Assert.Equal(2, await db.CountAsync(
            $"select count(*) from ledger_transactions where kind = 'settlement' and merchant_id = '{merchant}'"));
    }

    [Fact]
    public async Task E22_unmatched_or_mismatched_line_posts_nothing()
    {
        await db.ResetAsync();
        var (_, account, _, _) = await CapturedAsync();
        var source = new FakeSettlementSource();
        source.Add(FileId, Lines(
            (1, "sale", "psp_absent", 1000),
            (2, "sale", "psp_cap", 999)));
        await RunAsync(source);

        Assert.Equal(0, await db.CountAsync("select count(*) from ledger_transactions"));
        Assert.Equal(["UNMATCHED", "MISMATCH"], await LineStatesAsync(account));
    }

    [Fact]
    public async Task Settlement_never_changes_payment_state()
    {
        await db.ResetAsync();
        var (_, _, payment, _) = await CapturedAsync();
        var source = new FakeSettlementSource();
        source.Add(FileId, Lines((1, "sale", "psp_cap", 1000)));
        await RunAsync(source);

        Assert.Equal("CAPTURED", await db.TextAsync($"select state from payments where id = '{payment}'"));
        Assert.Equal(1, await db.CountAsync(
            $"select count(*) from ledger_transactions where payment_id = '{payment}' and kind = 'settlement'"));
    }

    [Fact]
    public async Task Settlement_of_a_refund_clears_the_payable()
    {
        await db.ResetAsync();
        var (merchant, account, _, _) = await CapturedAsync();
        var refund = Guid.NewGuid();
        var payment = Guid.Parse((await db.TextAsync(
            $"select id::text from payments where merchant_id = '{merchant}' limit 1"))!);
        await db.ExecAdminAsync($"""
            update payments set state = 'REFUNDED', refunded_minor = 1000 where id = '{payment}';
            insert into refunds
              (id, payment_id, merchant_id, amount_minor, state, psp, psp_account_id, psp_reference)
            values ('{refund}', '{payment}', '{merchant}', 1000, 'SUCCEEDED', 'mock', '{account}', 'psp_refund');
            """);
        var source = new FakeSettlementSource();
        source.Add(FileId, Lines((1, "refund", "psp_refund", 1000)));
        await RunAsync(source);

        Assert.Equal("MATCHED", await db.TextAsync(
            $"select state from settlement_lines where refund_id = '{refund}'"));
        Assert.Equal(1, await db.CountAsync(
            $"select count(distinct t.id) from ledger_entries e join ledger_transactions t on t.id = e.txn_id where t.payment_id = '{payment}' and t.kind = 'settlement'"));
    }

    [Fact]
    public async Task Fee_lines_are_stored_and_post_nothing()
    {
        await db.ResetAsync();
        var (_, account, _, _) = await CapturedAsync();
        var source = new FakeSettlementSource();
        source.Add(FileId, Lines(
            (1, "fee", null, 30),
            (2, "adjustment", null, -10)));
        var summary = await RunAsync(source);

        Assert.Equal(2, summary.Ignored);
        Assert.Equal(0, await db.CountAsync("select count(*) from ledger_transactions"));
        Assert.Equal(["IGNORED", "IGNORED"], await LineStatesAsync(account));
    }

    private async Task<(Guid Merchant, Guid Account, Guid Payment, Guid Attempt)> CapturedAsync()
    {
        var seed = await db.SeedPendingAsync();
        await db.ExecAdminAsync($"""
            update payment_attempts
               set operation = 'sale', state = 'SUCCEEDED', psp_reference = 'psp_cap',
                   created_at = now() - interval '2 hours'
             where attempt_id = '{seed.Attempt}';
            update payments set state = 'CAPTURED', captured_minor = 1000 where id = '{seed.Payment}';
            insert into accounts (id, merchant_id, account_type, class, class_of_money, currency)
            values (gen_random_uuid(), '{seed.Merchant}', 'settlement_bank', 'terminal', 'real', 'USD');
            """);
        return seed;
    }

    private async Task SeedSecondCaptureAsync(
        Guid merchant,
        Guid account,
        string reference = "psp_missing")
    {
        var payment = Guid.NewGuid();
        var attempt = Guid.NewGuid();
        await db.ExecAdminAsync($"""
            insert into payments (id, merchant_id, state, amount_minor, currency, captured_minor, expires_at)
            values ('{payment}', '{merchant}', 'CAPTURED', 1000, 'USD', 1000, now() + interval '1 day');
            insert into payment_attempts
              (attempt_id, payment_id, merchant_id, operation, psp, psp_account_id, state,
               method_ref, method_type, psp_reference, created_at)
            values ('{attempt}', '{payment}', '{merchant}', 'sale', 'mock', '{account}', 'SUCCEEDED',
                    'tok', 'card', '{reference}', now() - interval '2 hours');
            """);
    }

    private async Task<SettlementSummary> RunAsync(FakeSettlementSource source)
    {
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new SettlementStore(runner, new PaymentStore(runner));
        using var http = new HttpClient();
        var psp = new MockPspAdapterFactory(http);
        using var worker = new SettlementWorker(
            store, source, psp, Options.Create(new ReconcilerOptions()),
            NullLogger<SettlementWorker>.Instance);
        return await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);
    }

    private async Task<string[]> LineStatesAsync(Guid account)
    {
        await using var connection = new Npgsql.NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            $"select state from settlement_lines where psp_account_id = '{account}' order by line_no",
            connection);
        var states = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            states.Add(reader.GetString(0));
        }

        return [.. states];
    }

    private static List<SettlementLine> Lines(params (int No, string Kind, string? Reference, long Amount)[] lines) =>
        [.. lines.Select(line => new SettlementLine(
            line.No,
            line.Kind,
            line.Reference,
            new Money(line.Amount, CurrencyCode.Usd),
            "approved",
            null))];

    private sealed class FakeSettlementSource : ISettlementSource
    {
        private readonly Dictionary<string, List<SettlementLine>> _lines = [];

        public int? FailAfterLine { get; set; }

        public void Add(string fileId, List<SettlementLine> lines) => _lines[fileId] = lines;

        public Task<IReadOnlyList<SettlementFile>> ListFilesAsync(
            PspAccount account,
            DateOnly from,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SettlementFile>>(
                [.. _lines.Keys.Select(key => new SettlementFile(key, DateOnly.Parse(key)))]);

        public async IAsyncEnumerable<SettlementLine> ReadAsync(
            PspAccount account,
            string fileId,
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var line in _lines[fileId])
            {
                if (FailAfterLine is { } limit && line.LineNo > limit)
                {
                    throw new IOException("simulated ingest crash");
                }

                await Task.Yield();
                yield return line;
            }
        }
    }
}
