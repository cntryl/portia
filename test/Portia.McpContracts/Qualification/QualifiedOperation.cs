using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Serialization;

namespace Cntryl.Portia.McpContracts;

/// <summary>Changes one tenant's value and commits one event after authorization and validation.</summary>
[Discriminator("qualification.change")]
public sealed record QualifiedChange(string Tenant, int Value, string Mode = "success")
    : IRequest<QualifiedReply>, ICallable;

/// <summary>Reads committed events and completed invocation scopes for the authenticated tenant.</summary>
[Discriminator("qualification.read")]
public sealed record QualifiedRead(string Tenant, bool Wait = false, bool Fail = false) : IRequest<QualifiedState>, ICallable;

public sealed record QualifiedReply(int Value, Guid ScopeId, string[] Stages);
public sealed record QualifiedState(int[] Values, string[] Actors, QualifiedScopeEvidence[] Scopes,
    int WaitStarted, int WaitCanceled);
public sealed record QualifiedScopeEvidence(Guid Id, string[] Stages);

/// <summary>The value change committed by the shared application fixture.</summary>
[Discriminator("qualification.changed")]
public sealed record QualifiedChanged(int Value) : DomainEvent;

public sealed class QualifiedObservations
{
    int _waitStarted;
    int _waitCanceled;
    public const string Tenant = "11111111-1111-4111-8111-111111111111";
    public const string OtherTenant = "22222222-2222-4222-8222-222222222222";
    public ConcurrentQueue<QualifiedScopeEvidence> Completed { get; } = new();
    public int WaitStarted => Volatile.Read(ref _waitStarted);
    public int WaitCanceled => Volatile.Read(ref _waitCanceled);
    public async Task WaitAsync(CancellationToken ct)
    {
        _ = Interlocked.Increment(ref _waitStarted);
        try
        { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
        catch (OperationCanceledException)
        {
            _ = Interlocked.Increment(ref _waitCanceled);
            throw;
        }
    }
}

public sealed class QualifiedScope(QualifiedObservations observations) : IAsyncDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public List<string> Stages { get; } = [];
    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        Stages.Add("disposed");
        observations.Completed.Enqueue(new QualifiedScopeEvidence(Id, Stages.ToArray()));
    }
}

// Records disposal for suspended reads without adding completed-state observations for polling reads.
public sealed class QualifiedWaitScope(QualifiedObservations observations) : IAsyncDisposable
{
    readonly Guid _id = Guid.NewGuid();
    public bool Waiting { get; set; }
    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        if (Waiting)
            observations.Completed.Enqueue(new QualifiedScopeEvidence(_id, ["wait", "disposed"]));
    }
}

public sealed class QualifiedChangeAuthorizer(QualifiedScope scope) : IRequestAuthorizer<QualifiedChange>
{
    public ValueTask<Result> AuthorizeAsync(IRequestContext<QualifiedChange> context, CancellationToken ct)
    {
        scope.Stages.Add("authorized");
        return ValueTask.FromResult(QualifiedPolicy.Authorize(context.Actor, context.Request.Tenant));
    }
}

public sealed class QualifiedReadAuthorizer : IRequestAuthorizer<QualifiedRead>
{
    public ValueTask<Result> AuthorizeAsync(IRequestContext<QualifiedRead> context, CancellationToken ct) =>
        ValueTask.FromResult(QualifiedPolicy.Authorize(context.Actor, context.Request.Tenant));
}

static class QualifiedPolicy
{
    internal static Result Authorize(ClaimsPrincipal actor, string tenant) => actor.Identity?.Name == tenant
        ? Result.Success : Result.Failure(new RequestError(RequestErrorKind.Forbidden, "Tenant access denied."));
}

public sealed class QualifiedGuard(QualifiedScope scope) : IRequestGuard<QualifiedChange>
{
    public ValueTask<Result> GuardAsync(IRequestContext<QualifiedChange> context, CancellationToken ct)
    {
        scope.Stages.Add("guarded");
        return ValueTask.FromResult(context.Request.Value > 0 ? Result.Success
            : Result.Failure(new RequestError(RequestErrorKind.Validation, "Value must be positive.")));
    }
}

public sealed class QualifiedBehavior(QualifiedScope scope) : IRequestPipelineBehavior<QualifiedChange, QualifiedReply>
{
    public async ValueTask<Result<QualifiedReply>> HandleAsync(IRequestContext<QualifiedChange> context,
        RequestPipelineNext<QualifiedReply> continuation, CancellationToken ct)
    {
        scope.Stages.Add("before");
        var result = await continuation(ct);
        scope.Stages.Add("after");
        return result.IsSuccess
            ? Result<QualifiedReply>.Success(result.Value with { Stages = scope.Stages.ToArray() }) : result;
    }
}

public sealed class QualifiedChangeHandler(IAggregateExecutor executor, QualifiedScope scope)
    : IRequestHandler<QualifiedChange, QualifiedReply>
{
    public async ValueTask<Result<QualifiedReply>> HandleAsync(IRequestContext<QualifiedChange> context, CancellationToken ct)
    {
        scope.Stages.Add("handled");
        if (context.Request.Mode == "conflict")
            return Result<QualifiedReply>.Failure(new RequestError(RequestErrorKind.Conflict, "Value already exists."));
        if (context.Request.Mode == "fault")
            throw new IOException("sensitive application fault");
        return await executor.ExecuteAsync(new QualifiedAggregate(context.Request.Tenant), aggregate =>
        {
            aggregate.Change(context.Request.Value);
            return AggregateOutcome.Commit(Result<QualifiedReply>.Success(
                new QualifiedReply(aggregate.Value, scope.Id, [])));
        }, context, ct);
    }
}

public sealed class QualifiedReadHandler(IEventStore store, QualifiedObservations observations, QualifiedWaitScope scope)
    : IRequestHandler<QualifiedRead, QualifiedState>
{
    public async ValueTask<Result<QualifiedState>> HandleAsync(IRequestContext<QualifiedRead> context, CancellationToken ct)
    {
        if (context.Request.Wait)
        {
            scope.Waiting = true;
            await observations.WaitAsync(ct);
        }
        if (context.Request.Fail)
            throw new IOException("sensitive query fault");
        var values = new List<int>();
        var actors = new List<string>();
        await foreach (var record in store.ReadAsync(QualifiedAggregate.Address(context.Request.Tenant), ct: ct))
        {
            values.Add(((QualifiedChanged)record.Event).Value);
            actors.Add(record.Event.Metadata.Actor?.Subject ?? "missing");
        }
        return Result<QualifiedState>.Success(new QualifiedState(values.ToArray(), actors.ToArray(),
            observations.Completed.ToArray(), observations.WaitStarted, observations.WaitCanceled));
    }
}

sealed class QualifiedAggregate : Aggregate
{
    internal QualifiedAggregate(string tenant) : base(Uuid.Parse(tenant, CultureInfo.InvariantCulture), Address(tenant)) =>
        On<QualifiedChanged>(changed => Value = changed.Value);
    internal int Value { get; private set; }
    internal void Change(int value) => RaiseEvent(new QualifiedChanged(value));
    internal static EventStreamAddress Address(string tenant) => new("qualification", "values", tenant);
}

[PortiaJsonContext]
[JsonSerializable(typeof(QualifiedChange))]
[JsonSerializable(typeof(QualifiedRead))]
[JsonSerializable(typeof(QualifiedReply))]
[JsonSerializable(typeof(QualifiedState))]
[JsonSerializable(typeof(QualifiedChanged))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
sealed partial class QualifiedJsonContext : JsonSerializerContext;
