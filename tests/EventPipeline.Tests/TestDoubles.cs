using System.Net;
using System.Text;
using System.Text.Json;
using EventPipeline.Core.Contracts;
using EventPipeline.Core.Entities;
using EventPipeline.Core.Llm;
using EventPipeline.Core.Features.Posts;
using EventPipeline.Core.Features.Muxo;
using EventPipeline.Core.Features.Events;
using EventPipeline.Web.Features.Settings;
using EventPipeline.Web.Features.IgAccounts;
using EventPipeline.Web.Features.BrightData;
using EventPipeline.Web.Features.Jobs;
using Microsoft.Extensions.Logging;

namespace EventPipeline.Tests;

/// <summary>
/// Stub HttpMessageHandler that serves pre-queued responses and records every request.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, string?, Task<HttpResponseMessage>>> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>Bodies captured per request, same order as <see cref="Requests"/>.</summary>
    public List<string?> RequestBodies { get; } = new();

    public void Enqueue(Func<HttpRequestMessage, string?, Task<HttpResponseMessage>> responseFactory)
        => _responses.Enqueue(responseFactory);

    public void Enqueue(HttpStatusCode statusCode, string body)
        => _responses.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        // Capture the body while it is still readable — HttpClient disposes request
        // content once the handler returns.
        var body = request.Content != null
            ? await request.Content.ReadAsStringAsync(cancellationToken)
            : null;
        RequestBodies.Add(body);
        return await _responses.Dequeue()(request, body);
    }
}

/// <summary>
/// Minimal IHttpClientFactory that always hands out a client backed by the same handler.
/// </summary>
public sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler);
}

/// <summary>
/// In-memory IPostRegistryService: seeded URLs are known posts (no LLM), everything
/// else is unknown; stores into itself so a second run reuses what the first stored.
/// The analyze turn is a real semaphore so concurrency tests exercise the serialization.
/// </summary>
public sealed class FakePostRegistryService : IPostRegistryService
{
    private readonly Dictionary<string, PostAnalysisResult> _analyses = new();
    private readonly SemaphoreSlim _analyzeTurn = new(1, 1);

    public int StoreCallCount { get; private set; }

    public List<string> StoredUrls { get; } = new();

    public void Seed(string url, PostAnalysisResult analysis) => _analyses[url] = analysis;

    public Task<Dictionary<string, PostAnalysisResult>> GetAnalysesByUrlAsync(
        IReadOnlyList<string> urls,
        CancellationToken ct = default)
    {
        var hits = new Dictionary<string, PostAnalysisResult>();
        foreach (var url in urls)
        {
            if (_analyses.TryGetValue(url, out var analysis))
                hits[url] = analysis;
        }

        return Task.FromResult(hits);
    }

    public Task RegisterPostsAsync(
        IReadOnlyList<InstagramPost> posts,
        CancellationToken ct = default)
        => Task.CompletedTask;

    public Task StoreAnalysesAsync(
        IReadOnlyList<(string Url, PostAnalysisResult Analysis)> entries,
        CancellationToken ct = default)
    {
        StoreCallCount++;
        foreach (var (url, analysis) in entries)
        {
            _analyses[url] = analysis;
            StoredUrls.Add(url);
        }

        return Task.CompletedTask;
    }

    public Task WaitForAnalyzeTurnAsync(CancellationToken ct = default)
        => _analyzeTurn.WaitAsync(ct);

    public void ReleaseAnalyzeTurn()
        => _analyzeTurn.Release();
}

/// <summary>
/// In-memory IDeepSeekAuditService collecting the audit rows the real service persists.
/// </summary>
public sealed class FakeDeepSeekAuditService : IDeepSeekAuditService
{
    public List<DeepSeekCallLog> RecordedLogs { get; } = new();

    public Task RecordAsync(DeepSeekCallLog log, CancellationToken ct = default)
    {
        RecordedLogs.Add(log);
        return Task.CompletedTask;
    }
}

/// <summary>
/// DeepSeekService with the retry backoff replaced by a no-op so tests run instantly.
/// </summary>
public sealed class TestableDeepSeekService : DeepSeekService
{
    public TestableDeepSeekService(
        IHttpClientFactory httpClientFactory,
        ILogger<DeepSeekService> logger,
        FakeDeepSeekAuditService audit)
        : base(httpClientFactory, logger, audit)
    {
        Audit = audit;
    }

