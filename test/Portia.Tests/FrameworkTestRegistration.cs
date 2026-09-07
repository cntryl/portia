using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cntryl.Portia;

static class FrameworkTestRegistration
{
    public static IServiceCollection AddFrameworkTests(this IServiceCollection services)
    {
        _ = services.AddPortia(p =>
        {
            _ = p.AddRpcGetValueHandler();
            _ = p.AddRpcChangeValueHandler();
            _ = p.AddTelemetrySuccessActionHandler();
            _ = p.AddTelemetryFailureActionHandler();
            _ = p.AddTelemetryGuardedActionHandler();
            _ = p.AddTelemetrySequenceHandler();
            _ = p.AddNoWorkerRegisteredPingHandler();
            _ = p.AddGuardedActionHandler();
            _ = p.AddGuardedQueryHandler();
            _ = p.AddGetOrderHandler();
            _ = p.AddAuthorizedActionHandler();
            _ = p.AddGuardedSequenceHandler();
            _ = p.AddGuardedAndAuthorizedActionHandler();
            _ = p.AddChangeValueHandler();
            _ = p.AddGetValueHandler();
            _ = p.AddHttpGetWidgetHandler();
            _ = p.AddHttpCreatePaymentHandler();
            _ = p.AddHttpCreateOrderHandler();
            _ = p.AddHttpSendPingHandler();
            _ = p.AddHttpGuardedActionHandler();
            _ = p.AddHttpListWidgetsHandler();
            _ = p.AddUniversalActionHandler();
            _ = p.AddRunnerFaultActionHandler();
            _ = p.AddInvalidChangeValueHandler();
            _ = p.AddAuthorizedActionAuthorizer();
            _ = p.AddRecordingGuardedAndAuthorizedActionAuthorizer();
            _ = p.AddGeneratedEvents();
            _ = p.AddRpcGetValue();
            _ = p.AddRpcChangeValue();
            _ = p.AddNoWorkerRegisteredPing();
            _ = p.AddCreateUser();
            _ = p.AddSendWelcomeEmail();
            _ = p.AddHttpGetWidget();
            _ = p.AddHttpCreatePayment();
            _ = p.AddHttpCreateOrder();
            _ = p.AddHttpSendPing();
            _ = p.AddHttpGuardedAction();
            _ = p.AddUniversalAction();
        });
        services.TryAddScoped<TestReactor>();
        _ = services.AddSingleton(new ReactorRegistration(typeof(TestReactor), p => p.GetRequiredService<TestReactor>()));
        services.TryAddScoped<TestProjector>();
        _ = services.AddSingleton(ProjectorRegistration.Create<TestProjector>());
        return services;
    }
}
