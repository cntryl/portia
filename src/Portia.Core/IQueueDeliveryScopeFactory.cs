namespace Cntryl.Portia;

/// <summary>Creates one independently disposable queue delivery scope.</summary>
public interface IQueueDeliveryScopeFactory
{
    /// <summary>Creates the scope for one reserved request.</summary>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A scope the caller disposes once the delivery has been handled.</returns>
    ValueTask<IQueueDeliveryScope> CreateAsync(CancellationToken ct = default);
}
