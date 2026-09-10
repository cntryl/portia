namespace Cntryl.Portia;

/// <summary>Continues a streaming request pipeline.</summary>
public delegate IAsyncEnumerable<TOut> StreamRequestPipelineNext<TOut>(CancellationToken ct);
