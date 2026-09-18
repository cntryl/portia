using System.Security.Claims;

namespace Cntryl.Portia.Testing;

sealed record ScenarioDefinition<TPayload>(
    IServiceProvider Services,
    ClaimsPrincipal Actor,
    RequestMetadata? Metadata,
    IRequestBase Request,
    Func<IRequestBus, RequestDispatchContext, Task<(ScenarioOutcome Outcome, TPayload Payload)>> Dispatch);
