using System.Text;

namespace Cntryl.Portia;

/// <summary>
///     Verifies a query-side read can name its tenant as a <see cref="TenantId" /> and lands on exactly the
///     resource the per-tenant projector writes, so the read side does not fall back to a realm string
///     once the request side is typed.
/// </summary>
public sealed class FitzKvProjectionStoreTenantReadTests
{
    /// <summary>A typed tenant read opens the same read-only resource as the realm read for that tenant.</summary>
    [Fact]
    public async Task ShouldOpenTheTenantResourceForTypedRead()
    {
        var client = new FakeKvClient();
        var repository = new BalancesRepository(client, "kv://portia/accounts/balances");
        var tenant = new TenantId("acme");

        _ = await repository.ReadAsync(tenant, "savings");
        _ = await repository.ReadAsync("acme", "savings");

        Assert.Equal(2, client.Transactions.Count);
        Assert.Equal(client.Transactions[1].Route, client.Transactions[0].Route);
        Assert.Equal(FitzKvCheckpoints.WorkloadRoute("kv://portia/accounts/balances", "balances", "acme"),
            client.Transactions[0].Route);
    }

    /// <summary>A default tenant is refused before any transaction opens.</summary>
    [Fact]
    public async Task ShouldRejectDefaultTenant()
    {
        var client = new FakeKvClient();
        var repository = new BalancesRepository(client, "kv://portia/accounts/balances");

        _ = await Assert.ThrowsAnyAsync<ArgumentException>(async () => await repository.ReadAsync(default(TenantId), "savings"));

        Assert.Empty(client.Transactions);
    }

    sealed class BalancesRepository(IKvClient client, string route)
        : FitzKvProjectionStore(client, route, "balances")
    {
        public async Task<string?> ReadAsync(TenantId tenant, string name)
        {
            await using var tx = await BeginTenantReadAsync(tenant);
            return await ReadAsync(tx, name);
        }

        public async Task<string?> ReadAsync(string realm, string name)
        {
            await using var tx = await BeginReadAsync(realm);
            return await ReadAsync(tx, name);
        }

        static async Task<string?> ReadAsync(IKvTransaction tx, string name)
        {
            var result = await tx.GetAsync(Encoding.UTF8.GetBytes("balance\0" + name));
            return result.Found ? Encoding.UTF8.GetString(result.Value!.Value.Span) : null;
        }
    }
}
