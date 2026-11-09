using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using OpenPayments.Console.Data;
using OpenPayments.Console.Security;

namespace OpenPayments.Console.Pages.Attention;

/// <summary>
/// Needs attention, the home page (N1 and N2 of the Needs attention plan). It only reads:
/// proposing and deciding arrive with C12.
/// </summary>
public sealed class IndexModel(IQueueReader queues, IOptions<SearchOptions> search, IOptions<AttentionOptions> options, TimeProvider time) : PageModel
{
    public AttentionQueues? Result { get; private set; }

    public string? Message { get; private set; }

    public DateTimeOffset Now { get; private set; }

    public Security.Staff Person { get; private set; } = null!;

    public AttentionOptions Settings => options.Value;

    /// <summary>Merchant IDs are shown only when the deployment manages more than one.</summary>
    public bool ShowMerchant => search.Value.Merchants.Count > 1;

    public IReadOnlyList<QueueView> Waiting => Result is null ? [] : [.. Result.Queues.Where(q => q.Count > 0)];

    public IReadOnlyList<QueueView> Clear => Result is null ? [] : [.. Result.Queues.Where(q => q.Count == 0)];

    /// <summary>When the data was true: the replica's lag before now.</summary>
    public DateTimeOffset CheckedAt => Now - (Result?.Lag ?? TimeSpan.Zero);

    public async Task OnGetAsync(CancellationToken ct)
    {
        Now = time.GetUtcNow();
        Person = CurrentStaff.Get(HttpContext);
        try
        {
            Result = await queues.ReadAsync(Now, ct);
        }
        catch (ReplicaUnavailableException)
        {
            Message = "The queues are unavailable because the read replica can't be reached. Looking up a payment by ID still works.";
        }
        catch (ReadTimeoutException)
        {
            Message = "The queues took too long to read. Try again in a moment.";
        }
    }
}
