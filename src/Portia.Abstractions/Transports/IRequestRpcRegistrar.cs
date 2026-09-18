namespace Cntryl.Portia;

/// <summary>Accepts compile-time typed callable registrations without coupling feature contracts to a transport adapter.</summary>
public interface IRequestRpcRegistrar
{
    /// <summary>Registers a callable request without a result.</summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <param name="ct">Cancels registration.</param>
    /// <returns>Owns the worker registration.</returns>
    ValueTask<IAsyncDisposable> RegisterAsync<TRequest>(CancellationToken ct = default)
        where TRequest : IRequest, ICallable;

    /// <summary>Registers a callable request with a result.</summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TOut">The result type.</typeparam>
    /// <param name="ct">Cancels registration.</param>
    /// <returns>Owns the worker registration.</returns>
    ValueTask<IAsyncDisposable> RegisterAsync<TRequest, TOut>(CancellationToken ct = default)
        where TRequest : IRequest<TOut>, ICallable;
}
