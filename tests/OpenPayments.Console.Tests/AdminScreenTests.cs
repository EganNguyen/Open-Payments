using System.Net;
using System.Text.RegularExpressions;
using Npgsql;
using OpenPayments.Console.Security;
using Xunit;

namespace OpenPayments.Console.Tests;

// Task C13: the audit log and Staff screens, with the database store behind sign-in.
[Collection("console-db")]
public sealed partial class AdminScreenTests(ConsoleDb db)
{
    [Fact]
    public async Task Audit_log_filters_by_date_person_and_payment_with_keyset_paging()
    {
        await db.ResetAsync();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var payment = Guid.NewGuid();
        var values = string.Join(",", Enumerable.Range(0, 60).Select(i =>
            $"('{(i % 2 == 0 ? alice : bob)}', 'event', 'note {i}', {(i % 3 == 0 ? $"'{payment}'" : "null")}, now() - interval '{i} minutes')"));
        await db.ExecAsync("primary", "insert into console.audit_log (staff_id, phase, action, payment_id, created_at) values " + values);
        await db.ExecAsync("primary", $"insert into console.audit_log (staff_id, phase, action, created_at) values ('{alice}', 'event', 'old', now() - interval '20 days')");
        await using var factory = new DbConsoleFactory(db);
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Viewer).Subject);

        // Paging: 60 rows in two pages, each once, no overlap.
        var first = await GetTextAsync(client, "/Audit");
        var firstIds = AuditId().Matches(first).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(50, firstIds.Count);
        var next = NextLink().Match(first).Groups[1].Value.Replace("&amp;", "&", StringComparison.Ordinal);
        var secondIds = AuditId().Matches(await GetTextAsync(client, next)).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(10, secondIds.Count);
        Assert.Empty(firstIds.Intersect(secondIds));
        Assert.DoesNotContain(">old (event)", first + string.Join("", secondIds), StringComparison.Ordinal);

        var byPerson = AuditId().Matches(await GetTextAsync(client, "/Audit?Staff=" + bob)).Count;
        Assert.Equal(30, byPerson);
        var byPayment = AuditId().Matches(await GetTextAsync(client, "/Audit?Payment=" + payment)).Count;
        Assert.Equal(20, byPayment);
        var oldRange = DateTimeOffset.UtcNow.AddDays(-20).ToString("yyyy-MM-dd");
        Assert.Contains("old (event)", await GetTextAsync(client, $"/Audit?From={oldRange}&To={oldRange}"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_page_has_no_edit_or_delete()
    {
        await db.ResetAsync();
        await db.ExecAsync("primary", $"insert into console.audit_log (staff_id, phase, action) values ('{Guid.NewGuid()}', 'event', 'note')");
        await using var factory = new DbConsoleFactory(db);
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Admin).Subject);

        var html = await GetTextAsync(client, "/Audit");

        Assert.DoesNotContain("method=\"post\" action=\"/Audit", html, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?i)>\s*(edit|delete)\s*<", html);
        using var body = new FormUrlEncodedContent([]);
        Assert.NotEqual(HttpStatusCode.OK, (await client.PostAsync("/Audit", body)).StatusCode);
    }

    [Fact]
    public async Task Only_admin_can_change_roles_and_each_change_is_audited()
    {
        await db.ResetAsync();
        await using var factory = DatabaseStaff();
        var admin = await AddStaffRowAsync("admin");
        var target = await AddStaffRowAsync("viewer");
        var approver = await AddStaffRowAsync("approver");

        using var approverClient = factory.ClientFor(approver.Subject);
        Assert.Equal(HttpStatusCode.Forbidden, (await approverClient.GetAsync("/Staff")).StatusCode);
        var approverPost = await PostStaffAsync(approverClient, "/Payments", "Role", target.Id, "admin");
        Assert.NotEqual(HttpStatusCode.OK, approverPost.StatusCode);
        Assert.Equal("viewer", await RoleAsync(target.Id));
        Assert.Equal(0L, await ScalarAsync("select count(*) from console.audit_log"));

        using var adminClient = factory.ClientFor(admin.Subject);
        var response = await PostStaffAsync(adminClient, "/Staff", "Role", target.Id, "operator");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("operator", await RoleAsync(target.Id));
        Assert.Equal(1L, await ScalarAsync($"select count(*) from console.audit_log where action = 'staff.role' and staff_id = '{admin.Id}' and result like '%{target.Id}%operator'"));
    }

    [Fact]
    public async Task Admin_cannot_remove_the_last_admin()
    {
        await db.ResetAsync();
        await using var factory = DatabaseStaff();
        var admin = await AddStaffRowAsync("admin");
        using var client = factory.ClientFor(admin.Subject);

        var demote = await PostStaffAsync(client, "/Staff", "Role", admin.Id, "viewer");
        Assert.Contains("This is the last active admin.", System.Net.WebUtility.HtmlDecode(await demote.Content.ReadAsStringAsync()), StringComparison.Ordinal);
        var disable = await PostStaffAsync(client, "/Staff", "Disable", admin.Id, null);
        Assert.Contains("This is the last active admin.", System.Net.WebUtility.HtmlDecode(await disable.Content.ReadAsStringAsync()), StringComparison.Ordinal);

        Assert.Equal("admin", await RoleAsync(admin.Id));
        Assert.Equal(0L, await ScalarAsync("select count(*) from console.audit_log"));
    }

    [Fact]
    public async Task Admin_without_money_role_cannot_act_on_payments()
    {
        await db.ResetAsync();
        var id = await db.SeedPaymentAsync("primary", "AUTHORIZED");
        await using var factory = new DbConsoleFactory(db);
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Admin).Subject);

        var detail = await GetTextAsync(client, "/Payments/" + id + "?fresh=1");
        Assert.DoesNotContain("data-action=", detail, StringComparison.Ordinal);
        var form = await client.GetAsync($"/Payments/{id}/Action/capture");
        Assert.NotEqual(HttpStatusCode.OK, form.StatusCode);
        Assert.Empty(factory.Api.Calls);
    }

    private DbConsoleFactory DatabaseStaff() => new DbConsoleFactory(db) { UseDatabaseStaff = true };

    private async Task<Staff> AddStaffRowAsync(string role)
    {
        var staff = new Staff(Guid.CreateVersion7(), "sub-" + Guid.NewGuid().ToString("N"), role + "@example.test",
            ConsolePolicies.TryParse(role, out var parsed) ? parsed : ConsoleRole.Viewer, null);
        await db.ExecAsync("primary", $"insert into console.staff (id, oidc_subject, email, role) values ('{staff.Id}', '{staff.Subject}', '{staff.Email}', '{role}')");
        return staff;
    }

    private static async Task<HttpResponseMessage> PostStaffAsync(HttpClient client, string formPage, string handler, Guid staffId, string? role)
    {
        var page = await GetTextAsync(client, formPage);
        var token = Antiforgery().Match(page).Groups[1].Value;
        var values = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("StaffId", staffId.ToString()),
        };
        if (role is not null)
        {
            values.Add(new("Role", role));
        }

        using var body = new FormUrlEncodedContent(values);
        return await client.PostAsync("/Staff?handler=" + handler, body);
    }

    private async Task<string> RoleAsync(Guid staffId) =>
        (string)(await ScalarObjectAsync($"select role from console.staff where id = '{staffId}'"))!;

    private async Task<long> ScalarAsync(string sql) => (long)(await ScalarObjectAsync(sql))!;

    private async Task<object?> ScalarObjectAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(db.AdminFor("primary"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    [GeneratedRegex("data-audit-id=\"(\\d+)\"")]
    private static partial Regex AuditId();

    [GeneratedRegex("href=\"(/Audit\\?[^\"]*Before=[^\"]+)\"")]
    private static partial Regex NextLink();

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex Antiforgery();

    private static async Task<string> GetTextAsync(HttpClient client, string url) =>
        System.Net.WebUtility.HtmlDecode(await client.GetStringAsync(url));
}
