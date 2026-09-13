using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cntryl.Portia;

sealed class PortiaMcpServerTool(McpToolRegistration registration, JsonSerializerOptions json) : McpServerTool
{
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
        var invocation = new McpInvocation(registration.Name);
        using var receive = PortiaTelemetry.StartProcess(registration.Name, invocation,
            PortiaTelemetry.CaptureTraceContext());
        try
        {
            var result = await registration.InvokeAsync(request, json, cancellationToken).ConfigureAwait(false);
            PortiaTelemetry.RecordOutcome(receive, result.IsError != true, null);
            return result;
        }
        catch (JsonException)
        {
            PortiaTelemetry.RecordOutcome(receive, false, null);
            return McpToolRegistration.IngressFailure("Binding", "The tool input is not valid.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("actor", StringComparison.OrdinalIgnoreCase)
                                                           || exception.Message.Contains("principal", StringComparison.OrdinalIgnoreCase))
        {
            PortiaTelemetry.RecordOutcome(receive, false, null);
            return McpToolRegistration.IngressFailure("Unauthorized", "An authenticated actor is required.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = receive?.SetTag("portia.outcome", "canceled");
            throw;
        }
        catch (Exception)
        {
            _ = receive?.SetTag("portia.outcome", "fault");
            return McpToolRegistration.IngressFailure("Internal", "The tool could not be completed.");
        }
    }
}
