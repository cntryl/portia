namespace Cntryl.Portia;

/// <summary>
/// <see cref="GeneratedRequestBus" /> is generated once for the whole test compilation, with one
/// constructor parameter per handler/authorizer discovered anywhere in it, plus
/// <see cref="IPermissionEvaluator" /> if anything in the compilation requires a permission. Every
/// test that needs a bus goes through here instead of constructing
/// <see cref="GeneratedRequestBus" /> directly, so adding a handler anywhere in the test project
/// doesn't break every other test file's construction call.
/// </summary>
static class TestRequestBus
{
    public static GeneratedRequestBus Create(
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
        IPermissionEvaluator? permissionEvaluator = null) =>
        new(
            authorizedActionHandler: authorizedActionHandler ?? new AuthorizedActionHandler(),
            changeValueHandler: changeValueHandler ?? new ChangeValueHandler(),
            getOrderHandler: getOrderHandler ?? new GetOrderHandler(),
            getValueHandler: getValueHandler ?? new GetValueHandler(),
            guardedActionHandler: guardedActionHandler ?? new GuardedActionHandler(),
            guardedQueryHandler: guardedQueryHandler ?? new GuardedQueryHandler(),
            guardedSequenceHandler: guardedSequenceHandler ?? new GuardedSequenceHandler(),
            invalidChangeValueHandler: invalidChangeValueHandler ?? new QueueRunnerTests.InvalidChangeValueHandler(),
            rpcGetValueHandler: rpcGetValueHandler ?? new RpcGetValueHandler(),
            rpcChangeValueHandler: rpcChangeValueHandler ?? new RpcChangeValueHandler(),
            universalActionHandler: universalActionHandler ?? new UniversalActionHandler(),
            guardedAndAuthorizedActionHandler: guardedAndAuthorizedActionHandler ?? new GuardedAndAuthorizedActionHandler(),
            telemetrySuccessActionHandler: telemetrySuccessActionHandler ?? new TelemetrySuccessActionHandler(),
            telemetryFailureActionHandler: telemetryFailureActionHandler ?? new TelemetryFailureActionHandler(),
            telemetryGuardedActionHandler: telemetryGuardedActionHandler ?? new TelemetryGuardedActionHandler(),
            telemetrySequenceHandler: telemetrySequenceHandler ?? new TelemetrySequenceHandler(),
            runnerFaultActionHandler: runnerFaultActionHandler ?? new RunnerFaultVisibilityTests.RunnerFaultActionHandler(),
            httpGetWidgetHandler: httpGetWidgetHandler ?? new HttpGetWidgetHandler(),
            httpCreateOrderHandler: httpCreateOrderHandler ?? new HttpCreateOrderHandler(),
            httpCreatePaymentHandler: httpCreatePaymentHandler ?? new HttpCreatePaymentHandler(),
            httpSendPingHandler: httpSendPingHandler ?? new HttpSendPingHandler(),
            httpGuardedActionHandler: httpGuardedActionHandler ?? new HttpGuardedActionHandler(),
            httpListWidgetsHandler: httpListWidgetsHandler ?? new HttpListWidgetsHandler(),
            authorizedActionAuthorizer: authorizedActionAuthorizer ?? new AuthorizedActionAuthorizer(),
            recordingGuardedAndAuthorizedActionAuthorizer: guardedAndAuthorizedActionAuthorizer ?? new RecordingGuardedAndAuthorizedActionAuthorizer(),
            permissionEvaluator: permissionEvaluator ?? TestPermissionEvaluator.AllowAll());
}
