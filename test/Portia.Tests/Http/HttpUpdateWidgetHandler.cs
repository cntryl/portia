using System.Globalization;

namespace Cntryl.Portia;

sealed class HttpUpdateWidgetHandler : IRequestHandler<HttpUpdateWidget, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<HttpUpdateWidget> context, CancellationToken ct)
    {
        var request = context.Request;
        return ValueTask.FromResult(Result<string>.Success(
            $"{request.WidgetId} {request.Name} {request.Quantity} {request.Active} {request.Priority?.ToString(CultureInfo.InvariantCulture) ?? "none"}{(request.DryRun ? " dry-run" : string.Empty)}"));
    }
}
