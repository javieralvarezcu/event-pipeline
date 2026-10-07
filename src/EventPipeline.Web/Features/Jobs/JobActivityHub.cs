using System.Threading.Channels;
using EventPipeline.Core.Data;
using EventPipeline.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace EventPipeline.Web.Features.Jobs;

/// <summary>One activity entry broadcast to the live subscribers.</summary>
public abstract record JobActivity(int JobRunId, string JobName, DateTime AtUtc);

/// <summary>One user-facing log line emitted by a running job.</summary>
public sealed record JobLogEntry(int JobRunId, string JobName, DateTime LoggedAtUtc, string Message)
    : JobActivity(JobRunId, JobName, LoggedAtUtc);

/// <summary>A job run changed its status ("Running", "Succeeded", "Failed").</summary>
public sealed record JobRunEvent(int JobRunId, string JobName, DateTime AtUtc, string Status)
    : JobActivity(JobRunId, JobName, AtUtc);

public interface IJobActivityHub
{
    /// <summary>Persists the line and broadcasts it to the live subscribers.</summary>
    Task LogAsync(int jobRunId, string jobName, string message, CancellationToken ct = default);

    /// <summary>Broadcasts a status change of a run (not persisted: JobRuns already holds it).</summary>
    Task PublishRunAsync(int jobRunId, string jobName, string status, CancellationToken ct = default);

    /// <summary>
    /// Subscribes to the live stream. The returned reader must be disposed (or
    /// <see cref="Unsubscribe"/> called) when the subscriber goes away.
    /// </summary>
    (Guid Id, ChannelReader<JobActivity> Reader) Subscribe();

    void Unsubscribe(Guid id);
}

/// <summary>
/// In-process pub/sub of job progress lines. Jobs call LogAsync while they run;
/// the Dashboard subscribes and renders the lines as they arrive (same process,
/// so no polling needed). Every line is also persisted to JobRunLogs so the log
/// survives reloads and container restarts.
/// </summary>
public class JobActivityHub : IJobActivityHub
{
    private const int MaxBufferedPerSubscriber = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobActivityHub> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, Channel<JobActivity>> _subscribers = new();

    public JobActivityHub(IServiceScopeFactory scopeFactory, ILogger<JobActivityHub> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task LogAsync(int jobRunId, string jobName, string message, CancellationToken ct = default)
    {
        Broadcast(new JobLogEntry(jobRunId, jobName, DateTime.UtcNow, message));

        try
        {
            // La factory de DbContext es scoped: el hub (singleton) crea su propio
            // scope por línea para no consumir servicios scoped de otros ámbitos.
            using var scope = _scopeFactory.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.JobRunLogs.Add(new JobRunLog
            {
                JobRunId = jobRunId,
                JobName = jobName,
                Message = message,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // El log en vivo nunca debe tumbar el job.
            _logger.LogWarning(ex, "No se pudo persistir una línea del log del job {JobName}", jobName);
        }
    }

    public Task PublishRunAsync(int jobRunId, string jobName, string status, CancellationToken ct = default)
    {
        Broadcast(new JobRunEvent(jobRunId, jobName, DateTime.UtcNow, status));
        return Task.CompletedTask;
    }

    private void Broadcast(JobActivity entry)
    {
        lock (_lock)
        {
            foreach (var channel in _subscribers.Values)
            {
                channel.Writer.TryWrite(entry);
            }
        }
    }

    public (Guid Id, ChannelReader<JobActivity> Reader) Subscribe()
    {
        var channel = Channel.CreateBounded<JobActivity>(new BoundedChannelOptions(MaxBufferedPerSubscriber)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        var id = Guid.NewGuid();
        lock (_lock)
        {
            _subscribers[id] = channel;
        }

        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        lock (_lock)
        {
            if (_subscribers.Remove(id, out var channel))
            {
                channel.Writer.TryComplete();
            }
        }
    }
}
