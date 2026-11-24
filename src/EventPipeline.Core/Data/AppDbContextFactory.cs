using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EventPipeline.Core.Data;

/// <summary>
/// Design-time factory for EF Core migrations CLI (dotnet ef migrations add).
/// Not used at runtime.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlite("Data Source=eventpipeline-design.db");
        return new AppDbContext(optionsBuilder.Options);
    }
}
