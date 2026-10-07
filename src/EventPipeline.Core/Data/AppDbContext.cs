using EventPipeline.Core.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace EventPipeline.Core.Data;

public class AppDbContext : DbContext, IDataProtectionKeyContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<EventRecord> EventRecords => Set<EventRecord>();

    public DbSet<MuxoEvent> MuxoEvents => Set<MuxoEvent>();

    public DbSet<CrossMatch> CrossMatches => Set<CrossMatch>();

    public DbSet<DeepSeekCallLog> DeepSeekCallLogs => Set<DeepSeekCallLog>();

    public DbSet<PostRecord> Posts => Set<PostRecord>();

    // App-only tables (jobs, settings, accounts, users).
    public DbSet<IgAccount> IgAccounts => Set<IgAccount>();

    public DbSet<AppUser> AppUsers => Set<AppUser>();

    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    public DbSet<JobRun> JobRuns => Set<JobRun>();

    public DbSet<JobRunLog> JobRunLogs => Set<JobRunLog>();

    // Data Protection key ring, persisted in the shared database so container
    // recreation does not lose the keys that encrypt the stored secrets.
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EventRecord>(entity =>
        {
            entity.HasIndex(e => e.EventUniqueId)
                  .IsUnique();

            entity.HasIndex(e => e.PostId)
                  .IsUnique();

            entity.HasIndex(e => e.Account);

            entity.HasIndex(e => e.EventDate);

            entity.HasIndex(e => e.CreatedAt);
        });

        modelBuilder.Entity<MuxoEvent>(entity =>
        {
            entity.HasIndex(e => e.ExternalId)
                  .IsUnique();
        });

        modelBuilder.Entity<CrossMatch>(entity =>
        {
            entity.HasIndex(m => m.EventUniqueId)
                  .IsUnique();

            entity.HasIndex(m => m.MuxoEventId)
                  .IsUnique();

            entity.HasOne(m => m.MuxoEvent)
                  .WithMany()
                  .HasForeignKey(m => m.MuxoEventId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeepSeekCallLog>(entity =>
        {
            entity.HasIndex(e => e.StartedAtUtc);

            entity.HasIndex(e => e.Operation);
        });

        modelBuilder.Entity<PostRecord>(entity =>
        {
            entity.HasIndex(e => e.Url)
                  .IsUnique();
        });

        modelBuilder.Entity<DataProtectionKey>(entity =>
        {
            entity.ToTable("DataProtectionKeys");
            entity.HasKey(k => k.Id);
        });

        modelBuilder.Entity<IgAccount>(entity =>
        {
            entity.HasIndex(a => a.Username)
                  .IsUnique();
        });

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.HasIndex(u => u.Username)
                  .IsUnique();
        });

        modelBuilder.Entity<JobRun>(entity =>
        {
            entity.HasIndex(j => new { j.JobName, j.StartedAtUtc });
        });

        modelBuilder.Entity<JobRunLog>(entity =>
        {
            entity.HasIndex(l => l.JobRunId);
            entity.HasIndex(l => l.LoggedAtUtc);
        });
    }
}
