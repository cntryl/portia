namespace Cntryl.Portia;

[RequestRoute("*", "http-binding-tests", "orders", "create")]
[Discriminator("test.http.orders.create")]
sealed record HttpCreateOrder(List<HttpOrderLine> Lines) : IRequest<Uuid>, ICallable;
