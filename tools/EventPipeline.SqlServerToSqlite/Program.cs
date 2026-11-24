// Copies every EF-owned table of an existing SQL Server EventPipelineDb into a new
// SQLite database, keeping the primary keys (so foreign keys and the Data
// Protection key ring stay valid). The Hangfire schema is not copied: the Web uses
// in-memory Hangfire storage and re-registers the recurring jobs at startup.
//
// Usage:
//   dotnet run --project tools/EventPipeline.SqlServerToSqlite -- \
//     "<SQL Server connection string>" "Data Source=/data/eventpipeline.db"
//
// The target is created with the EF migrations and must be empty. The source is
// only read. Stop the Web and the Api before running it, so nothing is written to
// the source while it is being copied.

using EventPipeline.Core.Data;
using Microsoft.EntityFrameworkCore;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: <sqlserver-connection-string> <sqlite-connection-string>");
    return 2;
}

var sourceOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(args[0]).Options;
var targetOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(args[1]).Options;

await using var source = new AppDbContext(sourceOptions);
await using var target = new AppDbContext(targetOptions);

await target.Database.MigrateAsync();

// Parents before children (CrossMatches -> MuxoEvents is the only foreign key).
var steps = new List<(string Table, Func<Task<int>> Copy, Func<Task<(int Source, int Target)>> Count)>
{
    Step(db => db.IgAccounts),
    Step(db => db.AppUsers),
    Step(db => db.AppSettings),
    Step(db => db.DataProtectionKeys),
    Step(db => db.Posts),
    Step(db => db.EventRecords),
    Step(db => db.MuxoEvents),
    Step(db => db.CrossMatches),
    Step(db => db.DeepSeekCallLogs),
    Step(db => db.JobRuns),
    Step(db => db.JobRunLogs),
};

foreach (var step in steps)
{
    var (_, existing) = await step.Count();
    if (existing != 0)
    {
        Console.Error.WriteLine($"Target table {step.Table} is not empty ({existing} rows). Nothing copied.");
        return 1;
    }
}

await using (var tx = await target.Database.BeginTransactionAsync())
{
    foreach (var step in steps)
    {
        var copied = await step.Copy();
        Console.WriteLine($"{step.Table,-20} {copied,6} rows");
    }
    await tx.CommitAsync();
}

var ok = true;
foreach (var step in steps)
{
    var (src, dst) = await step.Count();
    if (src != dst)
    {
        Console.Error.WriteLine($"Row count mismatch in {step.Table}: source {src}, target {dst}");
        ok = false;
    }
}

Console.WriteLine(ok ? "Copy verified: row counts match." : "Copy FAILED verification.");
return ok ? 0 : 1;

(string, Func<Task<int>>, Func<Task<(int, int)>>) Step<T>(Func<AppDbContext, DbSet<T>> set) where T : class
{
    var table = target.Model.FindEntityType(typeof(T))!.GetTableName()!;
    return (
        table,
        async () =>
        {
            var rows = await set(source).AsNoTracking().ToListAsync();
            set(target).AddRange(rows);
            await target.SaveChangesAsync();
            target.ChangeTracker.Clear();
            return rows.Count;
        },
        async () => (await set(source).CountAsync(), await set(target).CountAsync()));
}
