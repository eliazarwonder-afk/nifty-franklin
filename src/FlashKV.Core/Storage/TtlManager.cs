using System;
using System.Threading;
using System.Threading.Tasks;

namespace FlashKV.Core.Storage;

/// <summary>
/// Active TTL expiration manager that periodically samples and purges expired keys.
/// </summary>
public sealed class TtlManager : IDisposable
{
    private readonly DataStore _store;
    private readonly Timer _timer;
    private readonly int _sampleCount;
    private int _isRunning;

    public TtlManager(DataStore store, int intervalMs = 100, int sampleCount = 20)
    {
        _store = store;
        _sampleCount = sampleCount;
        _timer = new Timer(OnTimerTick, null, intervalMs, intervalMs);
    }

    private void OnTimerTick(object? state)
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            return; // Skip if previous run is still active
        }

        try
        {
            _store.ActiveExpireCycle(_sampleCount);
        }
        catch
        {
            // Suppress background errors
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}
