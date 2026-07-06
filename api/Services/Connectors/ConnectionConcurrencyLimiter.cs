using System.Collections.Concurrent;

namespace ExcelDatasetManager.Api.Services.Connectors;

/// <summary>
/// Caps the number of concurrent live queries running against a single external db_connection,
/// so one noisy AI agent (or a burst of MCP calls) cannot exhaust a customer's production database
/// connection pool. One <see cref="SemaphoreSlim"/> per connection id, created lazily and kept for
/// the lifetime of the process — register this type as a singleton so counts persist across requests.
/// </summary>
public sealed class ConnectionConcurrencyLimiter
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _semaphores = new();

    /// <summary>
    /// Attempts to reserve one of up to <paramref name="max"/> concurrent slots for
    /// <paramref name="connectionId"/>, waiting up to <paramref name="wait"/> for a slot to free up.
    /// Returns an <see cref="IDisposable"/> that releases the slot on Dispose, or null if no slot
    /// became available within the wait window.
    /// </summary>
    public async Task<IDisposable?> TryEnterAsync(Guid connectionId, int max, TimeSpan wait, CancellationToken ct = default)
    {
        var semaphore = _semaphores.GetOrAdd(connectionId, _ => new SemaphoreSlim(max, max));

        var entered = await semaphore.WaitAsync(wait, ct);
        return entered ? new Slot(semaphore) : null;
    }

    private sealed class Slot(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            // Guard against double-release: a caller that disposes twice (e.g. once explicitly and
            // once via a `using` block) must not hand out a phantom extra slot to someone else.
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