    /// <summary>The audit rows recorded by this service.</summary>
    public FakeDeepSeekAuditService Audit { get; }

    protected override Task DelayBetweenRetriesAsync(int attempt, CancellationToken ct)
        => Task.CompletedTask;
}

/// <summary>
/// IDeepSeekService fake driven by a delegate, so each test defines its own analysis results.
/// </summary>
public sealed class FakeDeepSeekService : IDeepSeekService
{
    private readonly Func<List<InstagramPost>, string, List<PostAnalysisResult>> _analyze;
    private readonly Func<List<CleanupEventItem>, string, List<DuplicateGroupResult>>? _findDuplicates;
    private readonly Func<List<CleanupEventItem>, List<CleanupEventItem>, List<DuplicateGroupResult>>? _findDuplicateCandidates;
    private readonly Func<List<CleanupEventItem>, List<MuxoEventItem>, List<CrossMatchResult>>? _findCrossMatches;

    public FakeDeepSeekService(
        Func<List<InstagramPost>, string, List<PostAnalysisResult>> analyze,
        Func<List<CleanupEventItem>, string, List<DuplicateGroupResult>>? findDuplicates = null,
        Func<List<CleanupEventItem>, List<CleanupEventItem>, List<DuplicateGroupResult>>? findDuplicateCandidates = null,
        Func<List<CleanupEventItem>, List<MuxoEventItem>, List<CrossMatchResult>>? findCrossMatches = null)
    {
        _analyze = analyze;
        _findDuplicates = findDuplicates;
        _findDuplicateCandidates = findDuplicateCandidates;
        _findCrossMatches = findCrossMatches;
    }

    public List<string> ReceivedApiKeys { get; } = new();

    public DateRange? LastDateRange { get; private set; }

    public List<CleanupEventItem>? LastCleanupEvents { get; private set; }

    public string? LastCleanupMonthLabel { get; private set; }

    public List<CleanupEventItem>? LastDuplicateCandidates { get; private set; }

    public List<CleanupEventItem>? LastDuplicateExistingEvents { get; private set; }

    public List<MuxoEventItem>? LastCrossMatchMuxoEvents { get; private set; }

    public Task<List<PostAnalysisResult>> AnalyzePostsAsync(
        List<InstagramPost> posts,
        string apiKey,
        DateRange? dateRange = null,
        CancellationToken ct = default)
    {
        ReceivedApiKeys.Add(apiKey);
        LastDateRange = dateRange;
        return Task.FromResult(_analyze(posts, apiKey));
    }

    public Task<List<DuplicateGroupResult>> FindDuplicateEventsAsync(
        List<CleanupEventItem> events,
        string monthLabel,
        string apiKey,
        CancellationToken ct = default)
    {
        LastCleanupEvents = events;
        LastCleanupMonthLabel = monthLabel;
        ReceivedApiKeys.Add(apiKey);
        return Task.FromResult(_findDuplicates != null
            ? _findDuplicates(events, monthLabel)
            : throw new InvalidOperationException("No findDuplicates delegate configured."));
    }

    public Task<List<DuplicateGroupResult>> FindDuplicateCandidatesAsync(
        List<CleanupEventItem> candidates,
        List<CleanupEventItem> existingEvents,
        string apiKey,
        CancellationToken ct = default)
    {
        LastDuplicateCandidates = candidates;
        LastDuplicateExistingEvents = existingEvents;
        ReceivedApiKeys.Add(apiKey);
        return Task.FromResult(_findDuplicateCandidates != null
            ? _findDuplicateCandidates(candidates, existingEvents)
            : throw new InvalidOperationException("No findDuplicateCandidates delegate configured."));
    }

    public Task<List<CrossMatchResult>> FindCrossMatchesAsync(
        List<CleanupEventItem> ourEvents,
        List<MuxoEventItem> muxoEvents,
        string apiKey,
        CancellationToken ct = default)
    {
        LastCrossMatchMuxoEvents = muxoEvents;
        ReceivedApiKeys.Add(apiKey);
        return Task.FromResult(_findCrossMatches != null
            ? _findCrossMatches(ourEvents, muxoEvents)
            : throw new InvalidOperationException("No findCrossMatches delegate configured."));
    }
}

