using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Owns a scope using the same generated registrations as applications, with optional test overrides.</summary>
sealed class TestRequestBus : IDisposable
{
    readonly ServiceProvider _provider;
    readonly IServiceScope _scope;

    TestRequestBus(ServiceProvider provider)
    {
        _provider = provider;
        _scope = provider.CreateScope();
        Bus = _scope.ServiceProvider.GetRequiredService<IRequestBus>();
    }

    public IRequestBus Bus { get; }

    public IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();

    public static TestRequestBus Create(
        ChangeValueHandler? changeValueHandler = null,
        GetValueHandler? getValueHandler = null,
        QueueRunnerTests.InvalidChangeValueHandler? invalidChangeValueHandler = null,
        GuardedActionHandler? guardedActionHandler = null,
        GuardedQueryHandler? guardedQueryHandler = null,
        GetOrderHandler? getOrderHandler = null,
        GuardedSequenceHandler? guardedSequenceHandler = null,
        AuthorizedActionHandler? authorizedActionHandler = null,
        AuthorizedActionAuthorizer? authorizedActionAuthorizer = null,
        RpcGetValueHandler? rpcGetValueHandler = null,
        RpcChangeValueHandler? rpcChangeValueHandler = null,
        UniversalActionHandler? universalActionHandler = null,
        GuardedAndAuthorizedActionHandler? guardedAndAuthorizedActionHandler = null,
        RecordingGuardedAndAuthorizedActionAuthorizer? guardedAndAuthorizedActionAuthorizer = null,
        TelemetrySuccessActionHandler? telemetrySuccessActionHandler = null,
        TelemetryFailureActionHandler? telemetryFailureActionHandler = null,
        TelemetryGuardedActionHandler? telemetryGuardedActionHandler = null,
        TelemetrySequenceHandler? telemetrySequenceHandler = null,
        RunnerFaultVisibilityTests.RunnerFaultActionHandler? runnerFaultActionHandler = null,
        HttpGetWidgetHandler? httpGetWidgetHandler = null,
        HttpCreateOrderHandler? httpCreateOrderHandler = null,
        HttpCreatePaymentHandler? httpCreatePaymentHandler = null,
        HttpSendPingHandler? httpSendPingHandler = null,
        HttpGuardedActionHandler? httpGuardedActionHandler = null,
        HttpListWidgetsHandler? httpListWidgetsHandler = null,
        NoWorkerRegisteredPingHandler? noWorkerRegisteredPingHandler = null,
        IPermissionEvaluator? permissionEvaluator = null,
        IRequestActorValidator? actorValidator = null)
    {
        var services = new ServiceCollection();
        _ = services.AddFrameworkTests();
        _ = services.AddSingleton(actorValidator ?? new PassActorValidator());
        _ = services.AddSingleton<IRequestDeserializer, JsonRequestSerializer>();
        _ = services.AddSingleton<IRequestOutcomeSerializer, JsonRequestSerializer>();
        _ = services.AddSingleton(permissionEvaluator ?? TestPermissionEvaluator.AllowAll());
        if (changeValueHandler is not null)
            _ = services.AddSingleton(changeValueHandler);
        if (getValueHandler is not null)
            _ = services.AddSingleton(getValueHandler);
        if (invalidChangeValueHandler is not null)
            _ = services.AddSingleton(invalidChangeValueHandler);
        if (guardedActionHandler is not null)
            _ = services.AddSingleton(guardedActionHandler);
        if (guardedQueryHandler is not null)
            _ = services.AddSingleton(guardedQueryHandler);
        if (getOrderHandler is not null)
            _ = services.AddSingleton(getOrderHandler);
        if (guardedSequenceHandler is not null)
            _ = services.AddSingleton(guardedSequenceHandler);
        if (authorizedActionHandler is not null)
            _ = services.AddSingleton(authorizedActionHandler);
        if (authorizedActionAuthorizer is not null)
            _ = services.AddSingleton(authorizedActionAuthorizer);
        if (rpcGetValueHandler is not null)
            _ = services.AddSingleton(rpcGetValueHandler);
        if (rpcChangeValueHandler is not null)
            _ = services.AddSingleton(rpcChangeValueHandler);
        if (universalActionHandler is not null)
            _ = services.AddSingleton(universalActionHandler);
        if (guardedAndAuthorizedActionHandler is not null)
            _ = services.AddSingleton(guardedAndAuthorizedActionHandler);
        if (guardedAndAuthorizedActionAuthorizer is not null)
            _ = services.AddSingleton(guardedAndAuthorizedActionAuthorizer);
        if (telemetrySuccessActionHandler is not null)
            _ = services.AddSingleton(telemetrySuccessActionHandler);
        if (telemetryFailureActionHandler is not null)
            _ = services.AddSingleton(telemetryFailureActionHandler);
        if (telemetryGuardedActionHandler is not null)
            _ = services.AddSingleton(telemetryGuardedActionHandler);
        if (telemetrySequenceHandler is not null)
            _ = services.AddSingleton(telemetrySequenceHandler);
        if (runnerFaultActionHandler is not null)
            _ = services.AddSingleton(runnerFaultActionHandler);
        if (httpGetWidgetHandler is not null)
            _ = services.AddSingleton(httpGetWidgetHandler);
        if (httpCreateOrderHandler is not null)
            _ = services.AddSingleton(httpCreateOrderHandler);
        if (httpCreatePaymentHandler is not null)
            _ = services.AddSingleton(httpCreatePaymentHandler);
        if (httpSendPingHandler is not null)
            _ = services.AddSingleton(httpSendPingHandler);
        if (httpGuardedActionHandler is not null)
            _ = services.AddSingleton(httpGuardedActionHandler);
        if (httpListWidgetsHandler is not null)
            _ = services.AddSingleton(httpListWidgetsHandler);
        if (noWorkerRegisteredPingHandler is not null)
            _ = services.AddSingleton(noWorkerRegisteredPingHandler);
        return new TestRequestBus(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }));
    }

    sealed class PassActorValidator : IRequestActorValidator
    {
        public ValueTask<Result<System.Security.Claims.ClaimsPrincipal>> ValidateAsync(string? token, CancellationToken ct = default)
            => ValueTask.FromResult(Result<System.Security.Claims.ClaimsPrincipal>.Success(RequestActor.System));
    }

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
    }
}
