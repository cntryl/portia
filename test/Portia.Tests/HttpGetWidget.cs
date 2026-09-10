namespace Cntryl.Portia;

[RequestRoute("*", "http-binding-tests", "widgets", "get")]
[Discriminator("test.http.widgets.get")]
sealed record HttpGetWidget(Uuid WidgetId, bool IncludeArchived = false) : IRequest<string>, ICallable;