/// <summary>
/// IMuxoScraperService fake driven by a delegate.
/// </summary>
public sealed class FakeMuxoScraperService : IMuxoScraperService
{
    private readonly Func<int, List<MuxoEvent>> _scrape;

    public FakeMuxoScraperService(Func<int, List<MuxoEvent>> scrape)
        => _scrape = scrape;

    public int LastMonthsAhead { get; private set; } = -1;

    public Task<List<MuxoEvent>> ScrapeUpcomingAsync(int monthsAhead, CancellationToken ct = default)
    {
        LastMonthsAhead = monthsAhead;
        return Task.FromResult(_scrape(monthsAhead));
    }
}
public static class TestData
{
    public static InstagramPost CreatePost(
        string postId,
        string caption = "",
        string account = "test.account",
        DateTime? datetime = null)
        => new()
        {
            Account = account,
            PostId = postId,
            Caption = caption,
            Datetime = datetime ?? new DateTime(2026, 9, 19, 20, 0, 0, DateTimeKind.Utc),
            Url = $"https://instagram.com/p/{postId}",
            ImageUrl = $"https://cdn.example.com/{postId}.jpg"
        };

    public static PostAnalysisResult EventResult(
        string title = "Concierto de prueba",
        string? eventDate = "2026-09-19T22:00:00",
        string? dateDescription = "Sábado 19 de septiembre",
        string? summary = "Gran concierto en la sala principal.",
        string? eventTime = null)
        => new()
        {
            IsEvent = true,
            Title = title,
            EventDate = eventDate,
            EventTime = eventTime,
            EventDateDescription = dateDescription,
            Summary = summary
        };

    public static PostAnalysisResult NonEventResult()
        => new() { IsEvent = false };

    /// <summary>
    /// Weekly recurrence result, e.g. "todos los jueves" (days [4]) starting 2026-09-10.
    /// </summary>
    public static PostAnalysisResult WeeklyResult(
        List<int> daysOfWeek,
        string start,
        string? end = null,
        string title = "Evento semanal")
        => new()
        {
            IsEvent = true,
            Title = title,
            EventDate = start,
            EventDateDescription = "Todos los jueves",
            Summary = "Evento semanal recurrente",
            IsRecurrent = true,
            RecurrenceType = "weekly",
            RecurrenceDaysOfWeek = daysOfWeek,
            RecurrenceStartDate = start,
            RecurrenceEndDate = end
        };

    /// <summary>
    /// Multi-day / daily result, e.g. a fair running from start to end.
    /// </summary>
    public static PostAnalysisResult DailyRangeResult(string start, string end, string title = "Feria")
        => new()
        {
            IsEvent = true,
            Title = title,
            EventDate = start,
            EventDateDescription = $"Del {start} al {end}",
            Summary = "Feria de varios días",
            IsRecurrent = true,
            RecurrenceType = "daily",
            RecurrenceDaysOfWeek = null,
            RecurrenceStartDate = start,
            RecurrenceEndDate = end
        };

    /// <summary>
    /// Serializes the envelope DeepSeek actually returns: choices[0].message.content holds
    /// the JSON string {"results": [...]} that the service parses into BatchAnalysisResult.
    /// </summary>
    public static string BuildDeepSeekResponse(
        List<PostAnalysisResult> results,
        string finishReason = "stop",
        DeepSeekUsage? usage = null)
    {
        var content = JsonSerializer.Serialize(new BatchAnalysisResult { Results = results });
        var envelope = new DeepSeekResponse
        {
            Choices = new List<DeepSeekChoice>
            {
                new()
                {
                    Message = new DeepSeekChoiceMessage { Content = content },
                    FinishReason = finishReason
                }
            },
            Usage = usage
        };
        return JsonSerializer.Serialize(envelope);
    }

    /// <summary>
    /// Serializes the DeepSeek envelope whose content holds the duplicate cleanup JSON
    /// {"duplicate_groups": [...]} that FindDuplicateEventsAsync parses.
    /// </summary>
    public static string BuildCleanupDeepSeekResponse(List<DuplicateGroupResult> groups)
    {
        var content = JsonSerializer.Serialize(new DuplicateCleanupResult { DuplicateGroups = groups });
        var envelope = new DeepSeekResponse
        {
            Choices = new List<DeepSeekChoice>
            {
                new()
                {
                    Message = new DeepSeekChoiceMessage { Content = content },
                    FinishReason = "stop"
                }
            }
        };
        return JsonSerializer.Serialize(envelope);
    }

