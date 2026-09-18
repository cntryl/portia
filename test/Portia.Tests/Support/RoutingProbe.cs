namespace Cntryl.Portia;

[RequestRoute("*", "routing", "probe", "run")]
[Discriminator("test.routing.probe")]
sealed record RoutingProbe : IRequest, ICallable;
