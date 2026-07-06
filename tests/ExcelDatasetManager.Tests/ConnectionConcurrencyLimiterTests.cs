using ExcelDatasetManager.Api.Services.Connectors;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class ConnectionConcurrencyLimiterTests
{
    [Fact]
    public async Task Allows_up_to_max_concurrent_slots_then_rejects()
    {
        var limiter = new ConnectionConcurrencyLimiter();
        var connectionId = Guid.NewGuid();

        var slot1 = await limiter.TryEnterAsync(connectionId, 2, TimeSpan.FromMilliseconds(100));
        var slot2 = await limiter.TryEnterAsync(connectionId, 2, TimeSpan.FromMilliseconds(100));

        Assert.NotNull(slot1);
        Assert.NotNull(slot2);

        var slot3 = await limiter.TryEnterAsync(connectionId, 2, TimeSpan.FromMilliseconds(100));
        Assert.Null(slot3);

        slot1!.Dispose();
        slot2!.Dispose();
    }

    [Fact]
    public async Task Releasing_a_slot_frees_capacity_for_a_subsequent_request()
    {
        var limiter = new ConnectionConcurrencyLimiter();
        var connectionId = Guid.NewGuid();

        var slot1 = await limiter.TryEnterAsync(connectionId, 2, TimeSpan.FromMilliseconds(100));
        var slot2 = await limiter.TryEnterAsync(connectionId, 2, TimeSpan.FromMilliseconds(100));
        Assert.NotNull(slot1);
        Assert.NotNull(slot2);

        var blocked = await limiter.TryEnterAsync(connectionId, 2, TimeSpan.FromMilliseconds(100));
        Assert.Null(blocked);

        slot1!.Dispose();

        var slot3 = await limiter.TryEnterAsync(connectionId, 2, TimeSpan.FromMilliseconds(100));
        Assert.NotNull(slot3);

        slot2!.Dispose();
        slot3!.Dispose();
    }

    [Fact]
    public async Task Disposing_a_slot_twice_does_not_over_release_the_semaphore()
    {
        var limiter = new ConnectionConcurrencyLimiter();
        var connectionId = Guid.NewGuid();

        var slot1 = await limiter.TryEnterAsync(connectionId, 1, TimeSpan.FromMilliseconds(100));
        Assert.NotNull(slot1);

        slot1!.Dispose();
        slot1.Dispose(); // second dispose must be a no-op, not throw and not add extra capacity

        var slot2 = await limiter.TryEnterAsync(connectionId, 1, TimeSpan.FromMilliseconds(100));
        var slot3 = await limiter.TryEnterAsync(connectionId, 1, TimeSpan.FromMilliseconds(100));

        Assert.NotNull(slot2);
        Assert.Null(slot3); // only 1 slot total; double-dispose must not have granted a phantom extra slot

        slot2!.Dispose();
    }

    [Fact]
    public async Task Different_connections_have_independent_limits()
    {
        var limiter = new ConnectionConcurrencyLimiter();
        var connectionA = Guid.NewGuid();
        var connectionB = Guid.NewGuid();

        var slotA = await limiter.TryEnterAsync(connectionA, 1, TimeSpan.FromMilliseconds(100));
        var slotB = await limiter.TryEnterAsync(connectionB, 1, TimeSpan.FromMilliseconds(100));

        Assert.NotNull(slotA);
        Assert.NotNull(slotB);

        slotA!.Dispose();
        slotB!.Dispose();
    }
}