    /// <summary>A persisted event record for service-level tests.</summary>
    public static EventRecord CreateRecord(
        string eventUniqueId,
        string title,
        string account = "test.account",
        string postId = "p-1",
        DateTime? eventDate = null,
        DateTime? recurrenceStart = null,
        DateTime? recurrenceEnd = null,
        string? recurrenceDaysOfWeek = null,
        string? recurrenceType = null,
        string? url = null)
        => new()
        {
            EventUniqueId = eventUniqueId,
            Title = title,
            Summary = $"Resumen de {title}",
            Account = account,
            PostId = postId,
            EventDate = eventDate,
            RecurrenceStartDate = recurrenceStart,
            RecurrenceEndDate = recurrenceEnd,
            RecurrenceDaysOfWeek = recurrenceDaysOfWeek,
            RecurrenceType = recurrenceType,
            IsRecurrent = recurrenceType != null,
            Url = url ?? $"https://instagram.com/p/{postId}",
            CreatedAt = DateTime.UtcNow
        };

    /// <summary>A muxojaleo event for service-level tests.</summary>
    public static MuxoEvent CreateMuxoEvent(
        string externalId,
        string title,
        DateTime? date = null,
        string? venue = null,
        string? link = null,
        string? categories = null)
        => new()
        {
            ExternalId = externalId,
            Title = title,
            Date = date,
            Venue = venue,
            Link = link,
            Categories = categories,
            CreatedAt = DateTime.UtcNow
        };

    /// <summary>
    /// Serializes the DeepSeek envelope whose content holds the cross-match JSON
    /// {"matches": [...]} that FindCrossMatchesAsync parses.
    /// </summary>
    public static string BuildCrossMatchDeepSeekResponse(List<CrossMatchResult> matches)
    {
        var content = JsonSerializer.Serialize(new CrossMatchBatchResult { Matches = matches });
        var envelope = new DeepSeekResponse
        {
            Choices = new List<DeepSeekChoice>
            {
                new()
                {
                    Message = new DeepSeekChoiceMessage { Content = content },
                    FinishReason = "stop"
                }
            }
        };
        return JsonSerializer.Serialize(envelope);
    }
}

// --- Fakes for the feature services (controller tests) ---

public sealed class FakeRecognitionService : IRecognitionService
{
    private readonly Func<List<InstagramPost>, string, CancellationToken, Task<RecognitionResponse>>? _recognize;

    public FakeRecognitionService(Func<List<InstagramPost>, string, CancellationToken, Task<RecognitionResponse>>? recognize = null)
        => _recognize = recognize;

    public DateRange? LastDateRange { get; private set; }

    public List<string> ReceivedApiKeys { get; } = new();

    public Task<RecognitionResponse> RecognizeAsync(
        List<InstagramPost> posts, string deepSeekApiKey, DateRange? dateRange = null, CancellationToken ct = default)
    {
        LastDateRange = dateRange;
        ReceivedApiKeys.Add(deepSeekApiKey);
        return _recognize != null
            ? _recognize(posts, deepSeekApiKey, ct)
            : throw new InvalidOperationException("No recognize delegate configured.");
    }
}

public sealed class FakeEventQueryService : IEventQueryService
{
    private readonly Func<CancellationToken, Task<List<EventDetailResponse>>>? _getAll;
    private readonly Func<string, CancellationToken, Task<EventDetailResponse?>>? _getById;

    public FakeEventQueryService(
        Func<CancellationToken, Task<List<EventDetailResponse>>>? getAll = null,
        Func<string, CancellationToken, Task<EventDetailResponse?>>? getById = null)
    {
        _getAll = getAll;
        _getById = getById;
    }

    public DateRange? LastRange { get; private set; }

    public Task<EventDetailResponse?> GetByUniqueIdAsync(string eventUniqueId, CancellationToken ct = default)
        => _getById != null
            ? _getById(eventUniqueId, ct)
            : Task.FromResult<EventDetailResponse?>(null);

