namespace Cntryl.Portia;

/// <summary>
///     Continues a streaming request pipeline. A behavior may invoke this delegate zero or one time
///     during its own enumeration; a second, concurrent, or retained late invocation is invalid.
/// </summary>
/// <typeparam name="TOut">The type of the values produced.</typeparam>
/// <param name="ct">A token that can cancel enumeration of the rest of the pipeline.</param>
/// <returns>The values yielded by the rest of the pipeline.</returns>
public delegate IAsyncEnumerable<TOut> StreamRequestPipelineNext<TOut>(CancellationToken ct);
