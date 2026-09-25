using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cntryl.Portia;

static class McpPrimitiveHandlers
{
    internal static void Attach(IMcpServerBuilder server, IServiceCollection services)
    {
        if (services.Any(service => service.ServiceType == typeof(McpResourceEntry)))
        {
            _ = server.WithListResourcesHandler(ListResourcesAsync)
                .WithListResourceTemplatesHandler(ListTemplatesAsync)
                .WithReadResourceHandler(ReadResourceAsync);
        }
        if (services.Any(service => service.ServiceType == typeof(McpPromptEntry)))
            _ = server.WithListPromptsHandler(ListPromptsAsync).WithGetPromptHandler(GetPromptAsync);
    }

    internal static void MarkTransportActivated(IServiceCollection services, string transport) =>
        services.AddSingleton(new TransportActivation(transport));

    internal static void RequireDeclarationsBeforeTransport(IServiceCollection services)
    {
        var activation = services.FirstOrDefault(service => service.ServiceType == typeof(TransportActivation))
            ?.ImplementationInstance as TransportActivation;
        if (activation is not null)
            throw new InvalidOperationException(
                $"MCP resources and prompts must be declared before {activation.Transport}().");
    }

    sealed record TransportActivation(string Transport);

    static async ValueTask<ListResourcesResult> ListResourcesAsync(
        ModelContextProtocol.Server.RequestContext<ListResourcesRequestParams> call, CancellationToken ct)
    {
        var services = Services(call);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(services.GetRequiredService<McpLimits>().OperationDeadline);
        var actor = await ActorAsync(call, deadline.Token).ConfigureAwait(false);
        var visible = new List<Resource>();
        foreach (var entry in services.GetServices<McpResourceEntry>().Where(entry => !entry.IsTemplate))
            if (await entry.VisibleAsync(services, actor, deadline.Token).ConfigureAwait(false))
                visible.Add(new Resource { Name = entry.Template, Uri = entry.Template, MimeType = entry.MimeType });
        return new ListResourcesResult
        {
            Resources = visible,
            TimeToLive = TimeSpan.Zero,
            CacheScope = CacheScope.Private
        };
    }

    static async ValueTask<ListResourceTemplatesResult> ListTemplatesAsync(
        ModelContextProtocol.Server.RequestContext<ListResourceTemplatesRequestParams> call, CancellationToken ct)
    {
        var services = Services(call);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(services.GetRequiredService<McpLimits>().OperationDeadline);
        var actor = await ActorAsync(call, deadline.Token).ConfigureAwait(false);
        var visible = new List<ResourceTemplate>();
        foreach (var entry in services.GetServices<McpResourceEntry>().Where(entry => entry.IsTemplate))
            if (await entry.VisibleAsync(services, actor, deadline.Token).ConfigureAwait(false))
                visible.Add(new ResourceTemplate
                {
                    Name = entry.Template,
                    UriTemplate = entry.Template,
                    MimeType = entry.MimeType
                });
        return new ListResourceTemplatesResult
        {
            ResourceTemplates = visible,
            TimeToLive = TimeSpan.Zero,
            CacheScope = CacheScope.Private
        };
    }

