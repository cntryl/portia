using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>
///     Verifies the framework's tenant-scope authorization: one selected <see cref="TenantRequestAuthorizer" />
///     denies every <see cref="ITenantRequest" /> whose tenant is not the actor's, resolved by the
///     application's <see cref="IActorTenantResolver" />, and hosted startup refuses to run without that
///     resolver rather than letting every tenant request fail or pass by accident.
/// </summary>
public sealed class TenantRequestAuthorizerTests
{
    static readonly TenantId Acme = new("acme");

    /// <summary>An actor acting in its own tenant is authorized and handled.</summary>
    [Fact]
    public async Task ShouldAuthorizeActorInOwnTenant()
    {
        // Arrange
        await using var provider = Provider();

        // Act
        var exception = await Record.ExceptionAsync(async () => await RequestScenario.For(provider)
            .GivenActor(Actor("acme"))
            .When(new TenantScopedCommand(Acme))
            .ExpectAuthorized()
            .ExpectHandled()
            .ExpectSuccess());

        // Assert
        Assert.Null(exception);
    }

    /// <summary>An actor whose tenant differs, has no tenant, or has a malformed tenant claim is denied.</summary>
    /// <param name="claim">The actor's tenant claim, or null for none.</param>
    [Theory]
    [InlineData("beta")]
    [InlineData(null)]
    [InlineData("a/b")]
    public async Task ShouldDenyActorOutsideRequestTenant(string? claim)
    {
        // Arrange
        await using var provider = Provider();

        // Act
        var exception = await Record.ExceptionAsync(async () => await RequestScenario.For(provider)
            .GivenActor(Actor(claim))
            .When(new TenantScopedCommand(Acme))
            .ExpectDenied(RequestErrorKind.Forbidden)
            .ExpectNotHandled());

        // Assert
        Assert.Null(exception);
    }

    /// <summary>An actor with two tenant claims belongs to no single tenant and is denied.</summary>
    [Fact]
    public async Task ShouldDenyActorWithAmbiguousTenant()
    {
        // Arrange
        await using var provider = Provider();
        var actor = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "user"), new Claim("tenant_id", "acme"), new Claim("tenant_id", "beta")], "Tests"));

        // Act
        var exception = await Record.ExceptionAsync(async () => await RequestScenario.For(provider)
            .GivenActor(actor)
            .When(new TenantScopedCommand(Acme))
            .ExpectDenied(RequestErrorKind.Forbidden)
            .ExpectNotHandled());

        // Assert
        Assert.Null(exception);
    }

    /// <summary>A system actor acts in any tenant, so reactions and schedules are not blocked.</summary>
    [Fact]
    public async Task ShouldExemptSystemActor()
    {
        // Arrange
        await using var provider = Provider();

        // Act
        var exception = await Record.ExceptionAsync(async () => await RequestScenario.For(provider)
            .GivenActor(RequestActor.System)
            .When(new TenantScopedCommand(Acme))
            .ExpectAuthorized()
            .ExpectHandled());

        // Assert
        Assert.Null(exception);
    }

    /// <summary>Hosted startup fails when the authorizer is selected but no resolver is registered.</summary>
    [Fact]
    public async Task ShouldRequireResolverAtStartup()
    {
        // Arrange
        var builder = Host.CreateApplicationBuilder();
        _ = builder.Services.AddPortia().AddRequestHandler<TenantScopedCommandHandler>()
            .AddRequestAuthorizer<TenantRequestAuthorizer>();

        // Act
        using var host = builder.Build();

        // Assert
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.StartsWith("IActorTenantResolver is required by TenantRequestAuthorizer.", failure.Message,
            StringComparison.Ordinal);
    }

    /// <summary>A registered resolver satisfies hosted startup.</summary>
    [Fact]
    public async Task ShouldStartWithResolver()
    {
        // Arrange
        var builder = Host.CreateApplicationBuilder();
        _ = builder.Services.AddSingleton<IActorTenantResolver>(new ClaimTenantResolver("tenant_id"));
        _ = builder.Services.AddPortia().AddRequestHandler<TenantScopedCommandHandler>()
            .AddRequestAuthorizer<TenantRequestAuthorizer>();
        using var host = builder.Build();

        // Act
        var exception = await Record.ExceptionAsync(async () =>
        {
            await host.StartAsync();
            await host.StopAsync();
        });

        // Assert
        Assert.Null(exception);
    }

    static ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton<IActorTenantResolver>(new ClaimTenantResolver("tenant_id"));
        _ = services.AddPortia().AddRequestHandler<TenantScopedCommandHandler>()
            .AddRequestAuthorizer<TenantRequestAuthorizer>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    static ClaimsPrincipal Actor(string? tenant) => new(new ClaimsIdentity(
        tenant is null ? [new Claim("sub", "user")] : [new Claim("sub", "user"), new Claim("tenant_id", tenant)],
        "Tests"));
}
