using EventPipeline.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EventPipeline.Tests;

/// <summary>
/// Real SQLite in-memory database for service-level tests (same approach as the
/// legacy test suite): one open connection shared by the contexts created over it.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public AppDbContext Db { get; }

    public TestDatabase(Func<DbContextOptions<AppDbContext>, AppDbContext>? contextFactory = null)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        Db = contextFactory?.Invoke(options) ?? new AppDbContext(options);
        Db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}

/// <summary>
/// AppDbContext whose first SaveChangesAsync throws, simulating the race where a
/// concurrent request inserts the same PostId between the duplicate check and the save.
/// </summary>
public sealed class ThrowingOnceDbContext : AppDbContext
{
    public int SaveCalls { get; private set; }

    public ThrowingOnceDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCalls++;
        if (SaveCalls == 1)
            throw new DbUpdateException("Simulated unique constraint violation.", (Exception?)null);
        return base.SaveChangesAsync(cancellationToken);
    }
}
