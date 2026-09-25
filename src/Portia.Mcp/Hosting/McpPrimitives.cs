using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cntryl.Portia;

/// <summary>Decides whether an MCP catalog entry is visible to an actor.</summary>
public interface IMcpVisibilityPolicy
{
    /// <summary>Checks visibility for this invocation.</summary>
    ValueTask<bool> IsVisibleAsync(ClaimsPrincipal actor, CancellationToken cancellationToken);
}

/// <summary>Limits work accepted at MCP ingress.</summary>
public sealed class McpLimits
{
    /// <summary>Maximum UTF-8 bytes in a resource URI.</summary>
    public int MaxResourceUriBytes { get; set; } = 2048;
    /// <summary>Maximum UTF-8 bytes in prompt arguments.</summary>
    public int MaxPromptArgumentBytes { get; set; } = 65536;
    /// <summary>Maximum number of rendered prompt messages.</summary>
    public int MaxPromptMessages { get; set; } = 16;
    /// <summary>Maximum serialized result bytes.</summary>
    public int MaxResultBytes { get; set; } = 1048576;
    /// <summary>Maximum operation duration.</summary>
    public TimeSpan OperationDeadline { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>A Portia-owned prompt message.</summary>
public sealed record McpPromptMessage(string Role, string Text);

/// <summary>Configures visibility for a resource or prompt.</summary>
public abstract class McpEntryOptions
{
    internal McpAccess Access { get; private set; }
    internal Type? PolicyType { get; private set; }
    /// <summary>Allows authenticated actors to discover and invoke the entry.</summary>
    public void Authenticated() => Access = McpAccess.Authenticated;
    /// <summary>Explicitly exposes the entry to anonymous actors.</summary>
    public void Public() => Access = McpAccess.Public;
    /// <summary>Uses a registered policy for discovery and invocation.</summary>
    public void VisibleTo<TPolicy>() where TPolicy : IMcpVisibilityPolicy
    {
        Access = McpAccess.Policy;
        PolicyType = typeof(TPolicy);
    }
}

enum McpAccess { Unset, Public, Authenticated, Policy }

/// <summary>Configures a resource's output and visibility.</summary>
public sealed class McpResourceOptions<TOut> : McpEntryOptions
{
    internal Func<TOut, byte[]>? Binary { get; private set; }
    internal Func<TOut, string>? Text { get; private set; }
    internal bool Json { get; private set; }
    internal string? MediaType { get; private set; }
    /// <summary>Serializes the result using Portia's JSON configuration.</summary>
    public void AsJson()
    {
        SetMediaType("application/json");
        Json = true;
    }
    /// <summary>Projects the result to text.</summary>
    public void AsText(string mimeType, Func<TOut, string> project)
    {
        ArgumentNullException.ThrowIfNull(project);
        SetMediaType(mimeType);
        Text = project;
    }
    /// <summary>Projects the result to binary data.</summary>
    public void AsBinary(string mimeType, Func<TOut, byte[]> project)
    {
        ArgumentNullException.ThrowIfNull(project);
        SetMediaType(mimeType);
        Binary = project;
    }

    void SetMediaType(string mimeType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        if (mimeType.Length > 128 || mimeType.Any(character => character < 0x21 || character > 0x7e))
            throw new ArgumentException("MIME type must be printable ASCII and at most 128 characters.", nameof(mimeType));
        if (MediaType is not null)
            throw new InvalidOperationException("Choose one resource projection.");
        MediaType = mimeType;
    }
}

/// <summary>Configures prompt arguments and visibility.</summary>
public sealed class McpPromptOptions : McpEntryOptions
{
    internal Dictionary<string, bool> Arguments { get; } = new(StringComparer.Ordinal);
    /// <summary>Declares a required string argument.</summary>
    public void Required(string name) => Add(name, true);
    /// <summary>Declares an optional string argument.</summary>
    public void Optional(string name) => Add(name, false);
    void Add(string name, bool required)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Arguments.TryAdd(name, required))
            throw new ArgumentException("Duplicate prompt argument.", nameof(name));
    }
}

