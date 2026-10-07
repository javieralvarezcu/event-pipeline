using EventPipeline.Core.Contracts;
using EventPipeline.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace EventPipeline.Core.Features.Muxo;

public interface IMuxoQueryService
{
    /// <summary>All persisted muxojaleo.com events, with whether each one is already crossed.</summary>
    Task<List<MuxoEventDto>> GetAllAsync(CancellationToken ct = default);
}

public class MuxoQueryService : IMuxoQueryService
{
    private readonly AppDbContext _dbContext;

    public MuxoQueryService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<MuxoEventDto>> GetAllAsync(CancellationToken ct = default)
    {
        var muxoEvents = await _dbContext.MuxoEvents
            .AsNoTracking()
            .OrderBy(m => m.Date)
            .ToListAsync(ct);

        var matchedMuxoIds = (await _dbContext.CrossMatches
            .AsNoTracking()
            .Select(m => m.MuxoEventId)
            .ToListAsync(ct))
            .ToHashSet();

        return muxoEvents.Select(m => new MuxoEventDto
        {
            ExternalId = m.ExternalId,
            Title = m.Title,
            Date = m.Date,
            Venue = m.Venue,
            Link = m.Link,
            Categories = m.Categories,
            Price = m.Price,
            IsCrossed = matchedMuxoIds.Contains(m.Id)
        }).ToList();
    }
}