    public Task<List<EventDetailResponse>> GetAllAsync(
        DateRange? dateRange = null,
        bool includeExcluded = false,
        CancellationToken ct = default)
    {
        LastRange = dateRange;
        return _getAll != null
            ? _getAll(ct)
            : Task.FromResult(new List<EventDetailResponse>());
    }
}

public sealed class FakeEventCrudService : IEventCrudService
{
    private readonly Func<string, UpdateEventRequest, CancellationToken, Task<EventDetailResponse?>>? _update;
    private readonly Func<string, CancellationToken, Task<bool>>? _delete;

    public FakeEventCrudService(
        Func<string, UpdateEventRequest, CancellationToken, Task<EventDetailResponse?>>? update = null,
        Func<string, CancellationToken, Task<bool>>? delete = null)
    {
        _update = update;
        _delete = delete;
    }

    public Task<EventDetailResponse?> UpdateAsync(string eventUniqueId, UpdateEventRequest request, CancellationToken ct = default)
        => _update != null
            ? _update(eventUniqueId, request, ct)
            : throw new InvalidOperationException("No update delegate configured.");

    public Task<EventDetailResponse?> SetExcludedAsync(string eventUniqueId, bool excluded, CancellationToken ct = default)
        => Task.FromResult<EventDetailResponse?>(null);

    public Task<bool> DeleteAsync(string eventUniqueId, CancellationToken ct = default)
        => _delete != null
            ? _delete(eventUniqueId, ct)
            : throw new InvalidOperationException("No delete delegate configured.");
}

public sealed class FakeCleanupService : ICleanupService
{
    private readonly Func<int, int, string, CancellationToken, Task<CleanupResponse>>? _cleanup;

    public FakeCleanupService(Func<int, int, string, CancellationToken, Task<CleanupResponse>>? cleanup = null)
        => _cleanup = cleanup;

    public int LastYear { get; private set; }

    public int LastMonth { get; private set; }

    public Task<CleanupResponse> CleanupMonthAsync(int year, int month, string deepSeekApiKey, CancellationToken ct = default)
    {
        LastYear = year;
        LastMonth = month;
        return _cleanup != null
            ? _cleanup(year, month, deepSeekApiKey, ct)
            : throw new InvalidOperationException("No cleanup delegate configured.");
    }
}

public sealed class FakeMuxoSyncService : IMuxoSyncService
{
    private readonly Func<int, CancellationToken, Task<MuxoSyncResponse>>? _sync;

    public FakeMuxoSyncService(Func<int, CancellationToken, Task<MuxoSyncResponse>>? sync = null)
        => _sync = sync;

    public int LastMonthsAhead { get; private set; }

    public Task<MuxoSyncResponse> SyncAsync(int monthsAhead = 2, CancellationToken ct = default)
    {
        LastMonthsAhead = monthsAhead;
        return _sync != null
            ? _sync(monthsAhead, ct)
            : throw new InvalidOperationException("No sync delegate configured.");
    }
}

public sealed class FakeCrossCheckService : ICrossCheckService
{
    private readonly Func<string, CancellationToken, Task<CrossCheckResponse>>? _crossCheck;

    public FakeCrossCheckService(Func<string, CancellationToken, Task<CrossCheckResponse>>? crossCheck = null)
        => _crossCheck = crossCheck;

    public Task<CrossCheckResponse> CrossCheckAsync(string deepSeekApiKey, CancellationToken ct = default)
        => _crossCheck != null
            ? _crossCheck(deepSeekApiKey, ct)
            : throw new InvalidOperationException("No crosscheck delegate configured.");
}

public sealed class FakeMuxoQueryService : IMuxoQueryService
{
    private readonly Func<CancellationToken, Task<List<MuxoEventDto>>>? _getAll;

    public FakeMuxoQueryService(Func<CancellationToken, Task<List<MuxoEventDto>>>? getAll = null)
        => _getAll = getAll;

    public Task<List<MuxoEventDto>> GetAllAsync(CancellationToken ct = default)
        => _getAll != null
            ? _getAll(ct)
            : Task.FromResult(new List<MuxoEventDto>());
}

// --- Fakes for the jobs (Phase 5) ---

