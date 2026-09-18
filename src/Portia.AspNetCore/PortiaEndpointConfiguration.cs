using System.ComponentModel;
using Microsoft.AspNetCore.Http;

namespace Cntryl.Portia;

/// <summary>Configures a no-result Portia HTTP endpoint.</summary>
public sealed class PortiaEndpointConfiguration<TRequest>
    : PortiaEndpointConfigurationBase<TRequest, PortiaEndpointConfiguration<TRequest>>
{
    /// <summary>Gets the result hook used by generated endpoint code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Func<HttpContext, Result, CancellationToken, ValueTask<IResult?>>? ResultHandler { get; private set; }

    /// <summary>
    ///     Optionally replaces the final synchronous Portia response. Declared success responses replace Portia's default
    ///     success response in OpenAPI.
    /// </summary>
    public PortiaEndpointConfiguration<TRequest> OnResult(Func<HttpContext, Result, IResult?> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        SetResult((http, result, _) => ValueTask.FromResult(handler(http, result)));
        return this;
    }

    /// <summary>
    ///     Asynchronously and optionally replaces the final synchronous Portia response. Declared success responses
    ///     replace Portia's default success response in OpenAPI.
    /// </summary>
    public PortiaEndpointConfiguration<TRequest> OnResult(
        Func<HttpContext, Result, CancellationToken, ValueTask<IResult?>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        SetResult(handler);
        return this;
    }

    void SetResult(Func<HttpContext, Result, CancellationToken, ValueTask<IResult?>> handler)
    {
        EnsureMutable();
        if (ResultHandler is not null)
            throw new InvalidOperationException("OnResult can only be configured once.");
        RegisterResultHandler();
        ResultHandler = async (http, result, ct) =>
            EnsureResultHandlerResponse(result.IsSuccess, await handler(http, result, ct).ConfigureAwait(false));
    }
}

/// <summary>Configures a result-bearing Portia HTTP endpoint.</summary>
public sealed class PortiaEndpointConfiguration<TRequest, TOut>
    : PortiaEndpointConfigurationBase<TRequest, PortiaEndpointConfiguration<TRequest, TOut>>
{
    /// <summary>Gets the result hook used by generated endpoint code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Func<HttpContext, Result<TOut>, CancellationToken, ValueTask<IResult?>>? ResultHandler { get; private set; }

    /// <summary>
    ///     Optionally replaces the final synchronous Portia response. Declared success responses replace Portia's default
    ///     success response in OpenAPI.
    /// </summary>
    public PortiaEndpointConfiguration<TRequest, TOut> OnResult(Func<HttpContext, Result<TOut>, IResult?> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        SetResult((http, result, _) => ValueTask.FromResult(handler(http, result)));
        return this;
    }

    /// <summary>
    ///     Asynchronously and optionally replaces the final synchronous Portia response. Declared success responses
    ///     replace Portia's default success response in OpenAPI.
    /// </summary>
    public PortiaEndpointConfiguration<TRequest, TOut> OnResult(
        Func<HttpContext, Result<TOut>, CancellationToken, ValueTask<IResult?>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        SetResult(handler);
        return this;
    }

    void SetResult(Func<HttpContext, Result<TOut>, CancellationToken, ValueTask<IResult?>> handler)
    {
        EnsureMutable();
        if (ResultHandler is not null)
            throw new InvalidOperationException("OnResult can only be configured once.");
        RegisterResultHandler();
        ResultHandler = async (http, result, ct) =>
            EnsureResultHandlerResponse(result.IsSuccess, await handler(http, result, ct).ConfigureAwait(false));
    }
}
