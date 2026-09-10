namespace Cntryl.Portia;

/// <summary>Creates one independently disposable notification delivery scope.</summary>
public interface IRequestDeliveryScopeFactory
{
    /// <summary>Creates the scope for one delivered request.</summary>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A scope the caller disposes once the delivery has been handled.</returns>
    ValueTask<IRequestDeliveryScope> CreateAsync(CancellationToken ct = default);
}
