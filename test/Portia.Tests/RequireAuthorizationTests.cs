using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>
///     Verifies opt-in fail-closed authorization: when authorization is required, every request must be
///     protected by an applicable authorizer or a declared permission, or be explicitly allowed anonymous.
///     An unprotected request is a composition error, so it throws instead of looking like a denial.
/// </summary>
public sealed class RequireAuthorizationTests
{
    static readonly RequestAuthorizationRequirement Required = new([typeof(AnonymousQuery)]);

    /// <summary>An unprotected command throws and never reaches its handler.</summary>
    [Fact]
    public async Task ShouldRejectUnprotectedCommandAtDispatchWhenAuthorizationIsRequired()
    {
        var (bus, calls) = Bus(Required);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bus.DispatchAsync(new UnprotectedCommand(), bus.CreateContext(RequestActor.System)).AsTask());

        AssertUnprotected<UnprotectedCommand>(error);
        Assert.Empty(calls);
    }

    /// <summary>An unprotected result-bearing request throws and never reaches its handler.</summary>
    [Fact]
    public async Task ShouldRejectUnprotectedQueryAtDispatchWhenAuthorizationIsRequired()
    {
        var (bus, calls) = Bus(Required);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bus.DispatchAsync(new UnprotectedQuery(), bus.CreateContext(RequestActor.System)).AsTask());

        AssertUnprotected<UnprotectedQuery>(error);
        Assert.Empty(calls);
    }

    /// <summary>An unprotected stream throws before producing its first item.</summary>
    [Fact]
    public async Task ShouldRejectUnprotectedStreamBeforeFirstItemWhenAuthorizationIsRequired()
    {
        var (bus, calls) = Bus(Required);
        var items = new List<int>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var item in bus.DispatchStreamAsync(new UnprotectedStream(),
                               bus.CreateContext(RequestActor.System)))
            {
                items.Add(item);
            }
        });

        AssertUnprotected<UnprotectedStream>(error);
        Assert.Empty(items);
        Assert.Empty(calls);
    }

    /// <summary>The accept-now-run-later authorization check rejects an unprotected request too.</summary>
    [Fact]
    public async Task ShouldRejectUnprotectedRequestAtAuthorizeAsyncWhenAuthorizationIsRequired()
    {
        var (bus, _) = Bus(Required);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bus.AuthorizeAsync(new UnprotectedCommand(), bus.CreateContext(RequestActor.System)).AsTask());

        AssertUnprotected<UnprotectedCommand>(error);
    }

    /// <summary>A request allowed anonymous dispatches without any authorizer or permission.</summary>
    [Fact]
    public async Task ShouldDispatchAnonymousAllowedRequestWhenAuthorizationIsRequired()
    {
        var (bus, calls) = Bus(Required);

        var result = await bus.DispatchAsync(new AnonymousQuery(), bus.CreateContext(RequestActor.Anonymous));

        Assert.True(result.IsSuccess);
        Assert.Equal(["anonymous-handler"], calls);
    }

    /// <summary>A concrete-request authorizer protects its request.</summary>
    [Fact]
    public async Task ShouldDispatchRequestProtectedByConcreteAuthorizerWhenAuthorizationIsRequired()
    {
        var (bus, calls) = Bus(Required);

        var result = await bus.DispatchAsync(new AuthorizedCommand(), bus.CreateContext(RequestActor.System));

        Assert.True(result.IsSuccess);
        Assert.Equal(["concrete-authorizer", "authorized-handler"], calls);
    }

    /// <summary>A request-family authorizer protects every request in the family.</summary>
    [Fact]
    public async Task ShouldDispatchRequestProtectedByFamilyAuthorizerWhenAuthorizationIsRequired()
    {
        var (bus, calls) = Bus(Required);

        var result = await bus.DispatchAsync(new FamilyCommand(), bus.CreateContext(RequestActor.System));

        Assert.True(result.IsSuccess);
        Assert.Equal(["family-authorizer", "family-handler"], calls);
    }

    /// <summary>A principal-stage authorizer scoped to every request protects every request.</summary>
    [Fact]
    public async Task ShouldTreatPrincipalStageBaseAuthorizerAsProtectingEveryRequest()
    {
        var (bus, calls) = Bus(Required,
            new RequestAuthorizerRegistration<IRequestBase, EveryRequestAuthorizer>(AuthorizationStage.Principal));

        var result = await bus.DispatchAsync(new UnprotectedCommand(), bus.CreateContext(RequestActor.System));

        Assert.True(result.IsSuccess);
        Assert.Equal(["every-request-authorizer", "unprotected-handler"], calls);
    }

    /// <summary>A declared permission protects its request.</summary>
    [Fact]
    public async Task ShouldDispatchPermissionProtectedRequestWhenAuthorizationIsRequired()
    {
        var (bus, calls) = Bus(Required);

        var result = await bus.DispatchAsync(new PermissionCommand(), bus.CreateContext(RequestActor.System));

        Assert.True(result.IsSuccess);
        Assert.Equal(["permission-handler"], calls);
    }

    /// <summary>Without the requirement, an unprotected request dispatches exactly as before.</summary>
    [Fact]
    public async Task ShouldDispatchUnprotectedRequestWhenAuthorizationIsNotRequired()
    {
        var (bus, calls) = Bus(requirement: null);

        var result = await bus.DispatchAsync(new UnprotectedCommand(), bus.CreateContext(RequestActor.System));

        Assert.True(result.IsSuccess);
        Assert.Equal(["unprotected-handler"], calls);
    }

    /// <summary>The registry exposes unprotected request types only when authorization is required.</summary>
    [Fact]
    public void ShouldExposeUnprotectedRequestTypesOnlyWhenAuthorizationIsRequired()
    {
        var required = Registry(Required);
        var optional = Registry(requirement: null);

        Assert.True(required.RequiresAuthorization);
        Assert.Equal(
            [typeof(UnprotectedCommand).FullName!, typeof(UnprotectedQuery).FullName!, typeof(UnprotectedStream).FullName!],
            required.UnprotectedRequestTypes.Select(type => type.FullName!).Order(StringComparer.Ordinal));
        Assert.False(optional.RequiresAuthorization);
        Assert.Empty(optional.UnprotectedRequestTypes);
    }

    /// <summary>RequireAuthorization chains on the same builder.</summary>
    [Fact]
    public void ShouldReturnSameBuilderFromRequireAuthorization()
    {
        var builder = new ServiceCollection().AddPortia();

        Assert.Same(builder, builder.RequireAuthorization());
    }

    /// <summary>A provider composed without hosting still refuses unprotected requests.</summary>
    [Fact]
    public async Task ShouldRejectUnprotectedRequestAtDispatchInNonHostedProvider()
    {
        await using var provider = Composed(portia => portia.RequireAuthorization());
        await using var scope = provider.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bus.DispatchAsync(new UnprotectedCommand(), bus.CreateContext(RequestActor.System)).AsTask());

        AssertUnprotected<UnprotectedCommand>(error);
    }

    /// <summary>A request allowed anonymous at the composition root dispatches.</summary>
    [Fact]
    public async Task ShouldDispatchAnonymousAllowedRequestInNonHostedProvider()
    {
        await using var provider = Composed(portia =>
            portia.RequireAuthorization(options => options.AllowAnonymous<AnonymousQuery>()));
        await using var scope = provider.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();

        var result = await bus.DispatchAsync(new AnonymousQuery(), bus.CreateContext(RequestActor.Anonymous));

        Assert.Equal("public", result.Value);
    }

    /// <summary>Feature assemblies can each contribute anonymous requests to the same requirement.</summary>
    [Fact]
    public async Task ShouldUnionAnonymousRequestsAcrossRepeatedRequireAuthorizationCalls()
    {
        await using var provider = Composed(portia => portia
            .RequireAuthorization(options => options.AllowAnonymous<AnonymousQuery>())
            .RequireAuthorization(options => options.AllowAnonymous<UnprotectedCommand>()));

        var registry = provider.GetRequiredService<RequestRegistry>();

        Assert.True(registry.RequiresAuthorization);
        Assert.Empty(registry.UnprotectedRequestTypes);
    }

    /// <summary>A request family allowed anonymous covers every request in the family.</summary>
    [Fact]
    public void ShouldAllowRequestFamilyAnonymous()
    {
        var registry = Registry(new RequestAuthorizationRequirement([typeof(IPublicRequest)]));

        Assert.Equal(
            [typeof(AnonymousQuery).FullName!, typeof(UnprotectedStream).FullName!],
            registry.UnprotectedRequestTypes.Select(type => type.FullName!).Order(StringComparer.Ordinal));
    }

    /// <summary>Allowing every request anonymous is an explicit, supported choice.</summary>
    [Fact]
    public async Task ShouldAllowEveryRequestAnonymousThroughRequestBase()
    {
        await using var provider = Composed(portia =>
            portia.RequireAuthorization(options => options.AllowAnonymous<IRequestBase>()));

        var registry = provider.GetRequiredService<RequestRegistry>();

        Assert.True(registry.RequiresAuthorization);
        Assert.Empty(registry.UnprotectedRequestTypes);
    }

    /// <summary>Shared setup may allow requests another host does not register.</summary>
    [Fact]
    public async Task ShouldIgnoreAnonymousRequestsWithoutRegisteredHandlers()
    {
        await using var provider = Composed(portia => portia.RequireAuthorization(options => options
            .AllowAnonymous<AnonymousQuery>()
            .AllowAnonymous<UnprotectedCommand>()
            .AllowAnonymous<PermissionCommand>()));

        var registry = provider.GetRequiredService<RequestRegistry>();

        Assert.Empty(registry.UnprotectedRequestTypes);
    }

    /// <summary>Without RequireAuthorization the composed bus dispatches unprotected requests as before.</summary>
    [Fact]
    public async Task ShouldNotChangeDispatchWhenRequireAuthorizationIsNotCalled()
    {
        await using var provider = Composed(_ => { });
        await using var scope = provider.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();

        var result = await bus.DispatchAsync(new UnprotectedCommand(), bus.CreateContext(RequestActor.System));

        Assert.True(result.IsSuccess);
    }

    static ServiceProvider Composed(Action<PortiaBuilder> configure)
    {
        var services = new ServiceCollection();
        configure(services.AddPortia());
        _ = services
            .AddSingleton(new List<string>())
            .AddSingleton<RequestHandlerRegistration>(new RequestRegistration<UnprotectedCommand, UnprotectedCommandHandler>())
            .AddSingleton<RequestHandlerRegistration>(new RequestRegistration<AnonymousQuery, AnonymousQueryHandler, string>())
            .AddSingleton<UnprotectedCommandHandler>()
            .AddSingleton<AnonymousQueryHandler>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>Hosted startup names every unprotected request type in stable order before serving.</summary>
    [Fact]
    public async Task ShouldFailHostedStartupListingUnprotectedRequestTypesInOrdinalOrder()
    {
        using var host = Hosted(portia => portia.RequireAuthorization(options => options.AllowAnonymous<AnonymousQuery>()));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        const string prefix =
            "Authorization is required, but these request types have no applicable request authorizer or [RequiresPermission]: ";
        Assert.StartsWith(prefix, failure.Message, StringComparison.Ordinal);
        Assert.Equal(
            [typeof(UnprotectedCommand).FullName!, typeof(UnprotectedStream).FullName!],
            failure.Message[prefix.Length..failure.Message.IndexOf(". ", prefix.Length, StringComparison.Ordinal)]
                .Split(", "));
        Assert.Contains("AllowAnonymous", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Hosted startup succeeds once every request is protected or allowed anonymous.</summary>
    [Fact]
    public async Task ShouldStartHostWhenEveryRequestIsProtectedOrAnonymous()
    {
        using var host = Hosted(portia => portia.RequireAuthorization(options => options
            .AllowAnonymous<AnonymousQuery>()
            .AllowAnonymous<UnprotectedCommand>()
            .AllowAnonymous<UnprotectedStream>()));

        await host.StartAsync();
        await host.StopAsync();
    }

    /// <summary>Without RequireAuthorization hosted startup does not examine request protection.</summary>
    [Fact]
    public async Task ShouldNotValidateProtectionWhenAuthorizationIsNotRequired()
    {
        using var host = Hosted(_ => { });

        await host.StartAsync();
        await host.StopAsync();
    }

    /// <summary>A replaced registry cannot silently switch fail-closed authorization off.</summary>
    [Fact]
    public async Task ShouldFailHostedStartupWhenAuthorizationIsRequiredButRegistryWasReplaced()
    {
        var builder = Host.CreateApplicationBuilder();
        _ = builder.Services.AddSingleton(new RequestRegistry([], [], [], [], []));
        _ = builder.Services.AddPortia().RequireAuthorization();
        using var host = builder.Build();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("RequireAuthorization()", failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(RequestRegistry), failure.Message, StringComparison.Ordinal);
    }

    static IHost Hosted(Action<PortiaBuilder> configure)
    {
        var builder = Host.CreateApplicationBuilder();
        configure(builder.Services.AddPortia());
        _ = builder.Services
            .AddSingleton(new List<string>())
            .AddSingleton<IPermissionEvaluator>(TestPermissionEvaluator.AllowAll())
            .AddSingleton<RequestHandlerRegistration>(new RequestRegistration<UnprotectedCommand, UnprotectedCommandHandler>())
            .AddSingleton<RequestHandlerRegistration>(new StreamRequestRegistration<UnprotectedStream, UnprotectedStreamHandler, int>())
            .AddSingleton<RequestHandlerRegistration>(new RequestRegistration<AnonymousQuery, AnonymousQueryHandler, string>())
            .AddSingleton<RequestHandlerRegistration>(new RequestRegistration<AuthorizedCommand, AuthorizedCommandHandler>())
            .AddSingleton<RequestHandlerRegistration>(
                new RequestRegistration<PermissionCommand, PermissionCommandHandler>(_ => "commands:permission"))
            .AddSingleton<RequestAuthorizerRegistration>(new RequestAuthorizerRegistration<AuthorizedCommand, ConcreteAuthorizer>());
        return builder.Build();
    }

    static void AssertUnprotected<TRequest>(InvalidOperationException error)
    {
        Assert.Contains(typeof(TRequest).FullName!, error.Message, StringComparison.Ordinal);
        Assert.Contains("AllowAnonymous", error.Message, StringComparison.Ordinal);
    }

    static RequestRegistry Registry(RequestAuthorizationRequirement? requirement,
        params RequestAuthorizerRegistration[] extraAuthorizers) =>
        new(
            [
                new RequestRegistration<UnprotectedCommand, UnprotectedCommandHandler>(),
                new RequestRegistration<UnprotectedQuery, UnprotectedQueryHandler, string>(),
                new StreamRequestRegistration<UnprotectedStream, UnprotectedStreamHandler, int>(),
                new RequestRegistration<AnonymousQuery, AnonymousQueryHandler, string>(),
                new RequestRegistration<AuthorizedCommand, AuthorizedCommandHandler>(),
                new RequestRegistration<FamilyCommand, FamilyCommandHandler>(),
                new RequestRegistration<PermissionCommand, PermissionCommandHandler>(_ => "commands:permission")
            ],
            [
                new RequestAuthorizerRegistration<AuthorizedCommand, ConcreteAuthorizer>(),
                new RequestAuthorizerRegistration<IAuthorizedFamily, FamilyAuthorizer>(),
                .. extraAuthorizers
            ],
            [], [], [], requirement);

    static (RequestBus Bus, List<string> Calls) Bus(RequestAuthorizationRequirement? requirement,
        params RequestAuthorizerRegistration[] extraAuthorizers)
    {
        var calls = new List<string>();
        var services = new ServiceCollection()
            .AddSingleton(calls)
            .AddSingleton<IPermissionEvaluator>(TestPermissionEvaluator.AllowAll())
            .AddSingleton<UnprotectedCommandHandler>()
            .AddSingleton<UnprotectedQueryHandler>()
            .AddSingleton<UnprotectedStreamHandler>()
            .AddSingleton<AnonymousQueryHandler>()
            .AddSingleton<AuthorizedCommandHandler>()
            .AddSingleton<FamilyCommandHandler>()
            .AddSingleton<PermissionCommandHandler>()
            .AddSingleton<ConcreteAuthorizer>()
            .AddSingleton<FamilyAuthorizer>()
            .AddSingleton<EveryRequestAuthorizer>()
            .BuildServiceProvider();
        return (new RequestBus(services, Registry(requirement, extraAuthorizers)), calls);
    }

    internal interface IAuthorizedFamily : IRequest;

    internal interface IPublicRequest : IRequestBase;

    internal sealed record UnprotectedCommand : IRequest, IPublicRequest;

    internal sealed record UnprotectedQuery : IRequest<string>, IPublicRequest;

    internal sealed record UnprotectedStream : IStreamRequest<int>;

    internal sealed record AnonymousQuery : IRequest<string>;

    internal sealed record AuthorizedCommand : IRequest;

    internal sealed record FamilyCommand : IAuthorizedFamily;

    internal sealed record PermissionCommand : IRequest;

    internal sealed class UnprotectedCommandHandler(List<string> calls) : IRequestHandler<UnprotectedCommand>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<UnprotectedCommand> context, CancellationToken ct)
        {
            calls.Add("unprotected-handler");
            return ValueTask.FromResult(Result.Success);
        }
    }

    internal sealed class UnprotectedQueryHandler(List<string> calls) : IRequestHandler<UnprotectedQuery, string>
    {
        public ValueTask<Result<string>> HandleAsync(IRequestContext<UnprotectedQuery> context, CancellationToken ct)
        {
            calls.Add("unprotected-query-handler");
            return ValueTask.FromResult(Result<string>.Success("value"));
        }
    }

    internal sealed class UnprotectedStreamHandler(List<string> calls) : IStreamRequestHandler<UnprotectedStream, int>
    {
        public async IAsyncEnumerable<int> HandleAsync(IRequestContext<UnprotectedStream> context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            calls.Add("unprotected-stream-handler");
            yield return 1;
            await Task.CompletedTask;
        }
    }

    internal sealed class AnonymousQueryHandler(List<string> calls) : IRequestHandler<AnonymousQuery, string>
    {
        public ValueTask<Result<string>> HandleAsync(IRequestContext<AnonymousQuery> context, CancellationToken ct)
        {
            calls.Add("anonymous-handler");
            return ValueTask.FromResult(Result<string>.Success("public"));
        }
    }

    internal sealed class AuthorizedCommandHandler(List<string> calls) : IRequestHandler<AuthorizedCommand>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<AuthorizedCommand> context, CancellationToken ct)
        {
            calls.Add("authorized-handler");
            return ValueTask.FromResult(Result.Success);
        }
    }

    internal sealed class FamilyCommandHandler(List<string> calls) : IRequestHandler<FamilyCommand>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<FamilyCommand> context, CancellationToken ct)
        {
            calls.Add("family-handler");
            return ValueTask.FromResult(Result.Success);
        }
    }

    internal sealed class PermissionCommandHandler(List<string> calls) : IRequestHandler<PermissionCommand>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<PermissionCommand> context, CancellationToken ct)
        {
            calls.Add("permission-handler");
            return ValueTask.FromResult(Result.Success);
        }
    }

    internal sealed class ConcreteAuthorizer(List<string> calls) : IRequestAuthorizer<AuthorizedCommand>
    {
        public ValueTask<Result> AuthorizeAsync(IRequestContext<AuthorizedCommand> context, CancellationToken ct)
        {
            calls.Add("concrete-authorizer");
            return ValueTask.FromResult(Result.Success);
        }
    }

    internal sealed class FamilyAuthorizer(List<string> calls) : IRequestAuthorizer<IAuthorizedFamily>
    {
        public ValueTask<Result> AuthorizeAsync(IRequestContext<IAuthorizedFamily> context, CancellationToken ct)
        {
            calls.Add("family-authorizer");
            return ValueTask.FromResult(Result.Success);
        }
    }

    internal sealed class EveryRequestAuthorizer(List<string> calls) : IRequestAuthorizer<IRequestBase>
    {
        public ValueTask<Result> AuthorizeAsync(IRequestContext<IRequestBase> context, CancellationToken ct)
        {
            calls.Add("every-request-authorizer");
            return ValueTask.FromResult(Result.Success);
        }
    }
}
