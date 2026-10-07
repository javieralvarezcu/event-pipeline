namespace EventPipeline.Core.Features.Posts;

/// <summary>
/// Process-wide analysis turn: concurrent requests serialize the analysis phase so
/// only the first one pays the LLM and the rest find everything already registered.
/// Singleton — it must outlive any request scope.
/// </summary>
public sealed class AnalysisTurn
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public Task WaitAsync(CancellationToken ct = default) => _semaphore.WaitAsync(ct);

    public void Release() => _semaphore.Release();
}
