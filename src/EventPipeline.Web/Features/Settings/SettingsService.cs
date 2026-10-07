using EventPipeline.Core.Data;
using EventPipeline.Core.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace EventPipeline.Web.Features.Settings;

/// <summary>
/// Key/value settings persisted in the AppSettings table. Secret values (DeepSeek
/// API key, Bright Data token) are encrypted at rest with Data Protection; the key
/// ring lives in the shared database (DataProtectionKeys), so container recreation
/// does not invalidate the stored secrets. Values are never logged.
/// </summary>
public class SettingsService : ISettingsService
{
    private const string ProtectorPurpose = "EventPipeline.AppSettings.v1";

    private readonly AppDbContext _db;
    private readonly IDataProtector _protector;

    public SettingsService(AppDbContext db, IDataProtectionProvider protectionProvider)
    {
        _db = db;
        _protector = protectionProvider.CreateProtector(ProtectorPurpose);
    }

    public async Task<string?> GetSecretAsync(string key, CancellationToken ct = default)
    {
        var row = await _db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row == null || !row.IsSecret || string.IsNullOrEmpty(row.Value))
            return null;

        return _protector.Unprotect(row.Value);
    }

    public async Task SetSecretAsync(string key, string value, CancellationToken ct = default)
    {
        await SetAsync(key, value, isSecret: true, ct);
    }

    public async Task<string?> GetPlainAsync(string key, CancellationToken ct = default)
    {
        var row = await _db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == key, ct);
        return row is { IsSecret: false } ? row.Value : null;
    }

    public async Task SetPlainAsync(string key, string value, CancellationToken ct = default)
    {
        await SetAsync(key, value, isSecret: false, ct);
    }

    private async Task SetAsync(string key, string value, bool isSecret, CancellationToken ct)
    {
        var row = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);

        if (string.IsNullOrWhiteSpace(value))
        {
            if (row != null)
                _db.AppSettings.Remove(row);
            await _db.SaveChangesAsync(ct);
            return;
        }

        var stored = isSecret ? _protector.Protect(value.Trim()) : value.Trim();
        if (row == null)
        {
            _db.AppSettings.Add(new AppSetting { Key = key, Value = stored, IsSecret = isSecret });
        }
        else
        {
            row.Value = stored;
            row.IsSecret = isSecret;
            row.UpdatedAtUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
    }
}
