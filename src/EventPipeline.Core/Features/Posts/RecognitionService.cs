using System.Security.Cryptography;
using System.Text;
using EventPipeline.Core.Contracts;
using EventPipeline.Core.Data;
using EventPipeline.Core.Entities;
using EventPipeline.Core.Features.Events;
using EventPipeline.Core.Llm;
using Microsoft.EntityFrameworkCore;

namespace EventPipeline.Core.Features.Posts;

public interface IRecognitionService
{
    /// <summary>
    /// Recognizes events in the given Instagram posts. Posts whose URL is already in
    /// the registry — events and non-events alike — skip the LLM entirely. Duplicates
    /// of an already persisted event (same real event announced in another post,
    /// decided by the LLM) are not saved again: the stored event is the one returned.
    /// </summary>
    Task<RecognitionResponse> RecognizeAsync(
        List<InstagramPost> posts,
        string deepSeekApiKey,
        DateRange? dateRange = null,
        CancellationToken ct = default);
}

public class RecognitionService : IRecognitionService
{
    private readonly IDeepSeekService _deepSeekService;
    private readonly IPostRegistryService _postRegistry;
    private readonly AppDbContext _dbContext;
    private readonly ILogger<RecognitionService> _logger;

    public RecognitionService(
        IDeepSeekService deepSeekService,
        IPostRegistryService postRegistry,
        AppDbContext dbContext,
        ILogger<RecognitionService> logger)
    {
        _deepSeekService = deepSeekService;
        _postRegistry = postRegistry;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<RecognitionResponse> RecognizeAsync(
        List<InstagramPost> posts,
        string deepSeekApiKey,
        DateRange? dateRange = null,
        CancellationToken ct = default)
    {
        var (analyses, isValidEvent, candidates) =
            await BuildEventCandidatesAsync(posts, deepSeekApiKey, dateRange, ct);

        // 1. Posts whose PostId is already persisted are known events: their stored
        //    record stays.
        var existingPostIds = await _dbContext.EventRecords
            .Where(e => candidates.Select(ep => ep.PostId).Contains(e.PostId))
            .Select(e => e.PostId)
            .ToListAsync(ct);

        var freshCandidates = candidates
            .Where(c => !existingPostIds.Contains(c.PostId))
            .ToList();

        // 2. Ask the LLM which fresh candidates are the same real event as one already
        //    persisted (or as another candidate of this batch) and drop them: the
        //    already stored event stays and the duplicate is not saved again. With a
        //    single candidate and no persisted events there is nothing to compare.
        var existingEvents = await LoadExistingEventsForDedupAsync(dateRange, ct);

        var droppedBy = new Dictionary<string, string>(); // candidate id -> id of the event that stays
        if (freshCandidates.Count > 0 && (freshCandidates.Count > 1 || existingEvents.Count > 0))
        {
            var groups = await _deepSeekService.FindDuplicateCandidatesAsync(
                freshCandidates.Select(EventOccurrence.ToCleanupItem).ToList(),
                existingEvents.Select(EventOccurrence.ToCleanupItem).ToList(),
                deepSeekApiKey, ct);

            droppedBy = ResolveDroppedCandidates(groups, freshCandidates, existingEvents);
            if (droppedBy.Count > 0)
                _logger.LogInformation("Recognition dropped {Count} duplicate candidates", droppedBy.Count);
        }

        // 3. Persist only the survivors.
        await PersistNewEventsAsync(
            freshCandidates.Where(c => !droppedBy.ContainsKey(c.EventUniqueId)).ToList(), ct);

        // 4. Build the response: every valid event post maps to the persisted event
        //    that stays — its own record, or the kept one when dropped as duplicate.
        var allEventPostIds = candidates.Select(c => c.PostId).ToHashSet();
        var dbEvents = await _dbContext.EventRecords
            .Where(e => allEventPostIds.Contains(e.PostId))
            .ToListAsync(ct);

        var keptByUniqueId = new Dictionary<string, EventRecord>();
        foreach (var e in existingEvents)
            keptByUniqueId.TryAdd(e.EventUniqueId, e);
        foreach (var e in dbEvents)
            keptByUniqueId.TryAdd(e.EventUniqueId, e);

        var dbEventsByPostId = dbEvents.ToDictionary(e => e.PostId);
        var eventsByPostId = new Dictionary<string, EventRecord>();
        foreach (var candidate in candidates)
        {
            if (existingPostIds.Contains(candidate.PostId)
                && dbEventsByPostId.TryGetValue(candidate.PostId, out var known))
            {
                eventsByPostId[candidate.PostId] = known;
            }
            else if (droppedBy.TryGetValue(candidate.EventUniqueId, out var keptId)
                     && keptByUniqueId.TryGetValue(keptId, out var keptEvent))
            {
                eventsByPostId[candidate.PostId] = keptEvent;
            }
            else if (dbEventsByPostId.TryGetValue(candidate.PostId, out var ownRecord))
            {
                eventsByPostId[candidate.PostId] = ownRecord;
            }
        }

        return MapToResponse(posts, analyses, eventsByPostId, isValidEvent);
    }

    /// <summary>
    /// Common first half of recognition: resolves the analysis of every post
    /// (registry first, LLM only for unknown URLs), decides which posts are valid
    /// events for the requested date range, and builds the candidate event records.
    /// Repeated posts within the same batch are processed only once.
    /// </summary>
    private async Task<(List<PostAnalysisResult> Analyses, bool[] IsValidEvent, List<EventRecord> Candidates)>
        BuildEventCandidatesAsync(
            List<InstagramPost> posts,
            string deepSeekApiKey,
            DateRange? dateRange,
            CancellationToken ct)
    {
        var analyses = await AnalyzePostsWithRegistryAsync(posts, deepSeekApiKey, dateRange, ct);

        // Decide which posts are valid events for the requested date range. A post is
        // valid when the LLM says it is an event AND, if a range was requested, the
        // event (or its recurrence) occurs within that range.
        var isValidEvent = new bool[analyses.Count];
        for (var i = 0; i < analyses.Count; i++)
        {
            isValidEvent[i] = analyses[i].IsEvent
                && (dateRange == null
                    || RecurrenceEvaluator.OccursInRange(analyses[i], dateRange.From, dateRange.To));
        }

        var eventPosts = new Dictionary<string, EventRecord>();
        for (int i = 0; i < posts.Count && i < analyses.Count; i++)
        {
            if (!isValidEvent[i])
                continue;

            var post = posts[i];
            if (eventPosts.ContainsKey(post.PostId))
                continue;

            eventPosts[post.PostId] = BuildEventRecord(post, analyses[i]);
        }

        return (analyses, isValidEvent, eventPosts.Values.ToList());
    }

    /// <summary>
    /// Resolves the analysis of every post through the post registry: posts whose
    /// URL is already in the registry with a stored analysis — events and non-events
    /// alike — skip the LLM entirely. Only URLs the system has never analyzed are
    /// registered and sent to the LLM. Concurrent runs serialize the analysis phase
    /// (global turn), so only the first one pays for new posts and the rest find
    /// them stored.
    /// </summary>
    private async Task<List<PostAnalysisResult>> AnalyzePostsWithRegistryAsync(
        List<InstagramPost> posts,
        string deepSeekApiKey,
        DateRange? dateRange,
        CancellationToken ct)
    {
        var urls = posts.Select(p => p.Url).ToList();
        var known = await _postRegistry.GetAnalysesByUrlAsync(urls, ct);

        var analyses = new PostAnalysisResult[posts.Count];
        var fresh = new List<int>();
        for (var i = 0; i < posts.Count; i++)
        {
            if (known.TryGetValue(posts[i].Url, out var analysis))
                analyses[i] = analysis;
            else
                fresh.Add(i);
        }

        if (fresh.Count == 0)
        {
            _logger.LogInformation("All {Count} posts already known; no LLM calls", posts.Count);
            return analyses.ToList();
        }

        await _postRegistry.WaitForAnalyzeTurnAsync(ct);
        try
        {
            // Another run may have analyzed these URLs while we were waiting.
            known = await _postRegistry.GetAnalysesByUrlAsync(urls, ct);

            var toAnalyze = new List<int>();
            for (var i = 0; i < posts.Count; i++)
            {
                if (known.TryGetValue(posts[i].Url, out var analysis))
                    analyses[i] = analysis;
                else
                    toAnalyze.Add(i);
            }

            if (toAnalyze.Count == 0)
            {
                _logger.LogInformation("All {Count} posts already known after waiting; no LLM calls", posts.Count);
                return analyses.ToList();
            }

            if (toAnalyze.Count < posts.Count)
                _logger.LogInformation("{New} new posts to analyze ({Known} already known)",
                    toAnalyze.Count, posts.Count - toAnalyze.Count);

            // Register the new posts first (rows with a null analysis), then analyze.
            // Rows left unanalyzed by a failure are picked up by the next request.
            await _postRegistry.RegisterPostsAsync(
                toAnalyze.Select(i => posts[i]).ToList(), ct);

            var freshAnalyses = await _deepSeekService.AnalyzePostsAsync(
                toAnalyze.Select(i => posts[i]).ToList(), deepSeekApiKey, dateRange, ct);

            var entries = new List<(string Url, PostAnalysisResult Analysis)>(toAnalyze.Count);
            for (var j = 0; j < toAnalyze.Count; j++)
            {
                var index = toAnalyze[j];
                var analysis = j < freshAnalyses.Count
                    ? freshAnalyses[j]
                    : new PostAnalysisResult { IsEvent = false };
                analyses[index] = analysis;
                entries.Add((posts[index].Url, analysis));
            }

            await _postRegistry.StoreAnalysesAsync(entries, ct);
            return analyses.ToList();
        }
        finally
        {
            _postRegistry.ReleaseAnalyzeTurn();
        }
    }

    private static EventRecord BuildEventRecord(InstagramPost post, PostAnalysisResult analysis)
    {
        return new EventRecord
        {
            EventUniqueId = GenerateEventUniqueId(post, analysis),
            Title = analysis.Title ?? "Sin título",
            EventDate = CombineDateAndTime(ParseEventDate(analysis.EventDate), analysis.EventTime),
            EventDateDescription = analysis.EventDateDescription,
            Summary = analysis.Summary ?? "Sin resumen",
            IsRecurrent = analysis.IsRecurrent,
            RecurrenceType = string.IsNullOrWhiteSpace(analysis.RecurrenceType)
                ? null
                : Truncate(analysis.RecurrenceType, 20),
            RecurrenceDaysOfWeek = FormatRecurrenceDays(analysis.RecurrenceDaysOfWeek),
            RecurrenceStartDate = ParseEventDate(analysis.RecurrenceStartDate),
            RecurrenceEndDate = ParseEventDate(analysis.RecurrenceEndDate),
            Account = post.Account,
            PostId = post.PostId,
            Caption = Truncate(post.Caption, 4000),
            PostDatetime = post.Datetime,
            Url = post.Url,
            ImageUrl = post.ImageUrl,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Persists the given new events, skipping any whose PostId was inserted
    /// concurrently after the duplicate check (the unique PostId index makes the save
    /// fail, and only the still-missing events are retried).
    /// </summary>
    private async Task PersistNewEventsAsync(List<EventRecord> newEvents, CancellationToken ct)
    {
        if (newEvents.Count == 0)
            return;

        _dbContext.EventRecords.AddRange(newEvents);

        try
        {
            await _dbContext.SaveChangesAsync(ct);
            _logger.LogInformation("Persisted {Count} new events", newEvents.Count);
        }
        catch (DbUpdateException ex)
        {
            // Race: a concurrent request inserted one or more of these events after
            // our duplicate check. Detach the pending inserts, re-check against the
            // DB and retry only the ones that are still missing.
            _logger.LogWarning(ex, "Duplicate key conflict while saving events. Re-checking and retrying.");

            foreach (var entry in _dbContext.ChangeTracker.Entries<EventRecord>().ToList())
            {
                _dbContext.Entry(entry.Entity).State = EntityState.Detached;
            }

            var nowExistingPostIds = await _dbContext.EventRecords
                .Where(e => newEvents.Select(n => n.PostId).Contains(e.PostId))
                .Select(e => e.PostId)
                .ToListAsync(ct);

            var remaining = newEvents
                .Where(n => !nowExistingPostIds.Contains(n.PostId))
                .ToList();

            if (remaining.Count > 0)
            {
                _dbContext.EventRecords.AddRange(remaining);
                await _dbContext.SaveChangesAsync(ct);
                _logger.LogInformation("Persisted {Count} new events after retry", remaining.Count);
            }
        }
    }

    /// <summary>
    /// Resolves the LLM duplicate groups into the candidates that must not be
    /// persisted, mapped to the persisted event that stays for each of them.
    /// Candidates grouped with an existing event are always dropped (the existing
    /// event stays); groups with only new events drop their non-keeper candidates.
    /// </summary>
    private static Dictionary<string, string> ResolveDroppedCandidates(
        List<DuplicateGroupResult> groups,
        List<EventRecord> candidates,
        List<EventRecord> existingEvents)
    {
        var candidateIds = candidates.Select(c => c.EventUniqueId).ToHashSet();
        var existingIds = existingEvents.Select(e => e.EventUniqueId).ToHashSet();

        var dropped = new Dictionary<string, string>();

        // First pass: groups that reference an existing event drop all their candidates.
        foreach (var group in groups)
        {
            var groupIds = group.DuplicateEventIds.Append(group.KeepEventId).ToList();
            var groupCandidates = groupIds.Where(candidateIds.Contains).ToList();
            if (groupCandidates.Count == 0)
                continue;

            var groupExisting = groupIds.Where(existingIds.Contains).ToList();
            if (groupExisting.Count == 0)
                continue;

            var keptId = groupExisting.Contains(group.KeepEventId) ? group.KeepEventId : groupExisting[0];
            foreach (var candidateId in groupCandidates)
                dropped[candidateId] = keptId;
        }

        // Second pass: groups with only new events dedupe among themselves (the
        // keeper survives, the rest are dropped).
        foreach (var group in groups)
        {
            var groupIds = group.DuplicateEventIds.Append(group.KeepEventId).ToList();
            if (groupIds.Any(existingIds.Contains))
                continue; // mixed groups are handled above

            if (!candidateIds.Contains(group.KeepEventId))
                continue; // unknown keeper: skip the group, like cleanup does

            foreach (var duplicateId in group.DuplicateEventIds)
            {
                if (duplicateId == group.KeepEventId)
                    continue; // self-reference: a keeper is never dropped by its own group
                if (candidateIds.Contains(duplicateId))
                    dropped.TryAdd(duplicateId, group.KeepEventId);
            }
        }

        // Resolve chains: a keeper candidate dropped in the first pass leaves its own
        // duplicates pointing at it — follow to the final persisted event.
        foreach (var key in dropped.Keys.ToList())
        {
            var seen = new HashSet<string> { key };
            var target = dropped[key];
            while (dropped.TryGetValue(target, out var next))
            {
                if (!seen.Add(target))
                    break; // contradictory cycles: keep the current mapping
                target = next;
            }
            dropped[key] = target;
        }

        return dropped;
    }

    /// <summary>
    /// The already persisted events sent to the LLM for the duplicate check: the ones
    /// occurring within the requested date range plus the events without any date (so
    /// an undated candidate can match a dated one), or all of them when no range was
    /// requested.
    /// </summary>
    private async Task<List<EventRecord>> LoadExistingEventsForDedupAsync(DateRange? dateRange, CancellationToken ct)
    {
        if (dateRange == null)
            return await _dbContext.EventRecords.AsNoTracking().ToListAsync(ct);

        var records = await _dbContext.EventRecords.AsNoTracking().ToListAsync(ct);
        return records
            .Where(r => EventOccurrence.OccursInRange(r, dateRange.From ?? DateTime.MinValue, dateRange.To ?? DateTime.MaxValue)
                        || EventOccurrence.HasNoDate(r))
            .ToList();
    }

    private static string GenerateEventUniqueId(InstagramPost post, PostAnalysisResult analysis)
    {
        var raw = $"{post.PostId}-{post.Account}-{analysis.Title}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..12];
        var datePart = DateTime.UtcNow.ToString("yyyyMMdd");
        return $"EVT-{datePart}-{hash}";
    }

    /// <summary>Aplica la hora del cartel (event_time) a la fecha, si viene y es válida.</summary>
    private static DateTime? CombineDateAndTime(DateTime? date, string? eventTime)
    {
        if (date == null || string.IsNullOrWhiteSpace(eventTime))
            return date;

        return TimeSpan.TryParse(eventTime.Trim(), out var time)
            ? date.Value.Date + time
            : date;
    }

    private static DateTime? ParseEventDate(string? eventDateStr)
    {
        if (string.IsNullOrWhiteSpace(eventDateStr))
            return null;

        if (DateTime.TryParse(eventDateStr, out var parsed))
        {
            // Fechas con granularidad de DÍA: se guardan tal cual, SIN conversión a
            // UTC ni a ninguna zona horaria (ToUniversalTime desplazaba un día los
            // eventos cuando el valor era medianoche local).
            return DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        }

        return null;
    }

    private static RecognitionResponse MapToResponse(
        List<InstagramPost> posts,
        List<PostAnalysisResult> analyses,
        Dictionary<string, EventRecord> dbEventsByPostId,
        bool[] isValidEvent)
    {
        var results = new List<RecognizedEventDto>(posts.Count);
        var seenPostIds = new HashSet<string>();
        for (int i = 0; i < posts.Count && i < analyses.Count; i++)
        {
            var post = posts[i];
            if (!seenPostIds.Add(post.PostId))
                continue;

            if (isValidEvent[i] && dbEventsByPostId.TryGetValue(post.PostId, out var dbEvent))
            {
                // Valid event post — return full event data from DB
                results.Add(MapToDto(dbEvent));
            }
            else
            {
                // Non-event (or out-of-range) post — return only post fields, no event data
                results.Add(new RecognizedEventDto
                {
                    IsEvent = false,
                    Account = post.Account,
                    PostId = post.PostId,
                    Caption = post.Caption ?? string.Empty,
                    PostDatetime = post.Datetime,
                    Url = post.Url,
                    ImageUrl = post.ImageUrl,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        return new RecognitionResponse
        {
            TotalPosts = posts.Count,
            EventsFound = results.Count(r => r.IsEvent),
            Events = results
        };
    }

    private static RecognizedEventDto MapToDto(EventRecord e)
    {
        return new RecognizedEventDto
        {
            IsEvent = true,
            EventUniqueId = e.EventUniqueId,
            Title = e.Title,
            EventDate = e.EventDate,
            EventDateDescription = e.EventDateDescription,
            Summary = e.Summary,
            IsRecurrent = e.IsRecurrent,
            RecurrenceType = e.RecurrenceType,
            RecurrenceDaysOfWeek = e.RecurrenceDaysOfWeek,
            RecurrenceStartDate = e.RecurrenceStartDate,
            RecurrenceEndDate = e.RecurrenceEndDate,
            Account = e.Account,
            PostId = e.PostId,
            Caption = e.Caption,
            PostDatetime = e.PostDatetime,
            Url = e.Url,
            ImageUrl = e.ImageUrl,
            CreatedAt = e.CreatedAt
        };
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    /// <summary>
    /// Stores the LLM weekday list as a comma-separated string ("1,2,3,4") for the DB column.
    /// </summary>
    private static string? FormatRecurrenceDays(List<int>? days)
        => days is { Count: > 0 } ? string.Join(",", days) : null;
}
