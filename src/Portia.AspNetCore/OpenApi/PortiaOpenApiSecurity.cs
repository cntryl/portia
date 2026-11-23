using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Cntryl.Portia;

public sealed partial class PortiaOpenApiOptions
{
    readonly Dictionary<string, IReadOnlyDictionary<string, string[]>[]> _policySecurity = [with(StringComparer.Ordinal)];

    readonly Dictionary<Type, IReadOnlyDictionary<string, string[]>[]> _requestSecurity = [];
    IReadOnlyDictionary<string, string[]>[]? _defaultSecurity;
    IReadOnlyDictionary<string, string[]>[]? _fallbackSecurity;

    /// <summary>Gets HTTP security definitions, normally keyed by the actual authentication scheme name.</summary>
    public IDictionary<string, OpenApiSecurityScheme> SecuritySchemes { get; } =
        new Dictionary<string, OpenApiSecurityScheme>(StringComparer.Ordinal);

    /// <summary>Maps a named ASP.NET authorization policy to one credential scheme and explicit OAuth scopes.</summary>
    public PortiaOpenApiOptions SecurityPolicy(string policy, string scheme, params string[] scopes) =>
        SecurityPolicy(policy, new Dictionary<string, string[]> { [scheme] = scopes });

    /// <summary>Maps a named policy to OR alternatives; schemes within each dictionary are required together (AND).</summary>
    public PortiaOpenApiOptions SecurityPolicy(
        string policy,
        params IReadOnlyDictionary<string, string[]>[] alternatives
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policy);

        if (!_policySecurity.TryAdd(policy, Snapshot(alternatives)))
            throw new InvalidOperationException($"SecurityPolicy '{policy}' is mapped more than once.");

