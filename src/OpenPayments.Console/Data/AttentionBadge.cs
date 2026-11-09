using Microsoft.Extensions.Logging;
using OpenPayments.Console.Security;

namespace OpenPayments.Console.Data;

/// <summary>
/// The navigation badge: what the signed-in person can act on, read once per page load and
/// never polled. A failed read shows no badge rather than failing the page.
/// </summary>
public sealed class AttentionBadge(IQueueReader queues, TimeProvider time, ILogger<AttentionBadge> logger)
{
    public async Task<int?> CountAsync(HttpContext context)
    {
        if (CurrentStaff.Find(context) is not { } staff)
        {
            return null;
        }

        try
        {
            var count = await queues.CountActionableAsync(staff, time.GetUtcNow(), context.RequestAborted);
            return count > 0 ? count : null;
        }
        catch (Exception exception) when (exception is Npgsql.NpgsqlException or TimeoutException or System.Net.Sockets.SocketException or ReadTimeoutException)
        {
            logger.LogWarning(exception, "attention_badge_unavailable");
            return null;
        }
    }
}
