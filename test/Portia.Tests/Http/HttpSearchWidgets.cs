namespace Cntryl.Portia;

[RequestRoute("*", "http-binding-tests", "widgets", "search")]
[Discriminator("test.http.widgets.search")]
sealed record HttpSearchWidgets(HttpWidgetFilter Filter) : IRequest<string>, ICallable;

sealed record HttpWidgetFilter(string Name, int Limit);
