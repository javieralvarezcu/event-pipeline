using EventPipeline.Core.Entities;
using EventPipeline.Web.Features.Auth;
using Microsoft.EntityFrameworkCore;

namespace EventPipeline.Tests;

public class AuthServiceTests
{
    [Fact]
    public async Task Verify_AcceptsTheRightPasswordAndTracksLastLogin()
    {
        using var fixture = new TestDatabase();
        fixture.Db.AppUsers.Add(new AppUser
        {
            Username = "admin",
            PasswordHash = PasswordHasher.Hash("super-secret-password"),
            MustChangePassword = true
        });
        await fixture.Db.SaveChangesAsync();

        var service = new AuthService(fixture.Db);

        Assert.True(await service.VerifyAsync("admin", "super-secret-password"));

        var user = await fixture.Db.AppUsers.SingleAsync(u => u.Username == "admin");
        Assert.NotNull(user.LastLoginAtUtc);
        Assert.True(user.MustChangePassword); // flag untouched by Verify
    }

    [Fact]
    public async Task Verify_RejectsWrongPasswordOrUnknownUser()
    {
        using var fixture = new TestDatabase();
        fixture.Db.AppUsers.Add(new AppUser
        {
            Username = "admin",
            PasswordHash = PasswordHasher.Hash("super-secret-password")
        });
        await fixture.Db.SaveChangesAsync();

        var service = new AuthService(fixture.Db);

        Assert.False(await service.VerifyAsync("admin", "wrong-password"));
        Assert.False(await service.VerifyAsync("nobody", "super-secret-password"));

        var user = await fixture.Db.AppUsers.SingleAsync(u => u.Username == "admin");
        Assert.Null(user.LastLoginAtUtc);
    }

    [Fact]
    public async Task ChangePassword_RequiresCurrentPasswordAndClearsTheFlag()
    {
        using var fixture = new TestDatabase();
        fixture.Db.AppUsers.Add(new AppUser
        {
            Username = "admin",
            PasswordHash = PasswordHasher.Hash("old-password-123"),
            MustChangePassword = true
        });
        await fixture.Db.SaveChangesAsync();

        var service = new AuthService(fixture.Db);

        Assert.False(await service.ChangePasswordAsync("admin", "wrong-current", "new-password-456"));
        Assert.True(await service.ChangePasswordAsync("admin", "old-password-123", "new-password-456"));

        var user = await fixture.Db.AppUsers.SingleAsync(u => u.Username == "admin");
        Assert.False(user.MustChangePassword);
        Assert.True(await service.VerifyAsync("admin", "new-password-456"));
        Assert.False(await service.VerifyAsync("admin", "old-password-123"));
    }

    [Fact]
    public async Task ChangePassword_RejectsShortNewPasswords()
    {
        using var fixture = new TestDatabase();
        fixture.Db.AppUsers.Add(new AppUser
        {
            Username = "admin",
            PasswordHash = PasswordHasher.Hash("old-password-123")
        });
        await fixture.Db.SaveChangesAsync();

        var service = new AuthService(fixture.Db);

        Assert.False(await service.ChangePasswordAsync("admin", "old-password-123", "corta"));
    }

    [Fact]
    public void Hash_NeverStoresTheLiteralPasswordAndFollowsTheFormat()
    {
        var hash = PasswordHasher.Hash("super-secret-password");

        Assert.DoesNotContain("super-secret-password", hash);
        var parts = hash.Split('|');
        Assert.Equal(4, parts.Length);
        Assert.Equal("v1", parts[0]);
        Assert.Equal("210000", parts[1]);
        Assert.NotEmpty(parts[2]);
        Assert.NotEmpty(parts[3]);
    }

    [Fact]
    public void Hash_SaltsDifferBetweenHashes()
    {
        var first = PasswordHasher.Hash("same-password-123");
        var second = PasswordHasher.Hash("same-password-123");

        Assert.NotEqual(first, second);
        Assert.True(PasswordHasher.Verify("same-password-123", first));
        Assert.True(PasswordHasher.Verify("same-password-123", second));
    }
}
