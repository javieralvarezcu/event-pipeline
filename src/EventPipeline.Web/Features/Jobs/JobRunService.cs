using System.Text.Json;
using EventPipeline.Core.Data;
using EventPipeline.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace EventPipeline.Web.Features.Jobs;

public interface IJobRunService
{
    /// <summary>Opens a Running row and returns it.</summary>
    Task<JobRun> StartAsync(string jobName, string triggerType, CancellationToken ct = default);

    /// <summary>Marks the run Succeeded with a serialized summary.</summary>
    Task CompleteAsync(int runId, object summary, CancellationToken ct = default);

    /// <summary>Marks the run Failed with the exception message.</summary>
    Task FailAsync(int runId, Exception ex, CancellationToken ct = default);

    Task<List<JobRun>> GetLatestAsync(int count, string? jobName = null, CancellationToken ct = default);
}

/// <summary>
/// Run history of the scheduled jobs, shown in the Jobs page. Each job records its
/// own start/end and a JSON summary; the UI never depends on Hangfire internals.
/// </summary>
public class JobRunService : IJobRunService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly AppDbContext _db;
    private readonly IJobActivityHub _activity;

    public JobRunService(AppDbContext db, IJobActivityHub activity)
    {
        _db = db;
        _activity = activity;
    }

    public async Task<JobRun> StartAsync(string jobName, string triggerType, CancellationToken ct = default)
    {
        var run = new JobRun
        {
            JobName = jobName,
            TriggerType = triggerType,
            Status = "Running",
            StartedAtUtc = DateTime.UtcNow,
        };
        _db.JobRuns.Add(run);
        await _db.SaveChangesAsync(ct);
        await _activity.PublishRunAsync(run.Id, run.JobName, run.Status, ct);
        return run;
    }

    public async Task CompleteAsync(int runId, object summary, CancellationToken ct = default)
    {
        var run = await _db.JobRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null)
            return;

        run.Status = "Succeeded";
        run.CompletedAtUtc = DateTime.UtcNow;
        run.SummaryJson = JsonSerializer.Serialize(summary, JsonOptions);
        await _db.SaveChangesAsync(ct);
        await _activity.PublishRunAsync(run.Id, run.JobName, run.Status, ct);
    }

    public async Task FailAsync(int runId, Exception ex, CancellationToken ct = default)
    {
        var run = await _db.JobRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null)
            return;

        run.Status = "Failed";
        run.CompletedAtUtc = DateTime.UtcNow;
        run.Error = Truncate(ex.Message);
        await _db.SaveChangesAsync(ct);
        await _activity.PublishRunAsync(run.Id, run.JobName, run.Status, ct);
    }

    public Task<List<JobRun>> GetLatestAsync(int count, string? jobName = null, CancellationToken ct = default)
    {
        var query = _db.JobRuns.AsNoTracking().AsQueryable();
        if (jobName != null)
            query = query.Where(r => r.JobName == jobName);

        return query.OrderByDescending(r => r.StartedAtUtc)
            .Take(count)
            .ToListAsync(ct);
    }

    private static string Truncate(string text) =>
        text.Length <= 4000 ? text : text[..4000];
}