abstract class McpEntry(Type requestType, McpEntryOptions options)
{
    internal Type RequestType { get; } = requestType;
    internal McpEntryOptions Options { get; } = options;
    internal async ValueTask<bool> VisibleAsync(IServiceProvider services, ClaimsPrincipal actor, CancellationToken ct)
    {
        try
        {
            return Options.Access switch
            {
                McpAccess.Public => true,
                McpAccess.Authenticated => actor.Identity?.IsAuthenticated == true,
                McpAccess.Policy => await ((IMcpVisibilityPolicy)services.GetRequiredService(Options.PolicyType!))
                    .IsVisibleAsync(actor, ct).ConfigureAwait(false),
                _ => false
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

abstract class McpResourceEntry(Type requestType, McpEntryOptions options, string template)
    : McpEntry(requestType, options)
{
    internal string Template { get; } = template;
    internal bool IsTemplate => Template.Contains('{', StringComparison.Ordinal);
    internal abstract string MimeType { get; }
    internal abstract ValueTask<ReadResourceResult> ReadAsync(IReadOnlyDictionary<string, string> values,
        string uri, IServiceProvider services, ClaimsPrincipal actor, JsonSerializerOptions json, CancellationToken ct);

    internal bool Match(string uri, out Dictionary<string, string> values)
    {
        values = new(StringComparer.Ordinal);
        if (!McpUri.TryValidate(uri))
            return false;
        var candidate = uri.Split('/');
        var pattern = Template.Split('/');
        if (candidate.Length != pattern.Length)
            return false;
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i].Length > 2 && pattern[i][0] == '{' && pattern[i][^1] == '}')
            {
                var decoded = Uri.UnescapeDataString(candidate[i]);
                if (decoded.Length == 0 || decoded is "." or "..")
                    return false;
                values.Add(pattern[i][1..^1], decoded);
            }
            else if (!string.Equals(pattern[i], candidate[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}

sealed class McpResourceEntry<TRequest, TOut>(string template,
    Func<IReadOnlyDictionary<string, string>, TRequest> bind, McpResourceOptions<TOut> options)
    : McpResourceEntry(typeof(TRequest), options, template)
    where TRequest : IRequest<TOut>, ICallable
{
    internal override string MimeType => options.MediaType!;
    internal override async ValueTask<ReadResourceResult> ReadAsync(IReadOnlyDictionary<string, string> values,
        string uri, IServiceProvider services, ClaimsPrincipal actor, JsonSerializerOptions json, CancellationToken ct)
    {
        var request = bind(values);
        var bus = services.GetRequiredService<IRequestBus>();
        var result = await bus.DispatchAsync(request,
            new RequestDispatchContext(actor, new McpResourceInvocation(Template),
                timeProvider: services.GetService<TimeProvider>()), ct).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new McpException("Resource unavailable.");
        ResourceContents content;
        if (options.Binary is { } binary)
            content = BlobResourceContents.FromBytes(binary(result.Value), uri, MimeType);
        else
        {
            var text = options.Text is { } projection ? projection(result.Value)
                : JsonSerializer.Serialize(result.Value, json.GetTypeInfo(typeof(TOut)));
            content = new TextResourceContents { Uri = uri, MimeType = MimeType, Text = text };
        }
        return new ReadResourceResult
        {
            Contents = [content],
            TimeToLive = TimeSpan.Zero,
            CacheScope = CacheScope.Private
        };
    }
}

abstract class McpPromptEntry(Type requestType, McpPromptOptions options, string name)
    : McpEntry(requestType, options)
{
    internal string Name { get; } = name;
    internal McpPromptOptions PromptOptions { get; } = options;
    internal abstract ValueTask<GetPromptResult> GetAsync(IReadOnlyDictionary<string, string> values,
        IServiceProvider services, ClaimsPrincipal actor, CancellationToken ct);
}

sealed class McpPromptEntry<TRequest, TOut>(string name,
    Func<IReadOnlyDictionary<string, string>, TRequest> bind,
    Func<TOut, IReadOnlyList<McpPromptMessage>> render, McpPromptOptions options)
    : McpPromptEntry(typeof(TRequest), options, name)
    where TRequest : IRequest<TOut>, ICallable
{
    internal override async ValueTask<GetPromptResult> GetAsync(IReadOnlyDictionary<string, string> values,
        IServiceProvider services, ClaimsPrincipal actor, CancellationToken ct)
    {
        var request = bind(values);
        var result = await services.GetRequiredService<IRequestBus>().DispatchAsync(request,
            new RequestDispatchContext(actor, new McpPromptInvocation(Name),
                timeProvider: services.GetService<TimeProvider>()), ct).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new McpException("Prompt unavailable.");
        var messages = render(result.Value);
        if (messages.Count > services.GetRequiredService<McpLimits>().MaxPromptMessages)
            throw new McpException("Prompt unavailable.");
        if (messages.Any(message => message.Role is not ("user" or "assistant") || message.Text is null))
            throw new McpException("Prompt unavailable.");
        return new GetPromptResult
        {
            Messages = messages.Select(message => new PromptMessage
            {
                Role = message.Role == "assistant" ? Role.Assistant : Role.User,
                Content = new TextContentBlock { Text = message.Text }
            }).ToArray()
        };
    }
}

static class McpUri
{
    internal static void ValidateTemplate(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        var authorityEnd = template.IndexOf('/', template.IndexOf("://", StringComparison.Ordinal) + 3);
        if (template.Contains('{', StringComparison.Ordinal) &&
            (authorityEnd < 0 || template[..authorityEnd].Contains('{', StringComparison.Ordinal)))
            throw new ArgumentException("Resource placeholders must be path segments.", nameof(template));
        if (!TryValidate(template.Replace("{", "x", StringComparison.Ordinal).Replace("}", "", StringComparison.Ordinal)))
            throw new ArgumentException("Resource URI template must be an absolute URI without query or fragment.", nameof(template));
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in template.Split('/'))
        {
            if (!segment.Contains('{', StringComparison.Ordinal) && !segment.Contains('}', StringComparison.Ordinal))
                continue;
            if (segment.Length < 3 || segment[0] != '{' || segment[^1] != '}'
                || !names.Add(segment[1..^1]) || !segment[1..^1].All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                throw new ArgumentException("Resource placeholders must be unique whole path segments.", nameof(template));
        }
    }

    internal static bool TryValidate(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || uri.Contains('?', StringComparison.Ordinal)
            || uri.Contains('#', StringComparison.Ordinal) || uri.Contains('%') && !ValidEscapes(uri))
            return false;
        var pathStart = uri.IndexOf('/', uri.IndexOf("://", StringComparison.Ordinal) + 3);
        if (pathStart >= 0 && uri[pathStart..].Split('/').Any(segment => segment is "." or "..")
            || parsed.UserInfo.Length != 0)
            return false;
        return !Uri.UnescapeDataString(uri).Contains('\uFFFD', StringComparison.Ordinal);
    }

    static bool ValidEscapes(string uri)
    {
        for (var i = 0; i < uri.Length; i++)
        {
            if (uri[i] != '%')
                continue;
            if (i + 2 >= uri.Length || !Uri.IsHexDigit(uri[i + 1]) || !Uri.IsHexDigit(uri[i + 2]))
                return false;
            var value = Convert.ToByte(uri.Substring(i + 1, 2), 16);
            if (value is 0x2f or 0x5c or 0x2e or 0x25)
                return false;
            i += 2;
        }
        return true;
    }
}
