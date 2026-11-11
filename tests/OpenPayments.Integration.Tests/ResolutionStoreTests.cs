using Npgsql;
using OpenPayments.Domain;
using OpenPayments.Transitions;
using Xunit;

namespace OpenPayments.Integration.Tests;

[Collection("postgres")]
public sealed class ResolutionStoreTests(DbFixture db)
{
    [Fact]
    public async Task Applied_commits_state_ledger_outbox_resolution_and_audit_together()
    {
        await db.ResetAsync();
        var merchant = await db.SeedMerchantAsync();
        var payment = await db.SeedPaymentAsync(merchant, "REVIEW", attemptState: "REVIEW");
        var proposer = Guid.NewGuid();
        var approver = Guid.NewGuid();
        var resolution = await SeedResolutionAsync(merchant, payment, proposer);
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new ResolutionStore(new PaymentStore(runner), runner);

        var result = await store.ApplyApprovedAsync(
            resolution, approver, DateTimeOffset.UtcNow, CancellationToken.None);

        var applied = Assert.IsType<ResolutionResult.Applied>(result);
        Assert.Equal(PaymentState.Authorized, applied.Payment.State);
        Assert.Equal("applied", await db.TextAsync(
            $"select state from console.review_resolutions where id = '{resolution}'"));
        Assert.Equal(approver, await db.GuidAsync(
            $"select approved_by from console.review_resolutions where id = '{resolution}'"));
        Assert.Equal(1, await db.ScalarAsync(
            $"select count(*) from ledger_transactions where payment_id = '{payment.PaymentId.Value}' and kind = 'authorization'"));
        Assert.Equal(1, await db.ScalarAsync(
            $"select count(*) from console.audit_log where action = 'resolution.applied' and staff_id = '{approver}'"));
        Assert.Equal("AUTHORIZED", await db.TextAsync(
            $"select state from payments where id = '{payment.PaymentId.Value}'"));
    }

    [Fact]
    public async Task Failure_before_commit_leaves_nothing_changed()
    {
        await db.ResetAsync();
        var merchant = await db.SeedMerchantAsync();
        var payment = await db.SeedPaymentAsync(merchant, "REVIEW", attemptState: "REVIEW");
        var resolution = await SeedResolutionAsync(merchant, payment, Guid.NewGuid());
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new ResolutionStore(new PaymentStore(runner), runner)
        {
            BeforeCommit = () => throw new InvalidOperationException("failpoint"),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApplyApprovedAsync(
            resolution, Guid.NewGuid(), DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Equal("proposed", await db.TextAsync(
            $"select state from console.review_resolutions where id = '{resolution}'"));
        Assert.Equal("REVIEW", await db.TextAsync(
            $"select state from payments where id = '{payment.PaymentId.Value}'"));
        Assert.Equal(0, await db.ScalarAsync("select count(*) from ledger_transactions"));
        Assert.Equal(0, await db.ScalarAsync("select count(*) from console.audit_log"));
    }

    [Fact]
    public async Task Self_approval_is_refused_in_the_transaction()
    {
        await db.ResetAsync();
        var merchant = await db.SeedMerchantAsync();
        var payment = await db.SeedPaymentAsync(merchant, "REVIEW", attemptState: "REVIEW");
        var proposer = Guid.NewGuid();
        var resolution = await SeedResolutionAsync(merchant, payment, proposer);
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new ResolutionStore(new PaymentStore(runner), runner);

        var result = await store.ApplyApprovedAsync(
            resolution, proposer, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.IsType<ResolutionResult.SelfApproval>(result);
        Assert.Equal("proposed", await db.TextAsync(
            $"select state from console.review_resolutions where id = '{resolution}'"));
        Assert.Equal(0, await db.ScalarAsync("select count(*) from ledger_transactions"));
    }

    [Fact]
    public async Task Two_concurrent_approvals_apply_once()
    {
        await db.ResetAsync();
        var merchant = await db.SeedMerchantAsync();
        var payment = await db.SeedPaymentAsync(merchant, "REVIEW", attemptState: "REVIEW");
        var resolution = await SeedResolutionAsync(merchant, payment, Guid.NewGuid());
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new ResolutionStore(new PaymentStore(runner), runner);

        var results = await Task.WhenAll(
            store.ApplyApprovedAsync(resolution, Guid.NewGuid(), DateTimeOffset.UtcNow, CancellationToken.None),
            store.ApplyApprovedAsync(resolution, Guid.NewGuid(), DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Single(results, result => result is ResolutionResult.Applied);
        Assert.Single(results, result => result is ResolutionResult.AlreadyApplied);
        Assert.Equal(1, await db.ScalarAsync(
            $"select count(*) from ledger_transactions where payment_id = '{payment.PaymentId.Value}'"));
    }

    [Fact]
    public async Task Payment_that_left_review_returns_state_changed_and_no_ledger()
    {
        await db.ResetAsync();
        var merchant = await db.SeedMerchantAsync();
        var payment = await db.SeedPaymentAsync(merchant, "REVIEW", attemptState: "REVIEW");
        var resolution = await SeedResolutionAsync(merchant, payment, Guid.NewGuid());
        await using (var connection = await db.OpenAdminAsync())
        await using (var command = new NpgsqlCommand(
            $"update payments set state = 'CANCELED' where id = '{payment.PaymentId.Value}'",
            connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new ResolutionStore(new PaymentStore(runner), runner);
        var result = await store.ApplyApprovedAsync(
            resolution, Guid.NewGuid(), DateTimeOffset.UtcNow, CancellationToken.None);

        var changed = Assert.IsType<ResolutionResult.StateChanged>(result);
        Assert.Equal(PaymentState.Canceled, changed.Payment.State);
        Assert.Equal(0, await db.ScalarAsync("select count(*) from ledger_transactions"));
        Assert.Equal("proposed", await db.TextAsync(
            $"select state from console.review_resolutions where id = '{resolution}'"));
    }

    [Fact]
    public async Task Repeat_after_applied_returns_already_applied()
    {
        await db.ResetAsync();
        var merchant = await db.SeedMerchantAsync();
        var payment = await db.SeedPaymentAsync(merchant, "REVIEW", attemptState: "REVIEW");
        var resolution = await SeedResolutionAsync(merchant, payment, Guid.NewGuid());
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new ResolutionStore(new PaymentStore(runner), runner);
        await store.ApplyApprovedAsync(
            resolution, Guid.NewGuid(), DateTimeOffset.UtcNow, CancellationToken.None);

        var again = await store.ApplyApprovedAsync(
            resolution, Guid.NewGuid(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.IsType<ResolutionResult.AlreadyApplied>(again);
    }

    private async Task<Guid> SeedResolutionAsync(
        MerchantSeed merchant,
        SeededPayment payment,
        Guid proposer)
    {
        var id = Guid.NewGuid();
        await using var connection = await db.OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            """
            insert into console.review_resolutions
              (id, payment_id, attempt_id, kind, outcome, psp_answer, evidence, state, proposed_by)
            values (@id, @payment, @attempt, 'payment', 'authorize', 'approved',
                    'checked the PSP dashboard', 'proposed', @proposer)
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("payment", payment.PaymentId.Value);
        command.Parameters.AddWithValue("attempt", payment.AttemptId.Value);
        command.Parameters.AddWithValue("proposer", proposer);
        await command.ExecuteNonQueryAsync();
        return id;
    }
}
