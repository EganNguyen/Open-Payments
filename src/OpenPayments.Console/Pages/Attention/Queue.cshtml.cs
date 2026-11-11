using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenPayments.Console.Data;
using OpenPayments.Console.Security;

namespace OpenPayments.Console.Pages.Attention;

/// <summary>N3: the full list behind one queue, 50 rows at a time, oldest first.</summary>
public sealed class QueueModel(
    IQueueReader queues,
    TimeProvider time) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "queue")] public string Key { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true, Name = "page")] public int PageNumber { get; set; } = 1;

    public Queue Queue { get; private set; }

    public QueuePage Result { get; private set; } = null!;

    public string Title { get; private set; } = string.Empty;

    public DateTimeOffset Now { get; private set; }

    public int Pages => Result is null ? 0 : Math.Max(1, (int)Math.Ceiling(Result.Count / 50.0));

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!TryParse(Key, out var queue))
        {
            return NotFound();
        }

        Queue = queue;
        if (int.TryParse(Request.Query["page"], out var requested))
        {
            PageNumber = Math.Max(1, requested);
        }

        PageNumber = Math.Max(1, PageNumber);
        Now = time.GetUtcNow();
        Title = Presentation.AttentionPresentation.For(queue).Title;
        Result = await queues.ReadQueueAsync(queue, PageNumber, ct);
        return Page();
    }

    private static bool TryParse(string value, out Queue queue)
    {
        switch (value)
        {
            case "review": queue = Queue.Review; return true;
            case "approval": queue = Queue.Approval; return true;
            case "orphan": queue = Queue.Orphan; return true;
            case "unresolved": queue = Queue.Unresolved; return true;
            case "expiring": queue = Queue.Expiring; return true;
            default: queue = default; return false;
        }
    }
}
