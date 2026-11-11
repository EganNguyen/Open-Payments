using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Npgsql;
using OpenPayments.Checks;
using Xunit;

namespace OpenPayments.Checks.Tests;

public sealed class CorruptionTests : IClassFixture<DbFixture>
{
    private static readonly Guid Merchant = Guid.Parse("00000000-0000-7000-8000-000000000001");
    private static readonly Guid PspAccount = Guid.Parse("00000000-0000-7000-8000-000000000002");
    private static readonly Guid Payment = Guid.Parse("00000000-0000-7000-8000-000000000011");
    private static readonly Guid Attempt = Guid.Parse("00000000-0000-7000-8000-000000000021");
    private static readonly Guid StaleAttempt = Guid.Parse("00000000-0000-7000-8000-000000000022");
    private static readonly Guid Receivable = Guid.Parse("00000000-0000-7000-8000-000000000031");
    private static readonly Guid Payable = Guid.Parse("00000000-0000-7000-8000-000000000032");
    private static readonly Guid Outbox = Guid.Parse("00000000-0000-7000-8000-000000000041");
    private static readonly Guid LedgerTxn = Guid.Parse("00000000-0000-7000-8000-000000000051");
    private static readonly Guid AppendedTxn = Guid.Parse("00000000-0000-7000-8000-000000000052");

    private readonly DbFixture _fixture;

