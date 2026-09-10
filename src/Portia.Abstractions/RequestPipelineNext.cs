namespace Cntryl.Portia;

/// <summary>Continues a no-result request pipeline.</summary>
/// <param name="ct">A token that can cancel the remainder of the pipeline.</param>
/// <returns>The outcome produced by the rest of the pipeline.</returns>
public delegate ValueTask<Result> RequestPipelineNext(CancellationToken ct);
