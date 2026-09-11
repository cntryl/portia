namespace Cntryl.Portia;

[RequestRoute("*", "allocation-probe", "action", "run")]
[Discriminator("test.allocation.action")]
sealed record AllocationProbeAction : IRequest;
