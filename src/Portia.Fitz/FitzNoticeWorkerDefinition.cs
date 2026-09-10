namespace Cntryl.Portia;

sealed record FitzNoticeWorkerDefinition(string Route) : FitzRoutedWorkerDefinition(Route, NoticeScheme, 3)
{
    internal const string NoticeScheme = "notice";

    internal static FitzNoticeWorkerDefinition For(RequestRouteAttribute route) =>
        new(Format(NoticeScheme, route, false));

    internal override Func<CancellationToken, Task>? CreateRunner(FitzWorkerHost host) =>
        new RequestNotificationRunner(
            new FitzNoticeRequestConsumer(host.Client.Notice, host.Serializer, Route),
            new DependencyInjectionRequestDeliveryScopeFactory(host.Scopes), host.NotificationLogger).RunAsync;
}
