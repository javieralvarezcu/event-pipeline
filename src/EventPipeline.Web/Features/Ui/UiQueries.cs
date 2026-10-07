using System.Text.Json;
using EventPipeline.Core.Data;
using EventPipeline.Core.Entities;
using EventPipeline.Web.Features.Jobs;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace EventPipeline.Web.Features.Ui;

/// <summary>Counts, latest runs and live activity for the Dashboard page.</summary>
public record DashboardData(
    int EventCount,
    int MuxoCount,
    int CrossMatchCount,
    int PostCount,
    int AnalyzedPostCount,
    int DeepSeekCallCount,
    long? TotalTokens,
    List<JobRun> LatestRuns,
    List<JobRun> RunningRuns,
    long EnqueuedCount,
    List<JobRunLog> RecentLogs);

public record PostRow(
    int Id,
    string Url,
    string Account,
    string PostId,
    string Caption,
    DateTime? PostDatetime,
    DateTime CreatedAtUtc,
    DateTime? AnalyzedAtUtc,
    bool? IsEvent,
    string? AnalysisTitle,
    string? EventUniqueId);

public record DeepSeekLogRow(
    int Id,
    string Operation,
    DateTime StartedAtUtc,
    long DurationMs,
    int TotalTokens,
    int? HttpStatusCode,
    bool Succeeded,
    string? ErrorMessage);

public record DeepSeekLogSummary(int CallCount, long? TotalTokens, decimal EstimatedCostUsd);

public class UiQueryService
{
    // Precio de deepseek-chat (USD por millón de tokens) — ESTIMACIÓN para el
    // panel de costes; actualizar aquí si cambia la tarifa.
    private const decimal InputPricePerMillion = 0.27m;   // entrada (sin caché)
    private const decimal OutputPricePerMillion = 1.10m;  // salida

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IJobRunService _jobRuns;

    public UiQueryService(IDbContextFactory<AppDbContext> dbFactory, IJobRunService jobRuns)
    {
        _dbFactory = dbFactory;
        _jobRuns = jobRuns;
    }

    public async Task<DashboardData> GetDashboardAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var eventCount = await db.EventRecords.CountAsync(ct);
        var muxoCount = await db.MuxoEvents.CountAsync(ct);
        var crossCount = await db.CrossMatches.CountAsync(ct);
        var postCount = await db.Posts.CountAsync(ct);
        var analyzedCount = await db.Posts.CountAsync(p => p.AnalysisJson != null, ct);
        var callCount = await db.DeepSeekCallLogs.CountAsync(ct);
        var totalTokens = await db.DeepSeekCallLogs.SumAsync(l => (long?)l.TotalTokens, ct);
        var latestRuns = await _jobRuns.GetLatestAsync(5, ct: ct);
        var runningRuns = await db.JobRuns.AsNoTracking()
            .Where(r => r.Status == "Running")
            .OrderByDescending(r => r.StartedAtUtc)
            .ToListAsync(ct);
        var recentLogs = await db.JobRunLogs.AsNoTracking()
            .OrderByDescending(l => l.LoggedAtUtc)
            .Take(100)
            .ToListAsync(ct);
        var enqueued = JobStorage.Current.GetMonitoringApi().EnqueuedCount("default");

        return new DashboardData(
            eventCount, muxoCount, crossCount, postCount, analyzedCount,
            callCount, totalTokens, latestRuns, runningRuns, enqueued, recentLogs);
    }

    public async Task<List<PostRow>> GetPostsAsync(int limit = 200, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var posts = await db.Posts.AsNoTracking()
            .OrderByDescending(p => p.CreatedAtUtc)
            .Take(limit)
            .ToListAsync(ct);

        // Map PostId → EventUniqueId (unique index: at most one event per post).
        var postIds = posts.Select(p => p.PostId).Distinct().ToList();
        var eventLinks = await db.EventRecords.AsNoTracking()
            .Where(e => postIds.Contains(e.PostId))
            .Select(e => new { e.PostId, e.EventUniqueId })
            .ToDictionaryAsync(e => e.PostId, e => e.EventUniqueId, ct);

        return posts.Select(p =>
        {
            PostAnalysisResult? analysis = null;
            if (p.AnalysisJson != null)
            {
                try
                {
                    analysis = JsonSerializer.Deserialize<PostAnalysisResult>(p.AnalysisJson);
                }
                catch (JsonException)
                {
                    // análisis corrupto: se muestra como "pendiente"
                }
            }

            eventLinks.TryGetValue(p.PostId, out var eventUniqueId);
            return new PostRow(
                p.Id, p.Url, p.Account, p.PostId,
                Truncate(p.Caption, 200), p.PostDatetime, p.CreatedAtUtc, p.AnalyzedAtUtc,
                analysis?.IsEvent, analysis?.Title, eventUniqueId);
        }).ToList();
    }

    public async Task<(List<DeepSeekLogRow> Rows, DeepSeekLogSummary Summary)> GetDeepSeekLogsAsync(
        int limit = 200, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var rows = await db.DeepSeekCallLogs.AsNoTracking()
            .OrderByDescending(l => l.StartedAtUtc)
            .Take(limit)
            .Select(l => new DeepSeekLogRow(
                l.Id, l.Operation, l.StartedAtUtc, l.DurationMs, l.TotalTokens,
                l.HttpStatusCode, l.Succeeded, l.ErrorMessage))
            .ToListAsync(ct);

        var totals = await db.DeepSeekCallLogs
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Calls = g.Count(),
                TotalTokens = g.Sum(l => (long?)l.TotalTokens),
                InputTokens = g.Sum(l => (long?)l.PromptTokens),
                OutputTokens = g.Sum(l => (long?)l.CompletionTokens),
            })
            .FirstOrDefaultAsync(ct);

        var cost = totals == null
            ? 0m
            : (totals.InputTokens ?? 0) / 1_000_000m * InputPricePerMillion
              + (totals.OutputTokens ?? 0) / 1_000_000m * OutputPricePerMillion;

        return (rows, new DeepSeekLogSummary(totals?.Calls ?? 0, totals?.TotalTokens, cost));
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
