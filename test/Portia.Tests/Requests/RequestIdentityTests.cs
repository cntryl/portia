namespace Cntryl.Portia;

/// <summary>Proves explicit logical identity remains separate from receiver executions and source causation.</summary>
public sealed class RequestIdentityTests
{
    /// <summary>Actual RPC serialization, receipt authorization and retries retain one logical key.</summary>
    [Fact]
    public async Task ShouldPreserveLogicalIdentityGivenRpcRoundTrips()
    {
        // Arrange
        var handler = new IdentifiedCommandHandler();
        var authorization = new IdentityAuthorizer();
        using var provider = Compose(new IdentifiedQueryHandler(), handler, authorization);
        var rpc = new InMemoryRpcClient();
        var serializer = TestJson.Serializer(typeof(IdentifiedCommand));
        var server = new FitzRpcRequestServer(rpc, provider.GetRequiredService<IServiceScopeFactory>(),
            TestJson.Catalog(RequestTransportId.Callable, typeof(IdentifiedCommand)));
        await using var worker = await server.RegisterAsync<IdentifiedCommand>();
        var sender = new FitzRemoteRequestSender(rpc, serializer, serializer);
        var request = new IdentifiedCommand(Uuid.CreateVersion4());

        // Act
        var first = await sender.SendAsync(request, new RequestRouteValues(), null);
        var retry = await sender.SendAsync(request, new RequestRouteValues(), null);

        // Assert
        Assert.True(first.IsSuccess);
        Assert.True(retry.IsSuccess);
        Assert.Equal(2, authorization.Calls);
        Assert.Equal(2, handler.Contexts.Count);
        Assert.All(handler.Contexts, context => Assert.Equal(request.CommandId, context.RequestId));
        Assert.Equal(2, handler.Contexts.Select(context => context.ExecutionId).Distinct().Count());
    }

    /// <summary>Wire serialization preserves typed identity and rejects tampered carried intent.</summary>
    [Fact]
    public void ShouldPreserveTypedIdentityAndRejectMismatchGivenSerializedRequest()
    {
        // Arrange
        var request = new IdentifiedCommand(Uuid.CreateVersion4());
        var serializer = TestJson.Serializer(typeof(IdentifiedCommand));
        var metadata = RequestMetadata.FromRequest(request);

        // Act
        var bytes = serializer.Serialize(request, "opaque-test-credential", metadata, null);
        var decoded = serializer.DeserializeEnvelope(bytes);
        var root = System.Text.Json.Nodes.JsonNode.Parse(bytes.Span)!;
        var payload = root["payload"]!.AsObject();
        var member = Assert.Single(payload).Key;
        payload[member] = Uuid.CreateVersion4().ToString();
        var tampered = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());

