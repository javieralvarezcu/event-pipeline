using EventPipeline.Core.Data;
using EventPipeline.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace EventPipeline.Web.Features.IgAccounts;

public interface IIgAccountService
{
    Task<List<IgAccount>> GetAllAsync(CancellationToken ct = default);

    Task<List<IgAccount>> GetEnabledAsync(CancellationToken ct = default);

    Task<(bool Success, string? Error)> AddAsync(string username, string profileUrl, CancellationToken ct = default);

    Task<(bool Success, string? Error)> UpdateAsync(int id, string username, string profileUrl, CancellationToken ct = default);

    Task<bool> DeleteAsync(int id, CancellationToken ct = default);

    Task<bool> SetEnabledAsync(int id, bool enabled, CancellationToken ct = default);

    /// <summary>Enables or disables every account at once. Returns how many were updated.</summary>
    Task<int> SetAllEnabledAsync(bool enabled, CancellationToken ct = default);

    Task MarkScrapedAsync(IEnumerable<string> usernames, DateTime utcNow, CancellationToken ct = default);
}

/// <summary>
/// CRUD over the Instagram accounts scraped by the IG job. Username is unique;
/// the profile URL is validated as an http(s) URL before saving.
/// </summary>
public class IgAccountService : IIgAccountService
{
    private readonly AppDbContext _db;

    public IgAccountService(AppDbContext db)
    {
        _db = db;
    }

    public Task<List<IgAccount>> GetAllAsync(CancellationToken ct = default)
    {
        return _db.IgAccounts.AsNoTracking()
            .OrderBy(a => a.Username)
            .ToListAsync(ct);
    }

    public Task<List<IgAccount>> GetEnabledAsync(CancellationToken ct = default)
    {
        return _db.IgAccounts.AsNoTracking()
            .Where(a => a.Enabled)
            .OrderBy(a => a.Username)
            .ToListAsync(ct);
    }

    public async Task<(bool Success, string? Error)> AddAsync(
        string username, string profileUrl, CancellationToken ct = default)
    {
        var (valid, error) = Validate(username, profileUrl);
        if (!valid)
            return (false, error);

        if (await _db.IgAccounts.AnyAsync(a => a.Username == username, ct))
            return (false, "Ya existe una cuenta con ese usuario.");

        _db.IgAccounts.Add(new IgAccount
        {
            Username = username,
            ProfileUrl = profileUrl,
            Enabled = true
        });
        await _db.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdateAsync(
        int id, string username, string profileUrl, CancellationToken ct = default)
    {
        var (valid, error) = Validate(username, profileUrl);
        if (!valid)
            return (false, error);

        var account = await _db.IgAccounts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account == null)
            return (false, "Cuenta no encontrada.");

        if (await _db.IgAccounts.AnyAsync(a => a.Username == username && a.Id != id, ct))
            return (false, "Ya existe una cuenta con ese usuario.");

        account.Username = username;
        account.ProfileUrl = profileUrl;
        await _db.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        var account = await _db.IgAccounts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account == null)
            return false;

        _db.IgAccounts.Remove(account);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetEnabledAsync(int id, bool enabled, CancellationToken ct = default)
    {
        var account = await _db.IgAccounts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account == null)
            return false;

        account.Enabled = enabled;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> SetAllEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        return await _db.IgAccounts
            .ExecuteUpdateAsync(a => a.SetProperty(x => x.Enabled, enabled), ct);
    }

    public async Task MarkScrapedAsync(
        IEnumerable<string> usernames, DateTime utcNow, CancellationToken ct = default)
    {
        var list = usernames.ToList();
        if (list.Count == 0)
            return;

        var accounts = await _db.IgAccounts.Where(a => list.Contains(a.Username)).ToListAsync(ct);
        foreach (var account in accounts)
            account.LastScrapedAtUtc = utcNow;

        await _db.SaveChangesAsync(ct);
    }

    private static (bool Valid, string? Error) Validate(string username, string profileUrl)
    {
        if (string.IsNullOrWhiteSpace(username))
            return (false, "El usuario es obligatorio.");

        if (!Uri.TryCreate(profileUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return (false, "La URL del perfil debe ser una URL http(s) válida.");
        }

        return (true, null);
    }
}
