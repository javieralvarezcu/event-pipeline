using EventPipeline.Web.Features.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace EventPipeline.Tests;

public class SettingsServiceTests
{
    [Fact]
    public async Task Secret_RoundTripsEncryptedAtRest()
    {
        using var fixture = new TestDatabase();
        var service = new SettingsService(fixture.Db, new EphemeralDataProtectionProvider());

        await service.SetSecretAsync(SettingsKeys.DeepSeekApiKey, "sk-test-123");
        var stored = await fixture.Db.AppSettings.SingleAsync(s => s.Key == SettingsKeys.DeepSeekApiKey);

        Assert.True(stored.IsSecret);
        Assert.NotNull(stored.Value);
        Assert.NotEqual("sk-test-123", stored.Value); // encrypted at rest

        var retrieved = await service.GetSecretAsync(SettingsKeys.DeepSeekApiKey);
        Assert.Equal("sk-test-123", retrieved);
    }

    [Fact]
    public async Task Secret_ReturnsNullWhenNotConfigured()
    {
        using var fixture = new TestDatabase();
        var service = new SettingsService(fixture.Db, new EphemeralDataProtectionProvider());

        Assert.Null(await service.GetSecretAsync(SettingsKeys.DeepSeekApiKey));
    }

    [Fact]
    public async Task Secret_IsTreatedAsAbsentWhenStoredAsPlain()
    {
        using var fixture = new TestDatabase();
        var service = new SettingsService(fixture.Db, new EphemeralDataProtectionProvider());

        await service.SetPlainAsync(SettingsKeys.DeepSeekApiKey, "not-a-secret");
        Assert.Null(await service.GetSecretAsync(SettingsKeys.DeepSeekApiKey));
    }

    [Fact]
    public async Task Secret_EmptyValueClearsTheRow()
    {
        using var fixture = new TestDatabase();
        var service = new SettingsService(fixture.Db, new EphemeralDataProtectionProvider());

        await service.SetSecretAsync(SettingsKeys.BrightDataToken, "token");
        await service.SetSecretAsync(SettingsKeys.BrightDataToken, "  ");

        Assert.Null(await service.GetSecretAsync(SettingsKeys.BrightDataToken));
        Assert.False(await fixture.Db.AppSettings.AnyAsync(s => s.Key == SettingsKeys.BrightDataToken));
    }

    [Fact]
    public async Task Plain_RoundTripsAndReturnsNullWhenAbsent()
    {
        using var fixture = new TestDatabase();
        var service = new SettingsService(fixture.Db, new EphemeralDataProtectionProvider());

        Assert.Null(await service.GetPlainAsync(SettingsKeys.IgPostsPerAccount));

        await service.SetPlainAsync(SettingsKeys.IgPostsPerAccount, "6");
        Assert.Equal("6", await service.GetPlainAsync(SettingsKeys.IgPostsPerAccount));

        var stored = await fixture.Db.AppSettings.SingleAsync(s => s.Key == SettingsKeys.IgPostsPerAccount);
        Assert.False(stored.IsSecret);
        Assert.Equal("6", stored.Value);
    }

    [Fact]
    public async Task Plain_OverwritesASecretRow()
    {
        using var fixture = new TestDatabase();
        var service = new SettingsService(fixture.Db, new EphemeralDataProtectionProvider());

        await service.SetSecretAsync(SettingsKeys.BrightDataToken, "token");
        await service.SetPlainAsync(SettingsKeys.BrightDataToken, "plain");

        Assert.Equal("plain", await service.GetPlainAsync(SettingsKeys.BrightDataToken));
        Assert.Null(await service.GetSecretAsync(SettingsKeys.BrightDataToken));
    }
}
