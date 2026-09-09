using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cntryl.Portia;

static class FrameworkTestRegistration
{
    public static IServiceCollection AddFrameworkTests(this IServiceCollection services)
    {
        var portia = services.AddPortia();
        _ = portia.AddRequestHandler<RpcGetValueHandler>();
        _ = portia.AddRequestHandler<RpcChangeValueHandler>();
        _ = portia.AddRequestHandler<TelemetrySuccessActionHandler>();
        _ = portia.AddRequestHandler<TelemetryFailureActionHandler>();
        _ = portia.AddRequestHandler<TelemetryGuardedActionHandler>();
        _ = portia.AddRequestHandler<TelemetrySequenceHandler>();
        _ = portia.AddRequestHandler<NoWorkerRegisteredPingHandler>();
        _ = portia.AddRequestHandler<GuardedActionHandler>();
        _ = portia.AddRequestHandler<GuardedQueryHandler>();
        _ = portia.AddRequestHandler<GetOrderHandler>();
        _ = portia.AddRequestHandler<AuthorizedActionHandler>();
        _ = portia.AddRequestHandler<GuardedSequenceHandler>();
        _ = portia.AddRequestHandler<GuardedAndAuthorizedActionHandler>();
        _ = portia.AddRequestHandler<ChangeValueHandler>();
        _ = portia.AddRequestHandler<GetValueHandler>();
        _ = portia.AddRequestHandler<HttpGetWidgetHandler>();
        _ = portia.AddRequestHandler<HttpCreatePaymentHandler>();
        _ = portia.AddRequestHandler<HttpCreateOrderHandler>();
        _ = portia.AddRequestHandler<HttpSendPingHandler>();
        _ = portia.AddRequestHandler<HttpOptionalBodyHandler>();
        _ = portia.AddRequestHandler<HttpGuardedActionHandler>();
        _ = portia.AddRequestHandler<HttpListWidgetsHandler>();
        _ = portia.AddRequestHandler<UniversalActionHandler>();
        _ = portia.AddRequestHandler<RunnerFaultVisibilityTests.RunnerFaultActionHandler>();
        _ = portia.AddRequestHandler<QueueRunnerTests.InvalidChangeValueHandler>();
        _ = portia.AddRequestAuthorizer<AuthorizedActionAuthorizer>();
        _ = portia.AddRequestAuthorizer<RecordingGuardedAndAuthorizedActionAuthorizer>();
        _ = portia.AddRequestPipelineBehavior<ChangeValuePipelineBehavior>(order: 10);
        services.TryAddScoped<TestReactor>();
        _ = services.AddSingleton(new ReactorRegistration(typeof(TestReactor), provider => provider.GetRequiredService<TestReactor>()));
        services.TryAddScoped<TestProjector>();
        _ = services.AddSingleton(ProjectorRegistration.Create<TestProjector>());
        return services;
    }
}
