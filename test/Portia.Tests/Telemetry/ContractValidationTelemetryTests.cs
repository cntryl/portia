using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Validation preserves dispatch cancellation telemetry.</summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class ContractValidationTelemetryTests
{
    /// <summary>Cancellation while reading a validation value is a canceled stream outcome.</summary>
    [Fact]
    public async Task ShouldRecordCanceledGivenCancellationDuringStreamValidation()
    {
        // Arrange
        var captured = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PortiaTelemetry.SourceName,
            Sample = static (ref options) => ActivitySamplingResult.AllData,
            ActivityStopped = captured.Add
        };
        ActivitySource.AddActivityListener(listener);
        using var cancellation = new CancellationTokenSource();
        var services = new ServiceCollection();
        _ = services.AddPortia().AddRequestHandler<ContractCanceledValidationHandler>();
        await using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IRequestBus>();

        // Act
        var exception = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in bus.StreamAsync(new ContractCanceledValidationStream(cancellation),
                               RequestActor.System, cancellation.Token))
            {
            }
        });

        // Assert
        _ = Assert.IsAssignableFrom<OperationCanceledException>(exception);
        var activity = Assert.Single(captured,
            item => item.GetTagItem("portia.request.name") as string == nameof(ContractCanceledValidationStream));
        Assert.Equal("canceled", activity.GetTagItem("portia.outcome"));
    }
}

sealed record ContractCanceledValidationStream(CancellationTokenSource Cancellation) : IStreamRequest<int>
{
    [Required]
    public string Name
    {
        get
        {
            Cancellation.Cancel();
            return "valid";
        }
    }
}

sealed class ContractCanceledValidationHandler : IStreamRequestHandler<ContractCanceledValidationStream, int>
{
    public IAsyncEnumerable<int> HandleAsync(IRequestContext<ContractCanceledValidationStream> context,
        CancellationToken ct) =>
        throw new InvalidOperationException("A canceled request must not invoke its handler.");
}
