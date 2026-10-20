using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Cntryl.Portia;

sealed class PortiaHttpSecurityValidation(IServiceProvider services, PortiaStartupValidationRegistry validations)
    : IPortiaStartupValidationParticipant, IStartupFilter
{
    readonly List<(SecureEndpointDataSource Source, bool Root)> _sources = [];
    PortiaStartupValidationRegistry.EndpointValidationRegistration? _pipelineValidation;
    bool _initialized;

    public IEndpointRouteBuilder CreateRouteBuilder(IEndpointRouteBuilder parent)
    {
        var routes = new SecureEndpointRouteBuilder(parent);
        var source = new SecureEndpointDataSource(routes.DataSources, validations.BeginEndpointValidation());
        parent.DataSources.Add(source);
        _sources.Add((source, parent is not RouteGroupBuilder));
        return routes;
    }

    public void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;
        // Routes in Startup.Configure do not exist when hosted services enter StartingAsync.
        // Keep Portia workers gated until the web pipeline has finished mapping those routes.
        if (services.GetService<IWebHostEnvironment>() is not null)
            _pipelineValidation = validations.BeginEndpointValidation();
        validations.Add("secure-host-endpoints", ValidateRootSources);
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        Initialize();
        try
        {
            next(app);
        }
        catch (Exception error)
        {
            _pipelineValidation?.Fail(error);
            throw;
        }
    };

    public void Complete()
    {
        try
        {
            ValidateRootSources();
            if (_sources.Any(item => !item.Root))
            {
                // Group conventions can still change after middleware first enumerates endpoints.
                // Rebuild from attached roots with the final group context, rather than accepting
                // the DI composite's earlier cached endpoint list.
                foreach (var source in services.GetServices<EndpointDataSource>())
                {
                    if (source is CompositeEndpointDataSource composite)
                    {
                        foreach (var root in composite.DataSources)
                            _ = root.Endpoints;
                    }
                    else
                    {
                        _ = source.Endpoints;
                    }
                }
            }

            if (_sources.Any(item => !item.Source.Validated))
                throw new InvalidOperationException(
                    "RequireSecureHost() could not validate all Portia HTTP endpoints. Complete endpoint routing with UseEndpoints() before starting workers.");
            _pipelineValidation?.Complete();
            validations.ThrowIfEndpointValidationFailed();
        }
        catch (Exception error)
        {
            _pipelineValidation?.Fail(error);
            throw;
        }
    }

    void ValidateRootSources()
    {
        foreach (var (source, root) in _sources)
            if (root)
                _ = source.Endpoints;
    }

    sealed class SecureEndpointRouteBuilder(IEndpointRouteBuilder parent) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider => parent.ServiceProvider;
        public ICollection<EndpointDataSource> DataSources { get; } = [];
        public IApplicationBuilder CreateApplicationBuilder() => parent.CreateApplicationBuilder();
    }

    sealed class SecureEndpointDataSource(
        ICollection<EndpointDataSource> sources,
        PortiaStartupValidationRegistry.EndpointValidationRegistration validation) : EndpointDataSource, IDisposable
    {
        // Mapping populates the private route builder after this source is attached to its parent.
        readonly Lazy<CompositeEndpointDataSource> _inner = new(() => new CompositeEndpointDataSource(sources));
        int _disposed;
        public bool Validated { get; private set; }

        // CompositeEndpointDataSource caches its root endpoints. Read children directly so
        // route-group conventions added during pipeline construction are checked again.
        public override IReadOnlyList<Endpoint> Endpoints => Validate(() => sources.SelectMany(source => source.Endpoints).ToArray());

        public override IReadOnlyList<Endpoint> GetGroupedEndpoints(RouteGroupContext context)
            => Validate(() => _inner.Value.GetGroupedEndpoints(context));

        public override IChangeToken GetChangeToken() => _inner.Value.GetChangeToken();

        IReadOnlyList<Endpoint> Validate(Func<IReadOnlyList<Endpoint>> build)
        {
            try
            {
                var endpoints = build();
                foreach (var endpoint in endpoints)
                {
                    if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null
                        && endpoint.Metadata.GetMetadata<IAuthorizeData>() is null)
                    {
                        throw new InvalidOperationException(
                            $"RequireSecureHost() found Portia HTTP endpoint '{endpoint.DisplayName ?? "<unnamed>"}' without ASP.NET authorization metadata. Apply RequireAuthorization(), or explicitly mark the route AllowAnonymous().");
                    }
                }

                Validated = true;
                validation.Complete();
                return endpoints;
            }
            catch (Exception error)
            {
                validation.Fail(error);
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (_inner.IsValueCreated)
                _inner.Value.Dispose();
            else
                foreach (var source in sources)
                    (source as IDisposable)?.Dispose();
        }
    }
}
