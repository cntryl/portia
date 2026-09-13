using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cntryl.Portia;

sealed class PortiaMcpServerTool(
    McpToolRegistration registration,
    JsonSerializerOptions json,
    ILogger<PortiaMcpServerTool> logger) : McpServerTool
{
    static readonly CallToolResult BindingFailure = McpToolRegistration.IngressFailure(
        "Binding", "The tool input is not valid.");
    static readonly CallToolResult UnauthorizedFailure = McpToolRegistration.IngressFailure(
        "Unauthorized", "An authenticated actor is required.");
    static readonly CallToolResult InternalFailure = McpToolRegistration.IngressFailure(
        "Internal", "The tool could not be completed.");

    public override Tool ProtocolTool { get; } = registration.CreateProtocolTool(json);

    public override IReadOnlyList<object> Metadata => [];

    public override ValueTask<CallToolResult> InvokeAsync(
        ModelContextProtocol.Server.RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
        => InvokeCoreAsync(request, cancellationToken);

    async ValueTask<CallToolResult> InvokeCoreAsync(
        ModelContextProtocol.Server.RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken)
    {
        using var receive = PortiaTelemetry.StartProcess(registration.RequestName, registration.Invocation,
            PortiaTelemetry.CaptureTraceContext());
        try
        {
            var result = await registration.InvokeAsync(request, json, cancellationToken).ConfigureAwait(false);
            PortiaTelemetry.RecordOutcome(receive, result.Result.IsError != true, result.Error);
            return result.Result;
        }
        catch (McpBindingException)
        {
            PortiaTelemetry.RecordOutcome(receive, false,
                new RequestError(RequestErrorKind.Validation, "The tool input is not valid."));
            return BindingFailure;
        }
        catch (McpActorRequiredException)
        {
            PortiaTelemetry.RecordOutcome(receive, false,
                new RequestError(RequestErrorKind.Unauthorized, "An authenticated actor is required."));
            return UnauthorizedFailure;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = receive?.SetTag("portia.outcome", "canceled");
            throw;
        }
        catch (Exception exception)
        {
            PortiaTelemetry.RecordOutcome(receive, false, null);
            PortiaTelemetry.RecordRunnerFault(nameof(PortiaMcpServerTool), RunnerFaultStage.Execution,
                exception, logger);
            return InternalFailure;
        }
    }
}
