using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cntryl.Portia;

static class FrameworkTestRegistration
{
    public static IServiceCollection AddFrameworkTests(this IServiceCollection services)
    {
        _ = services.AddPortia(p =>
        {
            _ = p.AddRequestHandler<RpcGetValueHandler>();
            _ = p.AddRequestHandler<RpcChangeValueHandler>();
            _ = p.AddRequestHandler<TelemetrySuccessActionHandler>();
            _ = p.AddRequestHandler<TelemetryFailureActionHandler>();
            _ = p.AddRequestHandler<TelemetryGuardedActionHandler>();
            _ = p.AddRequestHandler<TelemetrySequenceHandler>();
            _ = p.AddRequestHandler<NoWorkerRegisteredPingHandler>();
            _ = p.AddRequestHandler<GuardedActionHandler>();
            _ = p.AddRequestHandler<GuardedQueryHandler>();
            _ = p.AddRequestHandler<GetOrderHandler>();
            _ = p.AddRequestHandler<AuthorizedActionHandler>();
            _ = p.AddRequestHandler<GuardedSequenceHandler>();
            _ = p.AddRequestHandler<GuardedAndAuthorizedActionHandler>();
            _ = p.AddRequestHandler<ChangeValueHandler>();
            _ = p.AddRequestHandler<GetValueHandler>();
            _ = p.AddRequestHandler<HttpGetWidgetHandler>();
            _ = p.AddRequestHandler<HttpCreatePaymentHandler>();
            _ = p.AddRequestHandler<HttpCreateOrderHandler>();
            _ = p.AddRequestHandler<HttpSendPingHandler>();
            _ = p.AddRequestHandler<HttpGuardedActionHandler>();
            _ = p.AddRequestHandler<HttpListWidgetsHandler>();
            _ = p.AddRequestHandler<UniversalActionHandler>();
            _ = p.AddRequestHandler<RunnerFaultVisibilityTests.RunnerFaultActionHandler>();
            _ = p.AddRequestHandler<QueueRunnerTests.InvalidChangeValueHandler>();
            _ = p.AddRequestAuthorizer<AuthorizedActionAuthorizer>();
            _ = p.AddRequestAuthorizer<RecordingGuardedAndAuthorizedActionAuthorizer>();
        });
        services.TryAddScoped<TestReactor>();
        _ = services.AddSingleton(new ReactorRegistration(typeof(TestReactor), p => p.GetRequiredService<TestReactor>()));
        services.TryAddScoped<TestProjector>();
        _ = services.AddSingleton(ProjectorRegistration.Create<TestProjector>());
        return services;
    }
}