    public CorruptionTests(DbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Clean_fixture_passes_all_applicable()
    {
        await _fixture.ResetAsync();
        await SeedCleanFixtureAsync();

        var results = await RunChecksAsync("phase1", BaselineEffect());

        Assert.All(
            results.Where(result => result.Applicable),
            result => Assert.True(
                result.Passed,
                $"{result.Check} failed: {result.Reason}; {string.Join(", ", result.SampleIds)}"));
    }

    [Fact]
    public async Task Each_check_records_its_duration()
    {
        await _fixture.ResetAsync();
        await SeedCleanFixtureAsync();

        var results = await RunChecksAsync("phase1", BaselineEffect());

        Assert.All(results, result => Assert.True(result.DurationMs >= 0, result.Check));
        Assert.True(results.Sum(result => result.DurationMs) > 0, "No check recorded any time.");
    }

    [Fact]
    public async Task CK1_detects_unbalanced_txn()
    {
        await _fixture.ResetAsync();
        await SeedCleanFixtureAsync();
        await ExecuteAsync(
            $"update ledger_entries set amount_minor = 90 where txn_id = '{LedgerTxn}' and entry_no = 1;");

        var check = (await RunChecksAsync("phase1", BaselineEffect()))
            .Single(result => result.Check == "CK1");

        Assert.False(check.Passed);
        Assert.True(check.Violations > 0);
        Assert.True(check.MoneyImpactMinor > 0);
    }

    [Fact]
    public async Task CK1_detects_updated_or_deleted_ledger_row()
    {
        await _fixture.ResetAsync();
        await SeedCleanFixtureAsync();
        var first = await RunChecksAsync("phase1", BaselineEffect());
        Assert.True(first.Single(result => result.Check == "CK1").Passed);

        await ExecuteAsync(
            $"update ledger_entries set account_id = '{Payable}' where txn_id = '{LedgerTxn}' and entry_no = 1;");

        var check = (await RunChecksAsync("phase1", BaselineEffect()))
            .Single(result => result.Check == "CK1");
        Assert.False(check.Passed);
        Assert.Contains(check.SampleIds, sample => sample.Contains("manifest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CK1_allows_new_ledger_rows()
    {
        await _fixture.ResetAsync();
        await SeedCleanFixtureAsync();
        Assert.True((await RunChecksAsync("phase1", BaselineEffect()))
            .Single(result => result.Check == "CK1").Passed);

        await ExecuteAsync($"""
            insert into ledger_transactions
              (id, merchant_id, payment_id, kind, source_event_id, effective_at)
            values
              ('{AppendedTxn}', '{Merchant}', '{Payment}', 'capture', '{AppendedTxn}', now());

            insert into ledger_entries
              (txn_id, entry_no, merchant_id, account_id, subject_id, direction, amount_minor, currency)
            values
              ('{AppendedTxn}', 1, '{Merchant}', '{Receivable}', '{Payment}', 'D', 50, 'USD'),
              ('{AppendedTxn}', 2, '{Merchant}', '{Payable}', '{Payment}', 'C', 50, 'USD');
            """);

        var check = (await RunChecksAsync("phase1", BaselineEffect()))
            .Single(result => result.Check == "CK1");
        Assert.True(check.Passed);
    }

    [Fact]
    public async Task CK2_detects_two_effects_for_one_key()
    {
        await _fixture.ResetAsync();
        await SeedCleanFixtureAsync();

        var check = (await RunChecksAsync("phase1", BaselineEffect(), BaselineEffect()))
            .Single(result => result.Check == "CK2");

        Assert.False(check.Passed);
        Assert.True(check.Violations > 0);
    }

    [Fact]
    public async Task CK2_detects_succeeded_attempt_without_effect()
    {
        await _fixture.ResetAsync();
        await SeedCleanFixtureAsync();

        var check = (await RunChecksAsync("phase1"))
            .Single(result => result.Check == "CK2");

        Assert.False(check.Passed);
        Assert.Contains(Attempt.ToString(), check.SampleIds);
    }

    [Fact]
    public async Task CK2_detects_effect_without_attempt()
    {
        await _fixture.ResetAsync();
        await SeedCleanFixtureAsync();
        var orphan = Guid.NewGuid().ToString();

        var check = (await RunChecksAsync(
                "phase1",
                BaselineEffect(),
                $"{orphan},sale,100,USD,{DateTimeOffset.UtcNow:O}"))
            .Single(result => result.Check == "CK2");

        Assert.False(check.Passed);
        Assert.Contains(orphan, check.SampleIds);
    }

    [Fact]
    public async Task CK3_detects_pending_not_matching_unresolved_refunds()
    {
        await _fixture.ResetAsync();
        await SeedCleanFixtureAsync();
        await ExecuteAsync($"update payments set refund_pending_minor = 10 where id = '{Payment}';");

        var check = (await RunChecksAsync("phase1", BaselineEffect()))
            .Single(result => result.Check == "CK3");

        Assert.False(check.Passed);
        Assert.Contains(Payment.ToString(), check.SampleIds);
    }

    [Fact]
    public async Task CK4a_detects_transition_without_outbox_row()
    {
        await _fixture.ResetAsync();
        await SeedCleanFixtureAsync();
        await ExecuteAsync($"update payments set event_seq = 2 where id = '{Payment}';");

        var check = (await RunChecksAsync("phase1", BaselineEffect()))
            .Single(result => result.Check == "CK4");

        Assert.False(check.Passed);
        Assert.Contains(Payment.ToString(), check.SampleIds);
    }

    [Fact]
    public async Task CK4b_detects_outbox_sequence_beyond_event_seq()
    {
        await _fixture.ResetAsync();
        await SeedCleanFixtureAsync();
        await ExecuteAsync($"""
            insert into outbox
              (id, merchant_id, aggregate_id, event_type, sequence, schema_version, payload, created_at)
            values
              (gen_random_uuid(), '{Merchant}', '{Payment}', 'payment.refunded', 2, 1, 'null'::jsonb, now());
            """);

        var check = (await RunChecksAsync("phase1", BaselineEffect()))
            .Single(result => result.Check == "CK4");

        Assert.False(check.Passed);
        Assert.Contains(Payment.ToString(), check.SampleIds);
    }

    [Fact]
    public async Task CK4_query_has_no_correlated_subplan()
    {
        await _fixture.ResetAsync();
        await SeedCleanFixtureAsync();
        string sql;
        await using (var stream = typeof(CheckRunner).Assembly
            .GetManifestResourceStream("OpenPayments.Checks.sql.ck4.sql")!)
        using (var reader = new StreamReader(stream))
        {
            sql = await reader.ReadToEndAsync();
        }

        await using var connection = await _fixture.OpenAsync();
        await using var command = new NpgsqlCommand("explain (format json) " + sql.TrimEnd().TrimEnd(';'), connection);
        var json = (string)(await command.ExecuteScalarAsync())!;
        using var document = JsonDocument.Parse(json);

        // A SubPlan runs once per outer row. Over every payment, that is a scan of outbox each.
        Assert.Empty(SubPlans(document.RootElement[0].GetProperty("Plan")));
    }

    [Fact]
    public async Task CK5_detects_stale_unresolved_operation()
    {
        await _fixture.ResetAsync();
        await SeedCleanFixtureAsync();
        await ExecuteAsync($"""
            insert into payment_attempts
              (attempt_id, payment_id, merchant_id, operation, psp, psp_account_id, state, created_at)
            values
              ('{StaleAttempt}', '{Payment}', '{Merchant}', 'capture', 'mock', '{PspAccount}', 'PENDING', now() - interval '2 minutes');
            """);

        var check = (await RunChecksAsync("phase1", BaselineEffect()))
            .Single(result => result.Check == "CK5");

        Assert.False(check.Passed);
        Assert.Contains($"attempt:{StaleAttempt}", check.SampleIds);
    }

    [Fact]
    public async Task CK9_detects_captured_payment_without_capture_txn()
    {
        await _fixture.ResetAsync();
        await SeedCleanFixtureAsync();
        await ExecuteAsync($"""
            delete from ledger_entries where txn_id = '{LedgerTxn}';
            delete from ledger_transactions where id = '{LedgerTxn}';
            """);

        var check = (await RunChecksAsync("phase1", BaselineEffect()))
            .Single(result => result.Check == "CK9");

        Assert.False(check.Passed);
        Assert.Contains(Payment.ToString(), check.SampleIds);
    }

    [Fact]
    public async Task CK10_detects_captured_minor_disagreeing_with_ledger()
    {
        await _fixture.ResetAsync();
        await SeedCleanFixtureAsync();
        await ExecuteAsync($"update payments set captured_minor = 90 where id = '{Payment}';");

        var check = (await RunChecksAsync("phase1", BaselineEffect()))
            .Single(result => result.Check == "CK10");

        Assert.False(check.Passed);
        Assert.Contains(Payment.ToString(), check.SampleIds);
    }


    [Fact]
    public async Task CK4_phase3_detects_unpublished_outbox()
    {
        await _fixture.ResetAsync();
        await SeedCleanFixtureAsync();
        await ExecuteAsync(
            $"update outbox set created_at = now() - interval '2 minutes' where id = '{Outbox}';");

        var check = (await RunChecksAsync("phase3", BaselineEffect()))
            .Single(result => result.Check == "CK4");

        Assert.False(check.Passed);
        Assert.Contains(Outbox.ToString(), check.SampleIds);
    }

    [Fact]
    public async Task CK6_detects_growing_outbox_age()
    {
        await _fixture.ResetAsync();
        await using var prometheus = new PrometheusStub(
            """
            {"status":"success","data":{"resultType":"matrix","result":[
              {"metric":{"__name__":"op_outbox_oldest_age_seconds"},
               "values":[[1700000000,"1"],[1700000015,"2"],[1700000030,"400"]]}
            ]}}
            """);

        var check = (await CheckRunner.RunAsync(new CheckOptions
            {
                Db = _fixture.ConnectionString,
                Effects = null,
                Stage = "phase3",
                Prom = prometheus.Url,
                From = DateTimeOffset.FromUnixTimeSeconds(1700000000),
                To = DateTimeOffset.FromUnixTimeSeconds(1700000030),
            }))
            .Single(result => result.Check == "CK6");

        Assert.True(check.Applicable);
        Assert.False(check.Passed);
        Assert.Contains("op_outbox_oldest_age_seconds", check.SampleIds);
    }

    [Fact]
    public async Task CK7_detects_stale_clearing_balance()
    {
        await _fixture.ResetAsync();
        await SeedCleanFixtureAsync();
        await ExecuteAsync(
            $"update ledger_entries set created_at = now() - interval '8 days' where txn_id = '{LedgerTxn}';");

        var check = (await RunChecksAsync("phase2", BaselineEffect()))
            .Single(result => result.Check == "CK7");

        Assert.False(check.Passed);
        Assert.True(check.MoneyImpactMinor > 0);
    }

    [Fact]
    public async Task CK8_detects_stale_settlement()
    {
        await _fixture.ResetAsync();
        await SeedCleanFixtureAsync();
        await ExecuteAsync($"""
            insert into settlement_lines
              (psp, psp_account_id, file_id, line_no, kind, amount_minor, currency, state, received_at)
            values
              ('mock', '{PspAccount}', 'file-1', 1, 'capture', 100, 'USD', 'UNMATCHED',
               now() - interval '25 hours');
            """);

        var check = (await RunChecksAsync("phase2", BaselineEffect()))
            .Single(result => result.Check == "CK8");

        Assert.False(check.Passed);
        Assert.True(check.Violations > 0);
    }

    [Fact]
    public async Task CK8_detects_a_late_file_and_fails_on_zero_rows()
    {
        await _fixture.ResetAsync();
        await SeedCleanFixtureAsync();
        await ExecuteAsync($"""
            insert into settlement_files
              (psp_account_id, file_id, business_date, state, lines_total, received_at)
            values ('{PspAccount}', 'file-late', current_date, 'receiving', 1,
                    now() - interval '25 hours');
            insert into settlement_lines
              (psp, psp_account_id, file_id, line_no, kind, amount_minor, currency, state, received_at)
            values ('mock', '{PspAccount}', 'file-late', 1, 'capture', 100, 'USD', 'RECEIVED',
                    now() - interval '25 hours');
            """);

        var check = (await RunChecksAsync("phase2", BaselineEffect()))
            .Single(result => result.Check == "CK8");

        Assert.False(check.Passed);
        Assert.Contains(check.SampleIds, id => id.StartsWith("file:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_check_fails_on_zero_rows()
    {
        await _fixture.ResetAsync();

        var results = await RunChecksAsync("phase3");

        Assert.All(
            results.Where(result => result.Applicable),
            result => Assert.False(result.Passed, $"{result.Check} passed on zero rows."));
        foreach (var check in new[] { "CK1", "CK2", "CK3", "CK4", "CK5", "CK7", "CK8", "CK9", "CK10" })
        {
            Assert.True(
                results.Single(result => result.Check == check).Applicable,
                $"{check} should be applicable at phase3.");
        }
    }

    [Fact]
    public async Task Inapplicable_check_reports_not_applicable_never_passed()
    {
        await _fixture.ResetAsync();
        await SeedCleanFixtureAsync();

        var check = (await RunChecksAsync("phase0", BaselineEffect()))
            .Single(result => result.Check == "CK7");

        Assert.False(check.Applicable);
        Assert.False(check.Passed);
        Assert.NotNull(check.Reason);
    }

    private string BaselineEffect() =>
        $"{Attempt},sale,100,USD,{DateTimeOffset.UtcNow:O}";

    private async Task<IReadOnlyList<CheckResult>> RunChecksAsync(string stage, params string[] effects)
    {
        var path = Path.Combine(Path.GetTempPath(), $"op-effects-{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllLinesAsync(
                path,
                new[] { "key,op,amount_minor,currency,at" }.Concat(effects));
            return await CheckRunner.RunAsync(new CheckOptions
            {
                Db = _fixture.ConnectionString,
                Effects = path,
                Stage = stage,
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    private async Task SeedCleanFixtureAsync()
    {
        await ExecuteAsync($"""
            insert into psp_accounts (id, merchant_id, psp, mode, credential_ref, rate_limit_per_s)
            values ('{PspAccount}', '{Merchant}', 'mock', 'test', 'ref', 100);

            insert into accounts (id, merchant_id, account_type, class, class_of_money, currency)
            values
              ('{Receivable}', '{Merchant}', 'psp_receivable', 'clearing', 'real', 'USD'),
              ('{Payable}', '{Merchant}', 'merchant_payable', 'clearing', 'real', 'USD');

            insert into payments
              (id, merchant_id, state, amount_minor, currency, captured_minor, event_seq, expires_at)
            values
              ('{Payment}', '{Merchant}', 'CAPTURED', 100, 'USD', 100, 1, now() + interval '1 day');

            insert into payment_attempts
              (attempt_id, payment_id, merchant_id, operation, psp, psp_account_id, state,
               method_ref, method_type, psp_reference)
            values
              ('{Attempt}', '{Payment}', '{Merchant}', 'sale', 'mock', '{PspAccount}', 'SUCCEEDED',
               'tok', 'card', 'psp_ref');

            insert into outbox
              (id, merchant_id, aggregate_id, event_type, sequence, schema_version, payload, created_at)
            values
              ('{Outbox}', '{Merchant}', '{Payment}', 'payment.captured', 1, 1, 'null'::jsonb, now());

            insert into ledger_transactions
              (id, merchant_id, payment_id, kind, source_event_id, effective_at)
            values
              ('{LedgerTxn}', '{Merchant}', '{Payment}', 'capture', '{LedgerTxn}', now());

            insert into ledger_entries
              (txn_id, entry_no, merchant_id, account_id, subject_id, direction, amount_minor, currency)
            values
              ('{LedgerTxn}', 1, '{Merchant}', '{Receivable}', '{Payment}', 'D', 100, 'USD'),
              ('{LedgerTxn}', 2, '{Merchant}', '{Payable}', '{Payment}', 'C', 100, 'USD');
            """);
    }

    private static IEnumerable<string> SubPlans(JsonElement plan)
    {
        if (plan.TryGetProperty("Parent Relationship", out var relationship)
            && relationship.GetString() == "SubPlan")
        {
            yield return plan.TryGetProperty("Subplan Name", out var name) ? name.GetString()! : "SubPlan";
        }

        if (plan.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                foreach (var found in SubPlans(child))
                {
                    yield return found;
                }
            }
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await _fixture.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class PrometheusStub : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        private readonly string _body;

        public PrometheusStub(string body)
        {
            _body = body;
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public string Url { get; }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            try
            {
                await _loop;
            }
            catch (HttpListenerException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task LoopAsync()
        {
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                var bytes = Encoding.UTF8.GetBytes(_body);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }
    }

}
