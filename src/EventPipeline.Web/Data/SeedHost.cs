using System.Security.Cryptography;
using EventPipeline.Core.Data;
using EventPipeline.Core.Entities;
using EventPipeline.Web.Features.Auth;
using EventPipeline.Web.Features.Settings;
using Microsoft.EntityFrameworkCore;

namespace EventPipeline.Web.Data;

/// <summary>
/// Idempotent startup seed: Instagram accounts, the initial administrator and the
/// default settings. Runs once at boot; every step only inserts what is missing, so
/// existing data (or user edits) are never overwritten. Fails fast when the
/// AddWebAppTables migration has not been applied to the database yet.
/// </summary>
public static class SeedHost
{
    /// <summary>
    /// The 34 unique Instagram profiles the n8n workflow scraped (one of its 35
    /// entries was a duplicate). Kept as the default account list; the UI owns
    /// them from now on.
    /// </summary>
    private static readonly string[] DefaultIgUsernames =
    {
        "juevescong", "salaimpala", "saturno___club", "sabotage_night",
        "salam100cordoba", "fueledbysalmorejo", "cordobichea", "limbocordoba",
        "c3a_andalucia", "juventud_cordoba", "tard.esdeneon", "ambiguaxerquia",
        "azabachecolectivo", "t4eventsofficial", "basilisk_collective",
        "verbenakolektiv", "colectivo_azuda", "la_vecina_escondida",
        "casa__escondida", "jazzcafecordoba", "lucianacenteno_",
        "librerialaromantica", "crashcomics", "circuloculturaljuan23",
        "ostinmacho", "wadubkibir.soundsystem", "el_patiovintage", "cigarrafilms",
        "bassicoclub", "cordoba.poetryslam", "objetodardo",
        "elmundoyafricatrabajan", "lanormalcordoba", "battlegamersbar_"
    };

    public static async Task EnsureSeededAsync(IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

            await SeedIgAccountsAsync(db, ct);
            await SeedAdminUserAsync(db, configuration, logger, ct);
            await SeedSettingsAsync(db, ct);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Seed de arranque fallido. Si no se puede conectar a la BD: ¿está Docker Desktop arrancado " +
                "con el SQL de desarrollo (docker compose -f docker-compose.dev.yml up -d)? Si el error " +
                "indica tablas inexistentes, comprueba que Database:MigrateOnStartup está activo en la Web.");
            throw;
        }
    }

    private static async Task SeedIgAccountsAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.IgAccounts.AnyAsync(ct))
            return;

        db.IgAccounts.AddRange(DefaultIgUsernames.Select(username => new IgAccount
        {
            Username = username,
            ProfileUrl = $"https://www.instagram.com/{username}/",
            Enabled = true
        }));

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedAdminUserAsync(
        AppDbContext db, IConfiguration configuration, ILogger logger, CancellationToken ct)
    {
        if (await db.AppUsers.AnyAsync(ct))
            return;

        var username = configuration["Auth:AdminUsername"] ?? "admin";
        var password = configuration["Auth:InitialAdminPassword"];

        if (string.IsNullOrWhiteSpace(password))
        {
            // Development-only fallback: log the generated password exactly once so the
            // operator can log in; production compose enforces INITIAL_ADMIN_PASSWORD.
            password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12));
            logger.LogWarning(
                "Auth:InitialAdminPassword no configurada. Contraseña inicial generada para el usuario " +
                "'{Username}': {Password} (cámbiala tras el primer login)", username, password);
        }

        db.AppUsers.Add(new AppUser
        {
            Username = username,
            PasswordHash = PasswordHasher.Hash(password),
            MustChangePassword = true
        });

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedSettingsAsync(AppDbContext db, CancellationToken ct)
    {
        var defaults = new (string Key, string Value, bool IsSecret)[]
        {
            (SettingsKeys.BrightDataBaseUrl, SettingsKeys.DefaultBrightDataBaseUrl, false),
            (SettingsKeys.BrightDataDatasetId, SettingsKeys.DefaultBrightDataDatasetId, false),
            (SettingsKeys.IgPostsPerAccount, SettingsKeys.DefaultIgPostsPerAccount, false),
            (SettingsKeys.JobsIgScrapeCron, SettingsKeys.DefaultIgScrapeCron, false),
            (SettingsKeys.JobsMuxoCron, SettingsKeys.DefaultMuxoCron, false),
            (SettingsKeys.JobsCleanupCron, SettingsKeys.DefaultCleanupCron, false),
        };

        var existing = await db.AppSettings.Select(s => s.Key).ToListAsync(ct);
        foreach (var (key, value, isSecret) in defaults)
        {
            if (!existing.Contains(key))
            {
                db.AppSettings.Add(new AppSetting { Key = key, Value = value, IsSecret = isSecret });
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
