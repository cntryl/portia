using System.Security.Claims;
using Microsoft.Extensions.Configuration;

namespace Cntryl.Portia;

/// <summary>Protects direct operational APIs independently of an application's permission policy.</summary>
public sealed class SystemOperatorAccessTests
{
    /// <summary>A role on an unauthenticated identity cannot borrow another identity's authentication.</summary>
    /// <param name="roleClaimType">The role claim type selected by the host's authentication scheme.</param>
    [Theory]
    [InlineData(ClaimTypes.Role)]
    [InlineData("roles")]
    public async Task ShouldDenyAdministratorRoleGivenUnauthenticatedIdentityOnMixedPrincipal(string roleClaimType)
    {
        // Arrange
        var store = new ObservedStore();
        var topology = new ObservedTopology();
        var policy = new PermissivePolicy();
        var operations = PortiaOperations.Create(TestOperationalAuthorizer.Configuration(), store,
            new OperationalOptions(), topology, policy);
        var ordinary = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "application-user")],
            "test", ClaimTypes.Name, roleClaimType);
        var unauthenticatedAdministrator = new ClaimsIdentity(
            [new Claim(roleClaimType, TestOperationalAuthorizer.AdministratorRole)],
            null, ClaimTypes.Name, roleClaimType);

        // Act
        var actor = new ClaimsPrincipal([ordinary, unauthenticatedAdministrator]);

        // Assert
        Assert.True(ordinary.IsAuthenticated);
        Assert.False(unauthenticatedAdministrator.IsAuthenticated);
        Assert.True(actor.IsInRole(TestOperationalAuthorizer.AdministratorRole));
        Assert.True(await policy.AuthorizeAsync(actor, OperationalScope.Global, OperationalAccess.Read));

        _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await operations.ListIssuesAsync(actor, OperationalScope.Global));

        Assert.Equal(0, store.Reads);
        Assert.Equal(0, store.Writes);
        Assert.Equal(0, topology.Reads);
    }

    /// <summary>Authenticated secondary identities retain administrator access with the host's role claim type.</summary>
    /// <param name="roleClaimType">The role claim type selected by the host's authentication scheme.</param>
    [Theory]
    [InlineData(ClaimTypes.Role)]
    [InlineData("roles")]
    public async Task ShouldAllowAdministratorGivenAuthenticatedSecondaryIdentityWithCustomRoleClaims(string roleClaimType)
    {
        // Arrange
        var topology = new ObservedTopology();
        var operations = PortiaOperations.Create(TestOperationalAuthorizer.Configuration(), new ObservedStore(),
            new OperationalOptions(), topology, new PermissivePolicy());
        var administrator = new ClaimsIdentity(
            [new Claim(roleClaimType, TestOperationalAuthorizer.AdministratorRole)],
            "test", ClaimTypes.Name, roleClaimType);
        var actor = new ClaimsPrincipal([new ClaimsIdentity(), administrator]);

        // Act
        var description = await operations.DescribeApplicationAsync(actor, OperationalScope.Global);

        // Assert
        Assert.Equal("test", description.Coverage);
        Assert.Equal(1, topology.Reads);
    }

    /// <summary>DI uses host role configuration even when no custom clock is registered.</summary>
    [Fact]
    public async Task ShouldUseConfiguredAdministratorRoleGivenResolutionWithoutRegisteredClock()
    {
        // Arrange
        var topology = new ObservedTopology();
        await using var client = new Client(new ClientConfig(new Uri("ws://127.0.0.1:1/ws")));
        var services = new ServiceCollection();
        _ = services.AddSingleton<IConfiguration>(TestOperationalAuthorizer.Configuration());
        _ = services.AddSingleton<IApplicationTopology>(topology);
        _ = services.AddSingleton<IOperationalAuthorizer, PermissivePolicy>();
        _ = services.AddPortia().UseFitzClient(client, fitz => fitz.DisableRequestWorkers().UseOperations(
            "kv://system-operator-tests/portia/checkpoints", options => options.QuiescedWorkerCutover = true));

        // Act
        using var provider = services.BuildServiceProvider();

        // Assert
        Assert.Null(provider.GetService<TimeProvider>());

        var result = await provider.GetRequiredService<IPortiaOperations>()
            .DescribeApplicationAsync(TestOperationalAuthorizer.AdministratorActor(), OperationalScope.Global);

        Assert.Equal("test", result.Coverage);
        Assert.Equal(1, topology.Reads);
    }

    /// <summary>Administrator membership alone does not grant direct operational permissions.</summary>
    [Fact]
    public async Task ShouldDenyConfiguredAdministratorsGivenNoExplicitOperationalPolicy()
    {
        // Arrange
        var store = new ObservedStore();

        // Act
        var operations = new PortiaOperations(TestOperationalAuthorizer.Configuration(), store,
            new OperationalOptions(), new ObservedTopology(), null, null);

        // Assert
        _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await operations.ListIssuesAsync(TestOperationalAuthorizer.AdministratorActor(), OperationalScope.Global));

        Assert.Equal(0, store.Reads);
        Assert.Equal(0, store.Writes);
    }

    /// <summary>Rejects ordinary application users before any operational read or repair mutation.</summary>
    /// <param name="operation">The operator API to invoke.</param>
    [Theory]
    [InlineData("describe")]
    [InlineData("health")]
    [InlineData("issues")]
    [InlineData("inspect")]
    [InlineData("trace")]
    [InlineData("preview")]
    [InlineData("approve")]
    [InlineData("revoke")]
    [InlineData("execute")]
    [InlineData("verify")]
    public async Task ShouldRejectApplicationUsersGivenOperationalPolicyAllowingEverything(string operation)
    {
        // Arrange
        var store = new ObservedStore();
        var topology = new ObservedTopology();
        var policy = new PermissivePolicy();
        var operations = new PortiaOperations(store, new OperationalOptions(), topology, policy);
        var actor = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "application-user"),
                new Claim(ClaimTypes.Role, "application-user-" + Environment.GetEnvironmentVariable("PORTIA_SYSTEM_ADMIN_ROLE"))], "test"));
        var target = new OperationalTarget("processor", "stream://realm/events/*", OperationalScope.Global);

        // Act
        var id = Guid.NewGuid();

        // Assert
        Assert.True(await policy.AuthorizeAsync(actor, OperationalScope.Global, OperationalAccess.Approve));

        _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            switch (operation)
            {
                case "describe":
                    _ = await operations.DescribeApplicationAsync(actor, target.Scope);
                    break;
                case "health":
                    _ = await operations.GetSystemHealthAsync(actor, target.Scope);
                    break;
                case "issues":
                    _ = await operations.ListIssuesAsync(actor, target.Scope);
                    break;
                case "inspect":
                    _ = await operations.InspectProcessorAsync(actor, target);
                    break;
                case "trace":
                    _ = await operations.TraceCausalityAsync(actor, target, id);
                    break;
                case "preview":
                    _ = await operations.PreviewRepairAsync(actor, target);
                    break;
                case "approve":
                    _ = await operations.ApproveRepairAsync(actor, target, id);
                    break;
                case "revoke":
                    await operations.RevokeRepairAsync(actor, target, id);
                    break;
                case "execute":
                    _ = await operations.ExecuteRepairAsync(actor, target, id);
                    break;
                case "verify":
                    _ = await operations.VerifyRepairAsync(actor, target, id);
                    break;
                default:
                    throw new InvalidOperationException("Unknown test operation.");
            }
        });

        Assert.Equal(0, store.Reads);
        Assert.Equal(0, store.Writes);
        Assert.Equal(0, topology.Reads);
    }

    sealed class ObservedStore : IOperationalStore
    {
        internal int Reads { get; private set; }
        internal int Writes { get; private set; }

        public ValueTask<OperationalProcessorState?> ReadAsync(OperationalTarget target, CancellationToken ct = default)
        {
            Reads++;
            return ValueTask.FromResult<OperationalProcessorState?>(null);
        }

        public ValueTask<IReadOnlyList<OperationalProcessorState>> ListAsync(OperationalScope scope,
            CancellationToken ct = default)
        {
            Reads++;
            return ValueTask.FromResult<IReadOnlyList<OperationalProcessorState>>([]);
        }

        public ValueTask<bool> CompareExchangeAsync(OperationalProcessorState replacement, long expectedRevision,
            Guid expectedOwner, CancellationToken ct = default)
        {
            Writes++;
            return ValueTask.FromResult(false);
        }
    }

    sealed class ObservedTopology : IApplicationTopology
    {
        internal int Reads { get; private set; }

        public ApplicationTopology Describe()
        {
            Reads++;
            return new ApplicationTopology([], [], "test");
        }
    }

    sealed class PermissivePolicy : IOperationalAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal actor, OperationalScope scope,
            OperationalAccess permission, CancellationToken ct = default) => ValueTask.FromResult(true);
    }
}
