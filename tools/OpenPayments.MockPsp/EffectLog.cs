using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace OpenPayments.MockPsp;

/// <summary>
/// Appends one CSV line per PSP effect, and does not complete an append until the line is
/// flushed. The bounded channel gives backpressure instead of unbounded memory.
/// </summary>
public sealed class EffectLog : IAsyncDisposable
{
    private readonly Channel<PendingEffect> _channel;
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private readonly Task _writerTask;

    public EffectLog(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false,
        };
        if (_stream.Length == 0)
        {
            _writer.WriteLine("key,op,amount_minor,currency,at");
            _writer.Flush();
        }

        _channel = Channel.CreateBounded<PendingEffect>(new BoundedChannelOptions(10_000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _writerTask = Task.Run(WriteLoopAsync);
    }

    public async Task AppendAsync(string key, string op, long amountMinor, string currency)
    {
        var pending = new PendingEffect(
            key,
            op,
            amountMinor,
            currency,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        await _channel.Writer.WriteAsync(pending);
        await pending.Completion.Task;
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.Complete();
        await _writerTask;
        await _writer.FlushAsync();
        _writer.Dispose();
        await _stream.DisposeAsync();
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var pending in _channel.Reader.ReadAllAsync())
        {
            try
            {
                _writer.WriteLine(string.Join(
                    ',',
                    Escape(pending.Key),
                    Escape(pending.Op),
                    pending.AmountMinor.ToString(CultureInfo.InvariantCulture),
                    Escape(pending.Currency),
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
                await _writer.FlushAsync();
                pending.Completion.SetResult();
            }
            catch (Exception exception)
            {
                pending.Completion.SetException(exception);
            }
        }
    }

    private static string Escape(string value)
    {
        if (value.IndexOfAny([',', '"', '\n', '\r']) < 0)
        {
            return value;
        }

        return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }

    private sealed record PendingEffect(
        string Key,
        string Op,
        long AmountMinor,
        string Currency,
        TaskCompletionSource Completion);
}
