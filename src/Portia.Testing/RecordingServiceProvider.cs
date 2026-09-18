using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Testing;

sealed class RecordingServiceProvider(IServiceProvider inner) : IServiceProvider, ISupportRequiredService
{
    readonly Lock _gate = new();
    readonly List<Type> _resolved = [];

    public IReadOnlyList<Type> Resolved
    {
        get
        {
            lock (_gate)
                return [.. _resolved];
        }
    }

    public object? GetService(Type serviceType)
    {
        var service = inner.GetService(serviceType);
        if (service is not null)
            Record(serviceType);
        return service;
    }

    public object GetRequiredService(Type serviceType)
    {
        var service = inner.GetRequiredService(serviceType);
        Record(serviceType);
        return service;
    }

    void Record(Type serviceType)
    {
        lock (_gate)
            _resolved.Add(serviceType);
    }
}
