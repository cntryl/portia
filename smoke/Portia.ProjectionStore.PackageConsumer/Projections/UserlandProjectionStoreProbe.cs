using Cntryl.Portia;
using Cntryl.Portia.Testing;

sealed class UserlandProjectionStoreProbe : IProjectionStoreConformanceProbe
{
    readonly UserlandTarget _target = new();

    public CheckpointIdentity LiveIdentity { get; } = new("package-consumer",
        EventStreamPattern.ForPattern("smoke", "projection"));

    public CheckpointIdentity RebuildIdentity { get; } = new("package-consumer",
        EventStreamPattern.ForPattern("smoke", "projection"), "rebuild");

    public ValueTask ResetAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _target.Reset();
        return ValueTask.CompletedTask;
    }

    public ValueTask<IProjectionStoreConformanceSession> OpenSessionAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IProjectionStoreConformanceSession>(new UserlandSession(_target));
    }
}

sealed class UserlandSession(UserlandTarget target) : IProjectionStoreConformanceSession, IProjectionStore
{
    UserlandBatch? _active;

    public IProjectionStore Store => this;

    public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(target.Read(identity).Checkpoint);
    }

    public ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        if (_active is not null)
        {
            throw new InvalidOperationException("Only one projection batch may be active in a repository session.");
        }

        _active = new UserlandBatch(this, target, context);
        return ValueTask.FromResult<IProjectionBatch>(_active);
    }

    public ValueTask StageValueAsync(string value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Active().Stage(value);
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> ReadValueAsync(CheckpointIdentity identity, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(target.Read(identity).Value);
    }

    public ValueTask FailNextCommitAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Active().FailNextCommit();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_active is { } batch)
        {
            await batch.DisposeAsync();
        }
    }

    internal void Release(UserlandBatch batch)
    {
        if (ReferenceEquals(_active, batch))
        {
            _active = null;
        }
    }

    UserlandBatch Active() => _active ??
        throw new InvalidOperationException("Begin a projection batch before staging application data.");
}

sealed class UserlandBatch(
    UserlandSession owner,
    UserlandTarget target,
    ProjectionBatchContext context) : IProjectionBatch
{
    int _disposed;
    bool _failNextCommit;
    string? _value;

    public ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        target.Commit(context.Identity, context.Checkpoint, checkpoint, _value, _failNextCommit);
        _ = Interlocked.Exchange(ref _disposed, 1);
        owner.Release(this);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _ = Interlocked.Exchange(ref _disposed, 1);
        owner.Release(this);
        return ValueTask.CompletedTask;
    }

    internal void FailNextCommit() => _failNextCommit = true;

    internal void Stage(string value)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _value = value;
    }
}

sealed class UserlandTarget
{
    readonly object _gate = new();
    readonly Dictionary<CheckpointIdentity, State> _states = [];

    internal void Commit(CheckpointIdentity identity, ProjectionCheckpoint expected,
        ProjectionCheckpoint checkpoint, string? value, bool fail)
    {
        lock (_gate)
        {
            var current = _states.GetValueOrDefault(identity, State.Empty);
            if (current.Checkpoint != expected)
            {
                throw new ProjectionConcurrencyException(
                    $"Projection '{identity.ComponentName}' no longer has checkpoint {expected.Cursor}.");
            }

            if (fail)
            {
                throw new IOException("Injected transaction failure.");
            }

            _states[identity] = new State(value, checkpoint);
        }
    }

    internal State Read(CheckpointIdentity identity)
    {
        lock (_gate)
        {
            return _states.GetValueOrDefault(identity, State.Empty);
        }
    }

    internal void Reset()
    {
        lock (_gate)
        {
            _states.Clear();
        }
    }

    internal sealed record State(string? Value, ProjectionCheckpoint Checkpoint)
    {
        internal static State Empty { get; } = new(null, ProjectionCheckpoint.Start);
    }
}
