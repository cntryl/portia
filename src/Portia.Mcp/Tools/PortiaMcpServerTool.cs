using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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

    static readonly CallToolResult ConcurrencyFailure = McpToolRegistration.IngressFailure(
        "Conflict", "The request conflicted with a concurrent update.", true);

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
        var limits = request.Services?.GetRequiredService<McpLimits>() ?? new McpLimits();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limits.OperationDeadline);
        using var receive = PortiaTelemetry.StartProcess(registration.RequestName, registration.Invocation,
            PortiaTelemetry.CaptureTraceContext());
        try
        {
            var result = await registration.InvokeAsync(request, json, deadline.Token).ConfigureAwait(false);
            var size = result.Result.StructuredContent is { } structured
                ? Encoding.UTF8.GetByteCount(structured.GetRawText()) + 128L : 128L;
            foreach (var block in result.Result.Content.OfType<TextContentBlock>())
                size += JsonEncodedText.Encode(block.Text).EncodedUtf8Bytes.Length + 64L;
            if (result.Result.Meta is { } meta)
                size += Encoding.UTF8.GetByteCount(meta.ToJsonString());
            if (size > limits.MaxResultBytes)
                throw new InvalidOperationException("MCP result exceeds the configured output limit.");
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
        catch (EventStreamConcurrencyException)
        {
            PortiaTelemetry.RecordOutcome(receive, false,
                new RequestError(RequestErrorKind.Conflict,
                    "The request conflicted with a concurrent update.", true));
            return ConcurrencyFailure;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = receive?.SetTag("portia.outcome", "canceled");
            throw;
        }
        catch (OperationCanceledException)
        {
            PortiaTelemetry.RecordOutcome(receive, false, null);
            return InternalFailure;
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
