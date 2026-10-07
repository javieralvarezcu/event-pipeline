using EventPipeline.Core.Data;
using EventPipeline.Web.Features.Jobs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventPipeline.Tests;

public class JobActivityHubTests
{
    private static (JobActivityHub Hub, SqliteConnection Connection) CreateHub()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(connection));
        var provider = services.BuildServiceProvider();

        // Esquema del modelo sobre la BD en memoria (mismo flujo que TestDatabase).
        using (var db = provider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        // El hub crea su propio scope por línea y resuelve la factory desde ahí.
        var hub = new JobActivityHub(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<JobActivityHub>.Instance);
        return (hub, connection);
    }

    [Fact]
    public async Task Log_PersistsTheLine()
    {
        var (hub, connection) = CreateHub();
        using (connection)
        {
            await hub.LogAsync(42, "IgScrape", "Scrapeando 3 cuentas…");

            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            await using var db = new AppDbContext(options);
            var line = await db.JobRunLogs.SingleAsync();
            Assert.Equal(42, line.JobRunId);
            Assert.Equal("IgScrape", line.JobName);
            Assert.Equal("Scrapeando 3 cuentas…", line.Message);
        }
    }

    [Fact]
    public async Task Log_BroadcastsToSubscribersUntilUnsubscribed()
    {
        var (hub, connection) = CreateHub();
        using (connection)
        {
            var (id, reader) = hub.Subscribe();

            await hub.LogAsync(1, "Muxo", "primera línea");
            var first = Assert.IsType<JobLogEntry>(await reader.ReadAsync());
            Assert.Equal("primera línea", first.Message);

            hub.Unsubscribe(id);
            await hub.LogAsync(1, "Muxo", "segunda línea (sin suscriptor)");

            Assert.False(await reader.WaitToReadAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(200)));
        }
    }

    [Fact]
    public async Task PublishRun_BroadcastsStatusChanges()
    {
        var (hub, connection) = CreateHub();
        using (connection)
        {
            var (id, reader) = hub.Subscribe();

            await hub.PublishRunAsync(7, "IgScrape", "Succeeded");

            var evt = Assert.IsType<JobRunEvent>(await reader.ReadAsync());
            Assert.Equal(7, evt.JobRunId);
            Assert.Equal("IgScrape", evt.JobName);
            Assert.Equal("Succeeded", evt.Status);

            hub.Unsubscribe(id);
        }
    }

    [Fact]
    public async Task Log_DoesNotThrowWhenTheDatabaseIsGone()
    {
        var (hub, connection) = CreateHub();
        connection.Dispose(); // BD inaccesible: el log en vivo debe sobrevivir

        await hub.LogAsync(1, "Cleanup", "línea sin BD");
    }
}