        return this;
    }

    /// <summary>Documents an independently enforced Portia request authorizer's credential scheme and explicit scopes.</summary>
    public PortiaOpenApiOptions SecurityFor<TRequest>(string scheme, params string[] scopes) =>
        SecurityFor<TRequest>(new Dictionary<string, string[]> { [scheme] = scopes });

    /// <summary>Documents a Portia request authorizer's OR alternatives, each containing ANDed credential schemes.</summary>
    public PortiaOpenApiOptions SecurityFor<TRequest>(params IReadOnlyDictionary<string, string[]>[] alternatives)
    {
        if (!_requestSecurity.TryAdd(typeof(TRequest), Snapshot(alternatives)))
            throw new InvalidOperationException($"SecurityFor '{typeof(TRequest)}' is mapped more than once.");

        return this;
    }

    /// <summary>Maps a custom ASP.NET default authorization policy to explicit credential alternatives.</summary>
    public PortiaOpenApiOptions SecurityDefault(params IReadOnlyDictionary<string, string[]>[] alternatives)
    {
        if (_defaultSecurity is not null)
            throw new InvalidOperationException("SecurityDefault is mapped more than once.");

        _defaultSecurity = Snapshot(alternatives);
        return this;
    }

    /// <summary>Maps a custom ASP.NET fallback authorization policy to explicit credential alternatives.</summary>
    public PortiaOpenApiOptions SecurityFallback(params IReadOnlyDictionary<string, string[]>[] alternatives)
    {
        if (_fallbackSecurity is not null)
            throw new InvalidOperationException("SecurityFallback is mapped more than once.");

        _fallbackSecurity = Snapshot(alternatives);
        return this;
    }

    internal async Task ApplySecurityAsync(
        OpenApiOperation operation,
        Type? requestType,
        IList<object> endpoint,
        IServiceProvider services,
        OpenApiDocument document,
        CancellationToken ct
    )
    {
        IReadOnlyDictionary<string, string[]>[] requirements = [new Dictionary<string, string[]>()];

        if (!endpoint.OfType<IAllowAnonymous>().Any())
        {
            var data = endpoint.OfType<IAuthorizeData>().ToArray();
            var policies = endpoint.OfType<AuthorizationPolicy>().ToArray();
            var provider = services.GetService<IAuthorizationPolicyProvider>();

            if (provider is null && (data.Length != 0 || policies.Length != 0))
                throw new InvalidOperationException(
                    "OpenAPI security requires AddAuthorization for protected endpoints.");

            var combined = provider is null
                ? null
                : await AuthorizationPolicy.CombineAsync(provider, data, policies).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (combined is not null)
            {
                var names = data.Select(item => item.Policy).Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal).ToArray();

                foreach (var name in names)
                    if (!_policySecurity.ContainsKey(name!))
                        throw new InvalidOperationException(
                            $"OpenAPI policy '{name}' needs an explicit SecurityPolicy mapping.");

                var unnamed = data.Where(item => string.IsNullOrWhiteSpace(item.Policy)).ToArray();

                if (names.Length == 0)
                {
                    var explicitMapping = data.Length == 0 && policies.Length == 0 ? _fallbackSecurity :
                        policies.Length == 0 && OnlyDefault(data) ? _defaultSecurity : null;
                    requirements = explicitMapping ?? await FromPolicy(combined).ConfigureAwait(false);
                }
                else if (names.Length == 1 && policies.Length == 0 && unnamed.Length == 0)
                {
                    requirements = _policySecurity[names[0]!];
                }
                else
                {
                    // ASP combines handler selections and checks requirements against their merged
                    // principal. Separate policy credential maps cannot generally be ANDed or ORed.
                    // Infer only the plain authenticated-user case with exact handler mappings.
                    var simple = combined.Requirements.All(item => item is DenyAnonymousAuthorizationRequirement);

                    foreach (var name in names)
                    {
                        var policy = await provider!.GetPolicyAsync(name!).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        var selected = policy!.AuthenticationSchemes;
                        var mapping = _policySecurity[name!];
                        simple &= selected.Count > 0 && mapping.All(alternative => alternative.Count == 1 &&
                                      alternative.All(item =>
                                          item.Value.Length == 0 &&
                                          selected.Contains(item.Key, StringComparer.Ordinal))) &&
                                  selected.All(scheme => mapping.Any(alternative => alternative.ContainsKey(scheme)));
                    }


                    if (unnamed.Length != 0 && OnlyDefault(unnamed) && _defaultSecurity is not null)
                        simple = false;

                    if (!simple)
                        throw new InvalidOperationException(
                            "OpenAPI cannot compose credential mappings for these authorization policies. Combine them into one named composite policy and describe its complete credential alternatives with SecurityPolicy.");

                    requirements = await FromPolicy(combined).ConfigureAwait(false);
                }

                // A policy mapping must cover explicitly selected authentication handlers too. Do not silently
                // drop them or invent an AND relationship: the host must describe the actual alternatives.

                foreach (var scheme in data.SelectMany(item =>
                             (item.AuthenticationSchemes ?? string.Empty).Split(',',
                                 StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
                    if (!requirements.Any(alternative => alternative.ContainsKey(scheme)))
                        throw new InvalidOperationException(
                            $"SecurityPolicy mappings omit explicit authentication scheme '{scheme}'. Include its credential alternatives in the mapping.");
            }
        }



        if (requestType is not null && _requestSecurity.TryGetValue(requestType, out var core))
            requirements = Combine(requirements, core);

        ct.ThrowIfCancellationRequested();
        operation.Security = [];

        foreach (var alternative in requirements.Any(item => item.Count != 0) ? requirements : [])
        {
            var requirement = new OpenApiSecurityRequirement();

            foreach (var (scheme, scopes) in alternative)
            {
                if (!SecuritySchemes.TryGetValue(scheme, out var definition))
                    throw new InvalidOperationException(
                        $"OpenAPI security scheme '{scheme}' has no SecuritySchemes definition.");


                if (scopes.Length != 0 &&
                    definition.Type is not (SecuritySchemeType.OAuth2 or SecuritySchemeType.OpenIdConnect))
                    throw new InvalidOperationException(
                        $"OpenAPI scopes on '{scheme}' require an OAuth2 or OpenID Connect scheme.");

                requirement.Add(new OpenApiSecuritySchemeReference(scheme, document), [.. scopes]);
            }


            operation.Security.Add(requirement);
        }


        async Task<IReadOnlyDictionary<string, string[]>[]> FromPolicy(AuthorizationPolicy policy)
        {
            if (!policy.Requirements.OfType<DenyAnonymousAuthorizationRequirement>().Any())
                throw new InvalidOperationException(
                    "OpenAPI needs explicit SecurityPolicy, SecurityDefault or SecurityFallback mappings for custom authorization requirements; an arbitrary assertion does not imply an authenticated caller.");

            var names = policy.AuthenticationSchemes.ToArray();

            if (names.Length == 0)
            {
                var scheme = await (services.GetService<IAuthenticationSchemeProvider>()
                                        ?.GetDefaultAuthenticateSchemeAsync()
                                    ?? Task.FromResult<AuthenticationScheme?>(null)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                if (scheme is null)
                    throw new InvalidOperationException(
                        "OpenAPI cannot determine a protected endpoint's authentication scheme. Configure authentication or SecurityPolicy mappings.");

                names = [scheme.Name];
            }


            return names.Select(name =>
                (IReadOnlyDictionary<string, string[]>)new Dictionary<string, string[]> { [name] = [] }).ToArray();
        }
    }

    internal void ApplySecuritySchemes(OpenApiDocument document)
    {
        if (SecuritySchemes.Count == 0)
            return;

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);

        foreach (var (name, scheme) in SecuritySchemes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

            if (!document.Components.SecuritySchemes.TryAdd(name, scheme))
                throw new InvalidOperationException($"OpenAPI security scheme '{name}' is defined more than once.");
        }
    }

    static bool OnlyDefault(IEnumerable<IAuthorizeData> data) => data.All(item =>
        string.IsNullOrWhiteSpace(item.Policy) && string.IsNullOrWhiteSpace(item.Roles) &&
        string.IsNullOrWhiteSpace(item.AuthenticationSchemes));

    static IReadOnlyDictionary<string, string[]>[] Snapshot(IReadOnlyDictionary<string, string[]>[] alternatives)
    {
        ArgumentNullException.ThrowIfNull(alternatives);

        if (alternatives.Length == 0 || alternatives.Any(item => item is null))
            throw new ArgumentException(
                "Security mappings require at least one alternative. Use an explicit empty dictionary for anonymous access.",
                nameof(alternatives));

        return alternatives.Select(alternative => (IReadOnlyDictionary<string, string[]>)alternative.ToDictionary(
            item =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(item.Key);
                return item.Key;
            }, item =>
            {
                ArgumentNullException.ThrowIfNull(item.Value);

                if (item.Value.Any(string.IsNullOrWhiteSpace))
                    throw new ArgumentException("OAuth scopes cannot be empty.", nameof(alternatives));

                return item.Value.Distinct(StringComparer.Ordinal).ToArray();
            }, StringComparer.Ordinal)).ToArray();
    }

    static IReadOnlyDictionary<string, string[]>[] Combine(
        IReadOnlyDictionary<string, string[]>[] left,
        IReadOnlyDictionary<string, string[]>[] right
    ) => left.SelectMany(first => right.Select(second =>
        (IReadOnlyDictionary<string, string[]>)first.Concat(second).GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.SelectMany(item => item.Value).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal))).ToArray();
}
