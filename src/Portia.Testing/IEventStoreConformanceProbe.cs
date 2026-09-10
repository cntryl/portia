namespace Cntryl.Portia.Testing;

/// <summary>Supplies an isolated <see cref="IEventStore" /> for its conformance suite.</summary>
public interface IEventStoreConformanceProbe
{
    /// <summary>Gets the realm every stream in one run shares.</summary>
    string Realm { get; }

    /// <summary>Gets the area every stream in one run shares.</summary>
    string Area { get; }

    /// <summary>Clears all data owned by the isolated conformance target.</summary>
    ValueTask ResetAsync(CancellationToken ct = default);

    /// <summary>Opens the store under test. Repeated calls address the same durable data.</summary>
    ValueTask<IEventStore> OpenAsync(CancellationToken ct = default);
}
