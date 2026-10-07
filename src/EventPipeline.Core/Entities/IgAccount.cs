using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EventPipeline.Core.Entities;

/// <summary>
/// Instagram profile scraped through Bright Data to feed the recognition pipeline.
/// Managed from the web UI (Accounts page); the scrape job only touches enabled rows.
/// </summary>
public class IgAccount
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string Username { get; set; } = string.Empty;

    /// <summary>Full profile URL sent to Bright Data (e.g. https://www.instagram.com/juevescong/).</summary>
    [Required]
    [MaxLength(500)]
    public string ProfileUrl { get; set; } = string.Empty;

    /// <summary>Disabled accounts are skipped by the scrape job.</summary>
    public bool Enabled { get; set; } = true;

    public DateTime? LastScrapedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
