using Npgsql;

namespace OpenPayments.Console.Security;

public sealed record Staff(Guid Id, string Subject, string Email, ConsoleRole Role, DateTimeOffset? DisabledAt);

public interface IStaffStore
{
    /// <summary>Read on every request and never cached in the cookie, so a role change or a
    /// disabled account applies on the next request.</summary>
    Task<Staff?> FindBySubjectAsync(string subject, CancellationToken ct);
}

public sealed class StaffStore(NpgsqlDataSource primary) : IStaffStore
{
    public async Task<Staff?> FindBySubjectAsync(string subject, CancellationToken ct)
    {
        await using var command = primary.CreateCommand(
            "select id, oidc_subject, email, role, disabled_at from console.staff where oidc_subject = @subject");
        command.Parameters.AddWithValue("subject", subject);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        if (!ConsolePolicies.TryParse(reader.GetString(3), out var role))
        {
            return null;
        }

        return new Staff(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            role,
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4));
    }
}