        // Assert
        Assert.Equal(request.CommandId, decoded.Metadata.RequestId);
        Assert.Equal(request.CommandId, Assert.IsType<IdentifiedCommand>(decoded.Request).CommandId);
        Assert.Throws<ArgumentException>(() => serializer.Serialize(request, null,
            new RequestMetadata(Uuid.CreateVersion4(), metadata.CorrelationId), null));
        Assert.Throws<InvalidOperationException>(() => serializer.DeserializeEnvelope(tampered));
    }

    /// <summary>Stable typed identity survives retries while each dispatch starts a new execution.</summary>
    [Fact]
    public async Task ShouldReuseLogicalIdentityAndCreateFreshExecutionsGivenExactRequestRetry()
    {
        // Arrange
        var handler = new IdentifiedQueryHandler();
        using var provider = Compose(handler, new IdentifiedCommandHandler(), new IdentityAuthorizer());
        using var scope = provider.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
        var request = new IdentifiedQuery(Uuid.CreateVersion4());

        // Act
        var first = await bus.SendAsync(request, RequestActor.System);
        var retry = await bus.SendAsync(request, RequestActor.System);

        // Assert
        Assert.True(first.IsSuccess);
        Assert.True(retry.IsSuccess);
        Assert.Equal(request.CommandId, first.Value);
        Assert.Equal(first.Value, retry.Value);
        Assert.Equal(2, handler.Executions.Distinct().Count());
    }

    /// <summary>Binding changes logical metadata without changing receiver execution or time.</summary>
    [Fact]
    public void ShouldPreserveExecutionAndClockGivenTypedIdentityBinding()
    {
        // Arrange
        var request = new IdentifiedCommand(Uuid.CreateVersion4());
        var context = new RequestDispatchContext(RequestActor.System);

        // Act
        var binding = context.BindRequest(request);

        // Assert
        Assert.True(binding.IsSuccess);
        Assert.Equal(request.CommandId, binding.Value.RequestId);
        Assert.Equal(request.CommandId, binding.Value.CorrelationId);
        Assert.Equal(context.ExecutionId, binding.Value.ExecutionId);
        Assert.Equal(context.StartedAt, binding.Value.StartedAt);
        Assert.Same(context.Invocation, binding.Value.Invocation);
        Assert.True(RequestActor.IsSystem(binding.Value.Actor));
    }

    /// <summary>Invalid explicit identities fail validation before authorization or handler work.</summary>
    [Fact]
    public async Task ShouldRejectEmptyOrConflictingIdentityGivenDirectDispatch()
    {
        // Arrange
        var handler = new IdentifiedCommandHandler();
        var authorization = new IdentityAuthorizer();
        using var provider = Compose(new IdentifiedQueryHandler(), handler, authorization);
        using var scope = provider.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
        var request = new IdentifiedCommand(Uuid.CreateVersion4());
        var wrong = new RequestDispatchContext(RequestActor.System,
            metadata: new RequestMetadata(Uuid.CreateVersion4(), Uuid.CreateVersion4()));

        // Act
        var empty = await bus.SendAsync(new IdentifiedCommand(Uuid.Empty), RequestActor.System);
        var mismatch = await bus.DispatchAsync(request, wrong);
        var emptyQuery = await bus.SendAsync(new IdentifiedQuery(Uuid.Empty), RequestActor.System);

        // Assert
        Assert.Equal(RequestErrorKind.Validation, empty.Error!.Kind);
        Assert.Equal(RequestErrorKind.Validation, mismatch.Error!.Kind);
        Assert.Equal(RequestErrorKind.Validation, emptyQuery.Error!.Kind);
        Assert.Empty(handler.Contexts);
        Assert.Equal(0, authorization.Calls);
    }

    /// <summary>A valid bound identity still passes through the declared authorizer.</summary>
    [Fact]
    public async Task ShouldAuthorizeBeforeHandlerGivenBoundTypedIdentity()
    {
        // Arrange
        var handler = new IdentifiedCommandHandler();
        var authorization = new IdentityAuthorizer { Allowed = false };
        using var provider = Compose(new IdentifiedQueryHandler(), handler, authorization);
        using var scope = provider.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
        var request = new IdentifiedCommand(Uuid.CreateVersion4());

        // Act
        var result = await bus.SendAsync(request, RequestActor.System);

        // Assert
        Assert.Equal(RequestErrorKind.Forbidden, result.Error!.Kind);
        Assert.Equal(request.CommandId, authorization.LastRequestId);
        Assert.Equal(1, authorization.Calls);
        Assert.Empty(handler.Contexts);
    }

    /// <summary>Repeated reactions preserve the selected effect identity and source event lineage.</summary>
    [Fact]
    public async Task ShouldPreserveEffectIdentityAndCausationGivenRepeatedReaction()
    {
        // Arrange
        var handler = new IdentifiedCommandHandler();
        using var provider = Compose(new IdentifiedQueryHandler(), handler, new IdentityAuthorizer());
        using var scope = provider.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
        var ev = new ValueChanged(7);
        ev.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), Uuid.CreateVersion4(), 1,
            DateTimeOffset.UtcNow, Uuid.CreateVersion4()));
        var source = new DomainEventRecord(new EventStreamAddress("realm", "events", "source"), ev, 0,
            new EventCursor("1"));
        var command = new IdentifiedCommand(Uuid.CreateVersion4());

        // Act
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var execution = new ReactionExecutionContext(source, RequestActor.System);
            var reaction = new ReactorContext<ValueChanged>(ev, source, execution);
            await bus.SendReactionAsync(command, reaction);
        }

        // Assert
        Assert.Equal(2, handler.Contexts.Count);
        Assert.All(handler.Contexts, context =>
        {
            Assert.Equal(command.CommandId, context.RequestId);
            Assert.Equal(ev.Metadata.CorrelationId, context.CorrelationId);
            Assert.Equal(ev.Metadata.EventId, context.CausationId);
        });
        Assert.Equal(2, handler.Contexts.Select(context => context.ExecutionId).Distinct().Count());
    }

    /// <summary>Carried metadata must agree with the typed request identity.</summary>
    [Fact]
    public void ShouldValidateCarriedIdentityGivenExplicitMetadata()
    {
        // Arrange
        var request = new IdentifiedCommand(Uuid.CreateVersion4());
        var parent = new RequestDispatchContext(RequestActor.System);

        // Act
        var metadata = RequestMetadata.FromRequest(request, parent);
        metadata.Validate(request);
        var wrong = metadata with { RequestId = Uuid.CreateVersion4() };

        // Assert
        Assert.Equal(request.CommandId, metadata.RequestId);
        Assert.Equal(parent.CorrelationId, metadata.CorrelationId);
        Assert.Equal(parent.CauseId, metadata.CausationId);
        Assert.Throws<ArgumentException>(() => wrong.Validate(request));
        Assert.Throws<ArgumentException>(() => RequestMetadata.FromRequest(new IdentifiedCommand(Uuid.Empty)));
    }

    internal static ServiceProvider Compose(IdentifiedQueryHandler query, IdentifiedCommandHandler command,
        IdentityAuthorizer authorization)
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton(query);
        _ = services.AddSingleton(command);
        _ = services.AddSingleton(authorization);
        var serializer = TestJson.Serializer(typeof(IdentifiedCommand));
        _ = services.AddSingleton<IRequestDeserializer>(serializer);
        _ = services.AddSingleton<IRequestOutcomeSerializer>(serializer);
        _ = services.AddSingleton<IRequestActorValidator>(new IdentityActorValidator());
        _ = services.AddPortia().AddRequestHandler<IdentifiedQueryHandler>()
            .AddRequestHandler<IdentifiedCommandHandler>().AddRequestAuthorizer<IdentityAuthorizer>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Discriminator("test.identity.query", 1)]
    internal sealed record IdentifiedQuery(Uuid CommandId) : IRequest<Uuid>, IIdentifiedRequest, ICallable
    {
        Uuid IIdentifiedRequest.RequestId => CommandId;
    }

    [Discriminator("test.identity.command", 1)]
    [RequestRoute("test", "identity", "command", "apply")]
    internal sealed record IdentifiedCommand(Uuid CommandId) : IRequest, IIdentifiedRequest, ICallable, IQueuable,
        ISchedulable, INotifiable
    {
        Uuid IIdentifiedRequest.RequestId => CommandId;
    }

    internal sealed class IdentifiedQueryHandler : IRequestHandler<IdentifiedQuery, Uuid>
    {
        public List<Uuid> Executions { get; } = [];
        public ValueTask<Result<Uuid>> HandleAsync(IRequestContext<IdentifiedQuery> context, CancellationToken ct)
        {
            Executions.Add(context.ExecutionId);
            return ValueTask.FromResult(Result<Uuid>.Success(context.RequestId));
        }
    }

    internal sealed class IdentifiedCommandHandler : IRequestHandler<IdentifiedCommand>
    {
        public List<IRequestContext<IdentifiedCommand>> Contexts { get; } = [];
        public ValueTask<Result> HandleAsync(IRequestContext<IdentifiedCommand> context, CancellationToken ct)
        {
            Contexts.Add(context);
            return ValueTask.FromResult(Result.Success);
        }
    }

    sealed class IdentityActorValidator : IRequestActorValidator
    {
        public ValueTask<Result<System.Security.Claims.ClaimsPrincipal>> ValidateAsync(string? token,
            CancellationToken ct = default) =>
            ValueTask.FromResult(Result<System.Security.Claims.ClaimsPrincipal>.Success(RequestActor.System));
    }

    internal sealed class IdentityAuthorizer : IRequestAuthorizer<IdentifiedCommand>
    {
        public bool Allowed { get; init; } = true;
        public int Calls { get; private set; }
        public Uuid LastRequestId { get; private set; }
        public ValueTask<Result> AuthorizeAsync(IRequestContext<IdentifiedCommand> context, CancellationToken ct)
        {
            Calls++;
            LastRequestId = context.RequestId;
            return ValueTask.FromResult(Allowed ? Result.Success :
                Result.Failure(new RequestError(RequestErrorKind.Forbidden, "Denied")));
        }
    }
}
