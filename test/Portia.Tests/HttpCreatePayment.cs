namespace Cntryl.Portia;

[RequestRoute("*", "http-binding-tests", "payments", "create")]
[Discriminator("test.http.payments.create")]
sealed record HttpCreatePayment(HttpMoney Amount) : IRequest<Uuid>, ICallable;
