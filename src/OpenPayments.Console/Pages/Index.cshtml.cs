using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OpenPayments.Console.Pages;

// Stage 1: home is Payments. Stage 2's C12 replaces this with Needs attention.
public sealed class IndexModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Payments/Index");
}
