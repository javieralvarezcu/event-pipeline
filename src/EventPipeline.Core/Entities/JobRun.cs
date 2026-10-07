using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EventPipeline.Core.Entities;

/// <summary>
/// Run history of the scheduled jobs (IG scrape, muxo sync+crosscheck, monthly
/// cleanup), written by the jobs themselves and shown in the Jobs page.
/// </summary>
public class JobRun
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string JobName { get; set; } = string.Empty;

    /// <summary>"recurring" (cron) or "manual" (run-now button).</summary>
    [Required]
    [MaxLength(20)]
    public string TriggerType { get; set; } = string.Empty;

    /// <summary>"Running", "Succeeded" or "Failed".</summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = string.Empty;

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>Serialized result summary (job-specific shape), shown in the UI.</summary>
    public string? SummaryJson { get; set; }

    [MaxLength(4000)]
    public string? Error { get; set; }
}
