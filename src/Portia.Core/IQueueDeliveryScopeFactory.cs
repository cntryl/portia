namespace Cntryl.Portia;

/// <summary>Creates one independently disposable queue delivery scope.</summary>
public interface IQueueDeliveryScopeFactory
{
    /// <summary>Creates the scope for one reserved request.</summary>
    ValueTask<IQueueDeliveryScope> CreateAsync(CancellationToken ct = default);
}
