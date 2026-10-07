using EventPipeline.Core.Data;
using EventPipeline.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace EventPipeline.Web.Features.Auth;

/// <summary>
/// Web UI administrator accounts (single user per deployment). Password hashes use
/// PBKDF2-SHA256 (see <see cref="PasswordHasher"/> for the stored format).
/// </summary>
public class AuthService
{
    private const int MinPasswordLength = 12;

    private readonly AppDbContext _db;

    public AuthService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Verifies credentials and updates LastLoginAtUtc on success.</summary>
    public async Task<bool> VerifyAsync(string username, string password, CancellationToken ct = default)
    {
        var user = await _db.AppUsers.FirstOrDefaultAsync(u => u.Username == username, ct);
        if (user == null)
            return false;

        if (!PasswordHasher.Verify(password, user.PasswordHash))
            return false;

        user.LastLoginAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Changes the password after validating the current one. Clears the
    /// must-change flag so the initial forced change counts as done.
    /// </summary>
    public async Task<bool> ChangePasswordAsync(
        string username, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        if (newPassword.Length < MinPasswordLength)
            return false;

        var user = await _db.AppUsers.FirstOrDefaultAsync(u => u.Username == username, ct);
        if (user == null || !PasswordHasher.Verify(currentPassword, user.PasswordHash))
            return false;

        user.PasswordHash = PasswordHasher.Hash(newPassword);
        user.MustChangePassword = false;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public Task<AppUser?> GetAsync(string username, CancellationToken ct = default)
    {
        return _db.AppUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == username, ct);
    }
}
