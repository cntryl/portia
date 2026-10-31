using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Connection;

namespace Cntryl.Portia;

/// <summary>A committed query-side read that holds off this repository's projection batches until it is disposed.</summary>
/// <remarks>
///     The read owns its native connection. Disposing the read, not closing that connection, ends it, so a
///     batch never begins while the read can still issue commands.
/// </remarks>
public sealed class CassieProjectionReadSession : IAsyncDisposable
{
    readonly CassieConnection _connection;
    CassieProjectionActivityGate.ReadLease? _lease;

    internal CassieProjectionReadSession(CassieConnection connection, CassieProjectionActivityGate.ReadLease lease)
    {
        _connection = connection;
        _lease = lease;
    }

    /// <summary>Creates a native command on this read's connection.</summary>
    /// <param name="sql">Application-owned SQL.</param>
    /// <returns>A command the application must dispose.</returns>
    public CassieCommand CreateCommand(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _lease) is null, this);
        var command = (CassieCommand)_connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    /// <summary>Closes the connection and allows this repository's next batch to begin.</summary>
    /// <returns>A task that completes once the read has ended.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _lease, null) is not { } lease)
            return;
        try
        { await _connection.DisposeAsync().ConfigureAwait(false); }
        finally { lease.Dispose(); }
    }
}
