using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EventPipeline.Core.Entities;

/// <summary>
/// Key/value settings managed from the UI (Settings page). Secret values (DeepSeek
/// API key, Bright Data token) are encrypted at rest with Data Protection; plain
/// values hold non-sensitive configuration such as cron expressions.
/// </summary>
[Table("AppSettings")]
public class AppSetting
{
    [Key]
    [MaxLength(200)]
    public string Key { get; set; } = string.Empty;

    /// <summary>Ciphertext when <see cref="IsSecret"/> is true, plaintext otherwise. Null = not configured.</summary>
    public string? Value { get; set; }

    public bool IsSecret { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
