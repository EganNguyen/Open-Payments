using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenPayments.Console.Data;
using OpenPayments.Console.Security;

namespace OpenPayments.Console.Pages.Staff;

/// <summary>People and roles. Admin only; desktop only in the layout. Admin has no money action.</summary>
[Authorize(Policy = ConsolePolicies.CanAdmin)]
public sealed class IndexModel(IAdminStore store) : PageModel
{
    public IReadOnlyList<OpenPayments.Console.Security.Staff> People { get; private set; } = [];

    public string? Notice { get; private set; }

    [BindProperty] public Guid StaffId { get; set; }

    [BindProperty] public string? Role { get; set; }

    [BindProperty] public string? Subject { get; set; }

    [BindProperty] public string? Email { get; set; }

    public async Task OnGetAsync(CancellationToken ct) => People = await store.ListStaffAsync(ct);

    public async Task<IActionResult> OnPostRoleAsync(CancellationToken ct)
    {
        if (!ConsolePolicies.TryParse(Role ?? string.Empty, out var role))
        {
            return BadRequest();
        }

        var result = await store.SetRoleAsync(StaffId, role, CurrentStaff.Get(HttpContext).Id, ct);
        return await AfterAsync(result, ct);
    }

    public async Task<IActionResult> OnPostDisableAsync(CancellationToken ct) =>
        await AfterAsync(await store.SetDisabledAsync(StaffId, disabled: true, CurrentStaff.Get(HttpContext).Id, ct), ct);

    public async Task<IActionResult> OnPostEnableAsync(CancellationToken ct) =>
        await AfterAsync(await store.SetDisabledAsync(StaffId, disabled: false, CurrentStaff.Get(HttpContext).Id, ct), ct);

    public async Task<IActionResult> OnPostAddAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Subject) || string.IsNullOrWhiteSpace(Email) || !ConsolePolicies.TryParse(Role ?? string.Empty, out var role))
        {
            Notice = "Enter the person's identity-provider subject, email and role.";
            People = await store.ListStaffAsync(ct);
            return Page();
        }

        await store.AddStaffAsync(Subject.Trim(), Email.Trim(), role, CurrentStaff.Get(HttpContext).Id, ct);
        return RedirectToPage();
    }

    private async Task<IActionResult> AfterAsync(StaffChange result, CancellationToken ct)
    {
        if (result == StaffChange.Done)
        {
            return RedirectToPage();
        }

        Notice = result == StaffChange.LastAdmin
            ? "This is the last active admin. Make someone else an admin first."
            : "That person wasn't found.";
        People = await store.ListStaffAsync(ct);
        return Page();
    }
}
