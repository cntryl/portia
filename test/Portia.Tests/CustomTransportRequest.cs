namespace Cntryl.Portia;

/// <summary>Custom userland transport marker used to prove generator extensibility.</summary>
[RequestTransport("custom-adapter")]
public interface ICustomAdapterRequest;

/// <summary>Request reachable only through the custom userland transport.</summary>
[RequestRoute("test", "custom", "requests", "send")]
[Discriminator("test.custom.request")]
public sealed record CustomTransportRequest : IRequest, ICustomAdapterRequest;

/// <summary>Registers the custom request through the normal generated handler path.</summary>
public sealed class CustomTransportRequestHandler : IRequestHandler<CustomTransportRequest>
{
    /// <inheritdoc />
    public ValueTask<Result> HandleAsync(IRequestContext<CustomTransportRequest> context,
        CancellationToken ct) => ValueTask.FromResult(Result.Success);
}
