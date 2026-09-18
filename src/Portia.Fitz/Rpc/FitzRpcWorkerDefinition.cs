namespace Cntryl.Portia;

sealed record FitzRpcWorkerDefinition() : FitzWorkerDefinition(string.Empty)
{
    static readonly Type[] RequiredServices =
    [
        typeof(IRequestBus), typeof(IRequestActorValidator), typeof(IRequestDeserializer),
        typeof(IRequestOutcomeSerializer)
    ];

    internal override string Key => "rpc:";

    internal override IReadOnlyCollection<Type> Requirements => RequiredServices;
    // RPC registration is owned by StartAsync, which must hold its handle for the host lifetime.
}
