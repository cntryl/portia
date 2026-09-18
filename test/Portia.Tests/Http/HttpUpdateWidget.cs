namespace Cntryl.Portia;

[RequestRoute("*", "http-binding-tests", "widgets", "update")]
[Discriminator("test.http.widgets.update")]
sealed record HttpUpdateWidget(
    Uuid WidgetId,
    string Name,
    int Quantity,
    bool Active,
    int? Priority = null,
    bool DryRun = false)
    : IRequest<string>, ICallable;
