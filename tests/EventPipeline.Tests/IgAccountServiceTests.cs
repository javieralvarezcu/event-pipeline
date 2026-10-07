using EventPipeline.Core.Entities;
using EventPipeline.Web.Features.IgAccounts;
using Microsoft.EntityFrameworkCore;

namespace EventPipeline.Tests;

public class IgAccountServiceTests
{
    [Fact]
    public async Task SetAllEnabled_TogglesEveryAccountAtOnce()
    {
        using var fixture = new TestDatabase();
        fixture.Db.IgAccounts.AddRange(
            new IgAccount { Username = "a", ProfileUrl = "https://www.instagram.com/a/", Enabled = true },
            new IgAccount { Username = "b", ProfileUrl = "https://www.instagram.com/b/", Enabled = false },
            new IgAccount { Username = "c", ProfileUrl = "https://www.instagram.com/c/", Enabled = true });
        await fixture.Db.SaveChangesAsync();

        var service = new IgAccountService(fixture.Db);

        Assert.Equal(3, await service.SetAllEnabledAsync(false));
        // AsNoTracking: ExecuteUpdate toca la BD directamente y el tracker del
        // contexto conservaría los valores antiguos de las entidades ya cargadas.
        Assert.True((await fixture.Db.IgAccounts.AsNoTracking().ToListAsync()).All(a => !a.Enabled));

        Assert.Equal(3, await service.SetAllEnabledAsync(true));
        Assert.True((await fixture.Db.IgAccounts.AsNoTracking().ToListAsync()).All(a => a.Enabled));
    }

    [Fact]
    public async Task SetEnabled_TogglesOneAccount()
    {
        using var fixture = new TestDatabase();
        fixture.Db.IgAccounts.Add(new IgAccount { Username = "a", ProfileUrl = "https://www.instagram.com/a/" });
        await fixture.Db.SaveChangesAsync();
        var id = (await fixture.Db.IgAccounts.SingleAsync()).Id;

        var service = new IgAccountService(fixture.Db);

        Assert.True(await service.SetEnabledAsync(id, false));
        Assert.False((await fixture.Db.IgAccounts.SingleAsync()).Enabled);
        Assert.False(await service.SetEnabledAsync(999, true)); // no existe
    }
}
