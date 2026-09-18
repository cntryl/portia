namespace Cntryl.Portia;

/// <summary>
///     Continues a no-result request pipeline. A behavior may invoke this delegate zero or one time
///     during its own invocation; a second, concurrent, or retained late invocation is invalid.
/// </summary>
/// <param name="ct">A token that can cancel the remainder of the pipeline.</param>
/// <returns>The outcome produced by the rest of the pipeline.</returns>
public delegate ValueTask<Result> RequestPipelineNext(CancellationToken ct);
