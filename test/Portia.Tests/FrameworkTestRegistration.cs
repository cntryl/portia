using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cntryl.Portia;

static class FrameworkTestRegistration
{
    public static IServiceCollection AddFrameworkTests(this IServiceCollection services)
    {
        _ = services.AddPortia(p =>
        {
            _ = p.AddHandler<RpcGetValueHandler>();
            _ = p.AddHandler<RpcChangeValueHandler>();
            _ = p.AddHandler<TelemetrySuccessActionHandler>();
            _ = p.AddHandler<TelemetryFailureActionHandler>();
            _ = p.AddHandler<TelemetryGuardedActionHandler>();
            _ = p.AddHandler<TelemetrySequenceHandler>();
            _ = p.AddHandler<NoWorkerRegisteredPingHandler>();
            _ = p.AddHandler<GuardedActionHandler>();
            _ = p.AddHandler<GuardedQueryHandler>();
            _ = p.AddHandler<GetOrderHandler>();
            _ = p.AddHandler<AuthorizedActionHandler>();
            _ = p.AddHandler<GuardedSequenceHandler>();
            _ = p.AddHandler<GuardedAndAuthorizedActionHandler>();
            _ = p.AddHandler<ChangeValueHandler>();
            _ = p.AddHandler<GetValueHandler>();
            _ = p.AddHandler<HttpGetWidgetHandler>();
            _ = p.AddHandler<HttpCreatePaymentHandler>();
            _ = p.AddHandler<HttpCreateOrderHandler>();
            _ = p.AddHandler<HttpSendPingHandler>();
            _ = p.AddHandler<HttpGuardedActionHandler>();
            _ = p.AddHandler<HttpListWidgetsHandler>();
            _ = p.AddHandler<UniversalActionHandler>();
            _ = p.AddHandler<RunnerFaultVisibilityTests.RunnerFaultActionHandler>();
            _ = p.AddHandler<QueueRunnerTests.InvalidChangeValueHandler>();
            _ = p.AddAuthorizer<AuthorizedActionAuthorizer>();
            _ = p.AddAuthorizer<RecordingGuardedAndAuthorizedActionAuthorizer>();
            _ = p.AddEvent<WidgetNamed>();
            _ = p.AddEvent<WidgetRenamed>();
            _ = p.AddEvent<OrderPlacedV1>();
            _ = p.AddEvent<OrderPlacedV2>();
            _ = p.AddEvent<GadgetRenamed>();
            _ = p.AddEvent<TenantRegistered>();
            _ = p.AddEvent<TenantDeregistered>();
            _ = p.AddEvent<ValueChanged>();
            _ = p.AddEvent<ValueIncremented>();
            _ = p.AddEvent<ValueAudited>();
            _ = p.AddEvent<UnhandledEvent>();
            _ = p.AddRequest<RpcGetValue>();
            _ = p.AddRequest<RpcChangeValue>();
            _ = p.AddRequest<NoWorkerRegisteredPing>();
            _ = p.AddRequest<CreateUser>();
            _ = p.AddRequest<SendWelcomeEmail>();
            _ = p.AddRequest<HttpGetWidget>();
            _ = p.AddRequest<HttpCreatePayment>();
            _ = p.AddRequest<HttpCreateOrder>();
            _ = p.AddRequest<HttpSendPing>();
            _ = p.AddRequest<HttpGuardedAction>();
            _ = p.AddRequest<HttpListWidgets>();
            _ = p.AddRequest<UniversalAction>();
        });
        services.TryAddScoped<TestReactor>();
        _ = services.AddSingleton(new ReactorRegistration(typeof(TestReactor), p => p.GetRequiredService<TestReactor>()));
        services.TryAddScoped<TestProjector>();
        _ = services.AddSingleton(ProjectorRegistration.Create<TestProjector>());
        return services;
    }
}
