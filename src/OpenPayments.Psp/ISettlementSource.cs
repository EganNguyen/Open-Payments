using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using OpenPayments.Domain;

namespace OpenPayments.Psp;

/// <summary>
/// Per-account settlement input (D63). Listing and reading are separate so an interrupted
/// ingest resumes file by file, and a repeated read changes nothing.
/// </summary>
public interface ISettlementSource
{
    Task<IReadOnlyList<SettlementFile>> ListFilesAsync(
        PspAccount account,
        DateOnly from,
        CancellationToken ct);

    IAsyncEnumerable<SettlementLine> ReadAsync(
        PspAccount account,
        string fileId,
        CancellationToken ct);
}

/// <summary>
/// Reads the mock PSP's itemized CSV. The mock keys its accounts by the platform account id, so
/// <c>psp_accounts.id</c> is the path segment.
/// </summary>
public sealed class MockSettlementSource(HttpClient client) : ISettlementSource
{
    public Task<IReadOnlyList<SettlementFile>> ListFilesAsync(
        PspAccount account,
        DateOnly from,
        CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var files = new List<SettlementFile>();
        for (var date = from; date <= today; date = date.AddDays(1))
        {
            files.Add(new SettlementFile(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), date));
        }

        return Task.FromResult<IReadOnlyList<SettlementFile>>(files);
    }

    public async IAsyncEnumerable<SettlementLine> ReadAsync(
        PspAccount account,
        string fileId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await client.GetAsync(
            $"admin/settlement/{account.Id:D}?date={Uri.EscapeDataString(fileId)}", ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var header = await reader.ReadLineAsync(ct);
        if (header is null)
        {
            yield break;
        }

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var cells = line.Split(',');
            if (cells.Length < 5)
            {
                continue;
            }

            yield return new SettlementLine(
                int.Parse(cells[0], CultureInfo.InvariantCulture),
                cells[1],
                cells[2].Length == 0 ? null : cells[2],
                new Money(long.Parse(cells[3], CultureInfo.InvariantCulture), CurrencyCode.Parse(cells[4])),
                cells.Length > 5 ? cells[5] : null,
                cells.Length > 6 ? cells[6] : null);
        }
    }
}
