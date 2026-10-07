using EventPipeline.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EventPipeline.Tests;

/// <summary>
/// Guards the EF model against accidental schema drift: exact table set and the
/// unique indexes that act as concurrency guardians (dedup of posts, events and
/// cross-matches). The database is exclusively owned by this app — the schema is
/// created by the EF migrations and nothing else writes to it.
/// </summary>
public class SchemaGuardTests
{
    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=localhost;Database=schema-check;TrustServerCertificate=true")
            .Options;
        using var db = new AppDbContext(options);
        return db.Model;
    }

    [Fact]
    public void Model_ExposesTheExpectedTables()
    {
        var tables = BuildModel().GetEntityTypes()
            .Select(e => e.GetTableName())
            .OrderBy(x => x)
            .ToList();

        Assert.Equal(
            new[]
            {
                "AppSettings", "AppUsers", "CrossMatches", "DataProtectionKeys",
                "DeepSeekCallLogs", "EventRecords", "IgAccounts", "JobRunLogs",
                "JobRuns", "MuxoEvents", "Posts"
            }.OrderBy(x => x),
            tables);
    }

    [Fact]
    public void Model_ExposesTheUniqueGuardianIndexes()
    {
        var uniqueIndexes = BuildModel().GetEntityTypes()
            .SelectMany(e => e.GetIndexes().Where(i => i.IsUnique))
            .Select(i => $"{i.DeclaringEntityType.GetTableName()}.{i.GetDatabaseName()}")
            .ToList();

        Assert.Contains("EventRecords.IX_EventRecords_EventUniqueId", uniqueIndexes);
        Assert.Contains("EventRecords.IX_EventRecords_PostId", uniqueIndexes);
        Assert.Contains("MuxoEvents.IX_MuxoEvents_ExternalId", uniqueIndexes);
        Assert.Contains("CrossMatches.IX_CrossMatches_EventUniqueId", uniqueIndexes);
        Assert.Contains("CrossMatches.IX_CrossMatches_MuxoEventId", uniqueIndexes);
        Assert.Contains("Posts.IX_Posts_Url", uniqueIndexes);
        Assert.Contains("IgAccounts.IX_IgAccounts_Username", uniqueIndexes);
        Assert.Contains("AppUsers.IX_AppUsers_Username", uniqueIndexes);
    }
}
