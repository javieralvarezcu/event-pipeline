using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EventPipeline.Core.Entities;

/// <summary>
/// One log line emitted by a running job (user-facing progress), persisted so the
/// log of a run survives reloads and is shown live on the Dashboard.
/// </summary>
public class JobRunLog
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Run this line belongs to (no FK: logs outlive purged runs until their own purge).</summary>
    public int JobRunId { get; set; }

    [Required]
    [MaxLength(100)]
    public string JobName { get; set; } = string.Empty;

    public DateTime LoggedAtUtc { get; set; } = DateTime.UtcNow;

    [Required]
    [MaxLength(500)]
    public string Message { get; set; } = string.Empty;
}
