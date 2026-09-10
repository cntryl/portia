namespace Cntryl.Portia;

/// <summary>Creates one independently disposable notification delivery scope.</summary>
public interface IRequestDeliveryScopeFactory
{
    /// <summary>Creates the scope for one delivered request.</summary>
    ValueTask<IRequestDeliveryScope> CreateAsync(CancellationToken ct = default);
}
