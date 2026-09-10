namespace Cntryl.Portia;

sealed record FitzScheduleWorkerDefinition(string Route) : FitzRoutedWorkerDefinition(Route, ScheduleScheme, 4)
{
    internal const string ScheduleScheme = "schedule";

    internal static FitzScheduleWorkerDefinition For(RequestRouteAttribute route) =>
        new(Format(ScheduleScheme, route, true));

    internal override Func<CancellationToken, Task>? CreateRunner(FitzWorkerHost host) =>
        new RequestNotificationRunner(
            new FitzScheduledRequestConsumer(host.Client.Schedule, host.Serializer, Route),
            new DependencyInjectionRequestDeliveryScopeFactory(host.Scopes), host.NotificationLogger).RunAsync;
}
