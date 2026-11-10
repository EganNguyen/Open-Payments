namespace OpenPayments.Psp.Resilience;

/// <summary>A small circuit breaker: closed, open for a cooldown, then one trial closes it (C13).</summary>
public sealed class Breaker(int threshold, TimeSpan openFor, TimeProvider? clock = null)
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private int _failures;
    private DateTimeOffset _openedAt;
    private bool _open;

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                if (!_open)
                {
                    return false;
                }

                if (_clock.GetUtcNow() - _openedAt < openFor)
                {
                    return true;
                }

                // The cooldown passed: let one trial through, and close on its success.
                _open = false;
                _failures = 0;
                return false;
            }
        }
    }

    public void Success()
    {
        lock (_gate)
        {
            _failures = 0;
            _open = false;
        }
    }

    public void Failure()
    {
        lock (_gate)
        {
            _failures++;
            if (_failures >= threshold)
            {
                _open = true;
                _openedAt = _clock.GetUtcNow();
            }
        }
    }

    public void OpenNow()
    {
        lock (_gate)
        {
            _open = true;
            _openedAt = _clock.GetUtcNow();
        }
    }
}
