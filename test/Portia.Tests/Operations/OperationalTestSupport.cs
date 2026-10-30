using System.Security.Claims;
using System.Text.Json;

namespace Cntryl.Portia;

sealed class EmptyApplicationTopology : IApplicationTopology
{
    public ApplicationTopology Describe() => new([], [], "test fixture; opaque dependencies unknown");
}

sealed class MemoryOperationalStore : IOperationalStore, IOperationalReactionCheckpointStore
{
    readonly Dictionary<OperationalTarget, byte[]> _documents = [];
    readonly Dictionary<CheckpointIdentity, ProjectionCheckpoint> _checkpoints = [];
    internal bool Unavailable { get; set; }

    public ValueTask<OperationalProcessorState?> ReadAsync(OperationalTarget target, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_documents)
        {
            if (Unavailable)
                throw new IOException("offline");
            return ValueTask.FromResult(_documents.TryGetValue(target, out var bytes)
                ? JsonSerializer.Deserialize(bytes, FitzJsonContext.Default.OperationalProcessorState) : null);
        }
    }

    public async ValueTask<IReadOnlyList<OperationalProcessorState>> ListAsync(OperationalScope scope, CancellationToken ct = default)
    {
        OperationalTarget[] targets;
        lock (_documents)
            targets = _documents.Keys.Where(t => t.Scope == scope).ToArray();
        var list = new List<OperationalProcessorState>();
        foreach (var target in targets)
            list.Add((await ReadAsync(target, ct))!);
        return list;
    }

    public ValueTask<bool> CompareExchangeAsync(OperationalProcessorState replacement, long expectedRevision,
        Guid expectedOwner, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Exchange(replacement, expectedRevision, expectedOwner));
    }

    OperationalProcessorState? Read(OperationalTarget target) => _documents.TryGetValue(target, out var bytes)
        ? JsonSerializer.Deserialize(bytes, FitzJsonContext.Default.OperationalProcessorState) : null;

    bool Exchange(OperationalProcessorState replacement, long expectedRevision, Guid expectedOwner)
    {
        lock (_documents)
        {
            if (Unavailable)
                throw new IOException("offline");
            var actual = Read(replacement.Target);
            if ((actual?.Revision ?? 0) != expectedRevision || (actual?.OwnerSession ?? Guid.Empty) != expectedOwner)
                return false;
            Assert.Equal(expectedRevision + 1, replacement.Revision);
            _documents[replacement.Target] = JsonSerializer.SerializeToUtf8Bytes(replacement,
                FitzJsonContext.Default.OperationalProcessorState);
            return true;
        }
    }

    public ValueTask<ProjectionCheckpoint> LoadAsync(CheckpointIdentity identity, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_documents)
            return ValueTask.FromResult(_checkpoints.GetValueOrDefault(identity));
    }

    public ValueTask SaveAsync(CheckpointIdentity identity, ProjectionCheckpoint checkpoint, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_documents)
            _checkpoints[identity] = checkpoint;
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> CommitAsync(OperationalProcessorState replacement, long expectedRevision, Guid ownerSession,
        Guid attemptId, string? expectedCheckpoint, string? nextCheckpoint, CancellationToken ct = default)
    {
        lock (_documents)
        {
            Assert.Equal(expectedCheckpoint, _checkpoints.GetValueOrDefault(replacement.Target.Identity).Cursor.Value);
            Assert.Equal("running", Read(replacement.Target)!.Attempts.Single(a => a.Id == attemptId).Outcome);
            if (!Exchange(replacement, expectedRevision, ownerSession))
                return ValueTask.FromResult(false);
            _checkpoints[replacement.Target.Identity] = new ProjectionCheckpoint(nextCheckpoint is null
                ? EventCursor.Start : new EventCursor(nextCheckpoint));
            return ValueTask.FromResult(true);
        }
    }
}

sealed class TestOperationalAuthorizer : IOperationalAuthorizer
{
    public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal actor, OperationalScope scope,
        OperationalAccess permission, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var allowed = actor.HasClaim("scope", scope.Tenant ?? "global")
            && (permission != OperationalAccess.Approve || actor.IsInRole("operator"));
        return ValueTask.FromResult(allowed);
    }

    internal static ClaimsPrincipal Actor(bool approval = false, string scope = "global") => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, approval ? "operator" : "agent"), new Claim("scope", scope),
            new Claim(ClaimTypes.Role, approval ? "operator" : "agent")], "test"));
}