    static async ValueTask<ReadResourceResult> ReadResourceAsync(
        ModelContextProtocol.Server.RequestContext<ReadResourceRequestParams> call, CancellationToken ct)
    {
        var services = Services(call);
        var limits = services.GetRequiredService<McpLimits>();
        var uri = call.Params.Uri;
        if (uri is null || Encoding.UTF8.GetByteCount(uri) > limits.MaxResourceUriBytes || !McpUri.TryValidate(uri))
            throw new McpException("Resource unavailable.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limits.OperationDeadline);
        try
        {
            var actor = await ActorAsync(call, deadline.Token).ConfigureAwait(false);
            foreach (var entry in services.GetServices<McpResourceEntry>())
            {
                if (!entry.Match(uri, out var values) ||
                    !await entry.VisibleAsync(services, actor, deadline.Token).ConfigureAwait(false))
                    continue;
                var json = services.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json);
                var result = await entry.ReadAsync(values, uri, services, actor, json, deadline.Token)
                    .ConfigureAwait(false);
                var size = result.Contents.Sum(content => content switch
                {
                    TextResourceContents text => JsonEncodedText.Encode(text.Text).EncodedUtf8Bytes.Length
                                                 + Encoding.UTF8.GetByteCount(text.Uri) + 128L,
                    BlobResourceContents blob => ((long)blob.DecodedData.Length + 2) / 3 * 4
                                                 + Encoding.UTF8.GetByteCount(blob.Uri) + 128,
                    _ => long.MaxValue
                });
                if (size > limits.MaxResultBytes)
                    throw new McpException("Resource unavailable.");
                return result;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new McpException("Resource unavailable.");
        }
        catch (McpException) { throw; }
        catch (Exception) { throw new McpException("Resource unavailable."); }
        throw new McpException("Resource unavailable.");
    }

    static async ValueTask<ListPromptsResult> ListPromptsAsync(
        ModelContextProtocol.Server.RequestContext<ListPromptsRequestParams> call, CancellationToken ct)
    {
        var services = Services(call);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(services.GetRequiredService<McpLimits>().OperationDeadline);
        var actor = await ActorAsync(call, deadline.Token).ConfigureAwait(false);
        var visible = new List<Prompt>();
        foreach (var entry in services.GetServices<McpPromptEntry>())
            if (await entry.VisibleAsync(services, actor, deadline.Token).ConfigureAwait(false))
                visible.Add(new Prompt
                {
                    Name = entry.Name,
                    Arguments = entry.PromptOptions.Arguments
                    .Select(argument => new PromptArgument { Name = argument.Key, Required = argument.Value }).ToArray()
                });
        return new ListPromptsResult
        {
            Prompts = visible,
            TimeToLive = TimeSpan.Zero,
            CacheScope = CacheScope.Private
        };
    }

    static async ValueTask<GetPromptResult> GetPromptAsync(
        ModelContextProtocol.Server.RequestContext<GetPromptRequestParams> call, CancellationToken ct)
    {
        var services = Services(call);
        var limits = services.GetRequiredService<McpLimits>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limits.OperationDeadline);
        try
        {
            var actor = await ActorAsync(call, deadline.Token).ConfigureAwait(false);
            var entry = services.GetServices<McpPromptEntry>().FirstOrDefault(prompt => prompt.Name == call.Params.Name);
            if (entry is null || !await entry.VisibleAsync(services, actor, deadline.Token).ConfigureAwait(false))
                throw new McpException("Prompt unavailable.");
            var input = call.Params.Arguments ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (input.Any(argument => !entry.PromptOptions.Arguments.ContainsKey(argument.Key)) ||
                entry.PromptOptions.Arguments.Any(argument => argument.Value && !input.ContainsKey(argument.Key)))
                throw new McpException("Prompt unavailable.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            long bytes = 0;
            foreach (var argument in input)
            {
                if (argument.Value.ValueKind != JsonValueKind.String)
                    throw new McpException("Prompt unavailable.");
                var value = argument.Value.GetString()!;
                bytes += Encoding.UTF8.GetByteCount(argument.Key)
                         + Encoding.UTF8.GetByteCount(argument.Value.GetRawText()) + 4;
                if (bytes > limits.MaxPromptArgumentBytes)
                    throw new McpException("Prompt unavailable.");
                values.Add(argument.Key, value);
            }
            var result = await entry.GetAsync(values, services, actor, deadline.Token).ConfigureAwait(false);
            var size = result.Messages.Sum(message => message.Content is TextContentBlock text
                ? JsonEncodedText.Encode(text.Text).EncodedUtf8Bytes.Length + 128L : long.MaxValue);
            if (size > limits.MaxResultBytes)
                throw new McpException("Prompt unavailable.");
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new McpException("Prompt unavailable.");
        }
        catch (McpException) { throw; }
        catch (Exception) { throw new McpException("Prompt unavailable."); }
    }

    static IServiceProvider Services(MessageContext call) =>
        call.Services ?? throw new InvalidOperationException("The MCP request has no service scope.");

    internal static async ValueTask<ClaimsPrincipal> ActorAsync(MessageContext call, CancellationToken ct)
    {
        if (call.User is not null)
            return call.User;
        if (PortiaMcpHttpMarker.IsCurrentRequest)
            return new ClaimsPrincipal(new ClaimsIdentity());
        var provider = Services(call).GetService<IMcpActorProvider>()
            ?? throw new McpActorRequiredException("MCP ingress has no actor provider.");
        return await provider.GetActorAsync(ct).ConfigureAwait(false)
            ?? throw new McpActorRequiredException("MCP ingress has no actor.");
    }
}