/// <summary>In-memory IJobActivityHub collecting the emitted lines.</summary>
public sealed class FakeJobActivityHub : IJobActivityHub
{
    public List<JobLogEntry> Entries { get; } = new();

    public List<JobRunEvent> RunEvents { get; } = new();

    public Task LogAsync(int jobRunId, string jobName, string message, CancellationToken ct = default)
    {
        Entries.Add(new JobLogEntry(jobRunId, jobName, DateTime.UtcNow, message));
        return Task.CompletedTask;
    }

    public Task PublishRunAsync(int jobRunId, string jobName, string status, CancellationToken ct = default)
    {
        RunEvents.Add(new JobRunEvent(jobRunId, jobName, DateTime.UtcNow, status));
        return Task.CompletedTask;
    }

    public (Guid Id, System.Threading.Channels.ChannelReader<JobActivity> Reader) Subscribe()
        => throw new NotImplementedException();

    public void Unsubscribe(Guid id)
    {
    }
}

/// <summary>In-memory ISettingsService for job tests.</summary>
public sealed class FakeSettingsService : ISettingsService
{
    private readonly Dictionary<string, string?> _values = new();

    public FakeSettingsService(IEnumerable<(string Key, string? Value)>? initial = null)
    {
        if (initial != null)
        {
            foreach (var (key, value) in initial)
                _values[key] = value;
        }
    }

    public string? this[string key]
    {
        get => _values.TryGetValue(key, out var value) ? value : null;
        set => _values[key] = value;
    }

    public Task<string?> GetSecretAsync(string key, CancellationToken ct = default)
        => Task.FromResult(this[key]);

    public Task SetSecretAsync(string key, string value, CancellationToken ct = default)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task<string?> GetPlainAsync(string key, CancellationToken ct = default)
        => Task.FromResult(this[key]);

    public Task SetPlainAsync(string key, string value, CancellationToken ct = default)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }
}

/// <summary>In-memory IIgAccountService for job tests.</summary>
public sealed class FakeIgAccountService : IIgAccountService
{
    private readonly List<IgAccount> _accounts;

    public FakeIgAccountService(IEnumerable<IgAccount>? accounts = null)
        => _accounts = (accounts ?? new List<IgAccount>()).ToList();

    public Task<List<IgAccount>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult(_accounts.ToList());

    public Task<List<IgAccount>> GetEnabledAsync(CancellationToken ct = default)
        => Task.FromResult(_accounts.Where(a => a.Enabled).ToList());

    public Task<(bool Success, string? Error)> AddAsync(string username, string profileUrl, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<(bool Success, string? Error)> UpdateAsync(int id, string username, string profileUrl, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<bool> DeleteAsync(int id, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<bool> SetEnabledAsync(int id, bool enabled, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<int> SetAllEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        foreach (var account in _accounts)
            account.Enabled = enabled;
        return Task.FromResult(_accounts.Count);
    }

    public Task MarkScrapedAsync(IEnumerable<string> usernames, DateTime utcNow, CancellationToken ct = default)
    {
        MarkedScrapedUsernames = usernames.ToList();
        MarkedScrapedAtUtc = utcNow;
        return Task.CompletedTask;
    }

    public List<string>? MarkedScrapedUsernames { get; private set; }

    public DateTime? MarkedScrapedAtUtc { get; private set; }
}

/// <summary>Delegate-driven IBrightDataService for job tests.</summary>
public sealed class FakeBrightDataService : IBrightDataService
{
    private readonly Func<IReadOnlyList<IgAccount>, BrightDataOptions, string, BrightDataFetchResult> _fetch;

    public FakeBrightDataService(
        Func<IReadOnlyList<IgAccount>, BrightDataOptions, string, BrightDataFetchResult> fetch)
        => _fetch = fetch;

    public IReadOnlyList<IgAccount>? LastAccounts { get; private set; }

    public BrightDataOptions? LastOptions { get; private set; }

    public Task<BrightDataFetchResult> FetchRecentPostsAsync(
        IReadOnlyList<IgAccount> accounts, BrightDataOptions options, string token, CancellationToken ct = default)
    {
        LastAccounts = accounts;
        LastOptions = options;
        return Task.FromResult(_fetch(accounts, options, token));
    }
}
