namespace Cntryl.Portia;

/// <summary>Continues a no-result request pipeline.</summary>
public delegate ValueTask<Result> RequestPipelineNext(CancellationToken ct);
