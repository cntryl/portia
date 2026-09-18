namespace Cntryl.Portia;

[RequestRoute("*", "allocation-probe", "query", "run")]
[Discriminator("test.allocation.query")]
sealed record AllocationProbeQuery : IRequest<string>;
