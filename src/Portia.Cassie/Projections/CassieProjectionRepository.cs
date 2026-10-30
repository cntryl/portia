using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using Cntryl.Cassie;
using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Connection;

namespace Cntryl.Portia;

/// <summary>Shares a native Cassie transaction between application projection writes and Portia checkpoints.</summary>
/// <remarks>
///     Derive an application repository from this class. Its updates use <see cref="CreateCommand" />
///     and include <see cref="CurrentProjectionKey" /> in their keys or predicates. Query methods return
///     application data contracts through <see cref="OpenReadAsync" />. Each repository serves one
///     explicitly named projector, and each batch has an independent tenant, pattern, and generation key.
/// </remarks>
public abstract class CassieProjectionRepository : IProjectionStore, IAsyncDisposable
{
    readonly CassieProjectionDatabase _database;
    readonly string _componentName;
    Session? _active;
    int _busy;
    bool _disposed;

    /// <summary>Creates the application repository's projection persistence boundary.</summary>
    /// <param name="database">Shared Cassie connection and metadata initialization.</param>
    /// <param name="componentName">The explicit stable name used to register this projector.</param>
    protected CassieProjectionRepository(CassieProjectionDatabase database, string componentName)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
        _database = database;
        _componentName = componentName;
    }

    /// <summary>Gets the active batch's tenant, pattern, and rebuild identity.</summary>
    protected CheckpointIdentity Identity => Active.Identity;

    /// <summary>Gets the stable full identity key to include in application writes for the active batch.</summary>
    protected string CurrentProjectionKey => GetProjectionKey(Active.Identity);

    /// <summary>Creates a native command enlisted in the active projection transaction.</summary>
    /// <param name="sql">Application-owned SQL.</param>
    /// <returns>A command the application must dispose.</returns>
    protected CassieCommand CreateCommand(string sql)
    {
        var session = Active;
        var command = (CassieCommand)session.Connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = session.Transaction;
        return command;
    }

    /// <summary>Opens a caller-owned connection for committed query-side reads outside an active batch.</summary>
    /// <param name="ct">Cancels opening the connection.</param>
    /// <returns>The native connection, which the query method must dispose.</returns>
    protected async ValueTask<CassieConnection> OpenReadAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _busy) != 0)
            throw new InvalidOperationException("Read through the active transaction while a projection batch is open.");
        await _database.InitializeAsync(ct).ConfigureAwait(false);
        return await _database.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Builds the same stable scope key used by projection writes, for application query predicates.</summary>
    /// <param name="identity">The explicit component, canonical pattern, and generation to query.</param>
    /// <returns>A structurally encoded key independent of CLR names.</returns>
    protected string GetProjectionKey(CheckpointIdentity identity)
    {
        EnsureServes(identity);
        return string.Concat(new[] { identity.ComponentName, identity.Pattern.Realm, identity.Pattern.Area,
                identity.Pattern.Resource, identity.RebuildId }
            .Select(static segment => segment is null ? "n;"
                : segment.Length.ToString(CultureInfo.InvariantCulture) + ":" + segment));
    }

    /// <inheritdoc />
    public async ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity, CancellationToken ct = default)
    {
        var key = GetProjectionKey(identity);
        await using var connection = await OpenReadAsync(ct).ConfigureAwait(false);
        await using var command = Command(connection, null,
            $"SELECT cursor FROM {_database.CheckpointTable} WHERE identity = @identity", ("identity", key));
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (value is null)
            return ProjectionCheckpoint.Start;
        if (value is not string cursor)
            throw new InvalidDataException("Cassie projection checkpoint metadata contains an invalid cursor.");
        return cursor.Length == 0 ? ProjectionCheckpoint.Start : new ProjectionCheckpoint(new EventCursor(cursor));
    }

    /// <inheritdoc />
    public async ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = GetProjectionKey(context.Identity);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("A projection batch is already open on this repository.");
        CassieConnection? connection = null;
        DbTransaction? transaction = null;
        try
        {
            await _database.InitializeAsync(ct).ConfigureAwait(false);
            connection = await _database.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using (var create = Command(connection, null,
                $"INSERT INTO {_database.CheckpointTable} (identity, cursor, revision) VALUES (@identity, '', 0) ON CONFLICT (identity) DO NOTHING", ("identity", key)))
                _ = await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
            await using var read = Command(connection, transaction,
                $"SELECT cursor, revision FROM {_database.CheckpointTable} WHERE identity = @identity", ("identity", key));
            await using var values = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await values.ReadAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException("Cassie projection checkpoint metadata is missing.");
            if (values.GetString(0) != (context.Checkpoint.Cursor.Value ?? ""))
                throw new ProjectionConcurrencyException("The Cassie projection checkpoint changed; reload progress.");
            var session = new Session(this, connection, transaction, context.Identity, key, values.GetInt64(1));
            ObjectDisposedException.ThrowIf(_disposed, this);
            _active = session;
            return session;
        }
        catch (Exception original)
        {
            try
            { await ReleaseAsync(connection, transaction, committed: false).ConfigureAwait(false); }
            catch (Exception cleanup) { original.Data["Portia.CleanupException"] = cleanup; }
            _active = null;
            Volatile.Write(ref _busy, 0);
            throw;
        }
    }

    /// <summary>Discards a leaked batch when its repository scope ends.</summary>
    public virtual async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_active is { } session)
            await session.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    Session Active => !_disposed && _active is { Finished: false } session ? session
        : throw new InvalidOperationException("No writable projection batch is open on this repository.");

    void EnsureServes(CheckpointIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.ComponentName != _componentName || identity.Pattern.IsTenantTemplate)
            throw new ArgumentException("Use this repository's registered projector name and a bound stream pattern.", nameof(identity));
    }

    static CassieCommand Command(CassieConnection connection, DbTransaction? transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = (CassieCommand)connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var parameter in parameters)
            _ = command.Parameters.Add(new CassieParameter { ParameterName = parameter.Name, Value = parameter.Value });
        return command;
    }

    static async ValueTask ReleaseAsync(CassieConnection? connection, DbTransaction? transaction, bool committed)
    {
        Exception? failure = null;
        if (transaction is not null)
        {
            if (!committed)
            {
                try
                { await transaction.RollbackAsync().ConfigureAwait(false); }
                catch (Exception ex) { failure = ex; }
            }
            try
            { await transaction.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { failure = failure is null ? ex : new AggregateException(failure, ex); }
        }
        if (connection is not null)
        {
            try
            { await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { failure = failure is null ? ex : new AggregateException(failure, ex); }
        }
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    sealed class Session(CassieProjectionRepository owner, CassieConnection connection, DbTransaction transaction,
        CheckpointIdentity identity, string key, long revision) : IProjectionBatch
    {
        int _disposed;
        bool _committed;
        public CassieConnection Connection { get; } = connection;
        public DbTransaction Transaction { get; } = transaction;
        public CheckpointIdentity Identity { get; } = identity;
        public bool Finished { get; private set; }
        public async ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
        {
            if (Finished || Volatile.Read(ref _disposed) != 0)
                throw new InvalidOperationException("This projection batch has already finished.");
            try
            {
                await using var command = Command(Connection, Transaction,
                    $"UPDATE {owner._database.CheckpointTable} SET cursor = @cursor, revision = revision + 1 WHERE identity = @identity AND revision = @revision",
                    ("cursor", checkpoint.Cursor.Value ?? ""), ("identity", key), ("revision", revision));
                if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                    throw new ProjectionConcurrencyException("The Cassie projection checkpoint changed; reload progress.");
                await Transaction.CommitAsync(ct).ConfigureAwait(false);
                _committed = true;
            }
            catch (CassieException ex) when (ex.SqlState is "40001" or "40P01")
            {
                throw new ProjectionConcurrencyException("Cassie rejected a concurrent projection commit.", ex);
            }
            finally { Finished = true; }
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Finished = true;
            try
            { await ReleaseAsync(Connection, Transaction, _committed).ConfigureAwait(false); }
            finally
            {
                owner._active = null;
                Volatile.Write(ref owner._busy, 0);
            }
        }
    }
}
