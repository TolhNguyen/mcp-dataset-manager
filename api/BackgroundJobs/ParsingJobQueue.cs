using System.Threading.Channels;

namespace ExcelDatasetManager.Api.BackgroundJobs;

public record ParsingJob(Guid UserId, Guid DatasetId);

/// <summary>
/// Bounded in-memory channel for queueing parsing jobs from upload requests.
/// Survives only as long as the process — if you need durability across restarts, move this to a database queue.
/// </summary>
public class ParsingJobQueue
{
    private readonly Channel<ParsingJob> _channel = Channel.CreateBounded<ParsingJob>(
        new BoundedChannelOptions(capacity: 100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

    public ValueTask EnqueueAsync(ParsingJob job, CancellationToken ct) =>
        _channel.Writer.WriteAsync(job, ct);

    public ChannelReader<ParsingJob> Reader => _channel.Reader;
}
