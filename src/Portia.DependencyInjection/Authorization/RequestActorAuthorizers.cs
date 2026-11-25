namespace Cntryl.Portia;

sealed class AuthenticatedRequestActorAuthorizer : IRequestAuthorizer<IAuthenticatedRequest>
{
    public ValueTask<Result> AuthorizeAsync(IRequestContext<IAuthenticatedRequest> context, CancellationToken ct)
    {
        _ = ct;
        return ValueTask.FromResult(ActorAttribution.TryFromAuthenticatedPrincipal(context.Actor, out _)
            ? Result.Success
            : Result.Failure(RequestError.Unauthorized("The request requires one unambiguous authenticated actor.")));
    }
}

sealed class SystemRequestActorAuthorizer : IRequestAuthorizer<ISystemRequest>
{
    public ValueTask<Result> AuthorizeAsync(IRequestContext<ISystemRequest> context, CancellationToken ct)
    {
        _ = ct;
        var actor = context.Actor;

        if (!ActorAttribution.TryFromAuthenticatedPrincipal(actor, out _))
            return ValueTask.FromResult(Result.Failure(RequestError.Unauthorized(
                "The request requires one unambiguous authenticated system actor.")));

        return ValueTask.FromResult(RequestActor.IsSystem(actor)
            ? Result.Success
            : Result.Failure(RequestError.Forbidden("The request requires a system actor.")));
    }
}
