namespace Cntryl.Portia;

sealed record FitzQueueWorkerDefinition(string Route) : FitzRoutedWorkerDefinition(Route, QueueScheme, 3)
{
    internal const string QueueScheme = "queue";

    internal static FitzQueueWorkerDefinition For(RequestRouteAttribute route) =>
        new(Format(QueueScheme, route, false));

    internal override Func<CancellationToken, Task>? CreateRunner(FitzWorkerHost host) =>
        new QueueRunner(
            new FitzRequestQueueConsumer(host.Client.Queue, host.Serializer, Route, timeProvider: host.Clock,
                logger: host.QueueLogger),
            new DependencyInjectionQueueDeliveryScopeFactory(host.Scopes), host.QueueRunnerLogger).RunAsync;
}
