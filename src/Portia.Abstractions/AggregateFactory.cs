namespace Cntryl.Portia;

/// <summary>Constructs a new aggregate with its explicit identity and stream address.</summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
/// <param name="services">The current application scope.</param>
/// <param name="id">The aggregate identity.</param>
/// <returns>A new, empty aggregate.</returns>
public delegate TAggregate AggregateFactory<out TAggregate>(IServiceProvider services, Uuid id)
    where TAggregate : Aggregate;
