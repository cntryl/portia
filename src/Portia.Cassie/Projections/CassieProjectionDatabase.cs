using Cntryl.Cassie.Connection;

namespace Cntryl.Portia;

/// <summary>Initializes shared projection metadata in the same Cassie database as application read models.</summary>
/// <remarks>The data source is borrowed and retains its own lifetime. Application schemas remain application-owned.</remarks>
public sealed class CassieProjectionDatabase : IDisposable
{
    readonly SemaphoreSlim _initialization = new(1, 1);
    bool _initialized;
    bool _disposed;

    /// <summary>Creates a shared projection database.</summary>
    /// <param name="dataSource">The application's Cassie data source.</param>
    /// <param name="checkpointTable">An unqualified metadata table name, using ASCII letters, digits, and underscores.</param>
    public CassieProjectionDatabase(CassieDataSource dataSource, string checkpointTable = "portia_projection_checkpoints")
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointTable);
        if (checkpointTable.Length > 63 || !(char.IsAsciiLetter(checkpointTable[0]) || checkpointTable[0] == '_')
            || checkpointTable.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException("Use an unqualified SQL identifier of at most 63 ASCII characters.", nameof(checkpointTable));
        DataSource = dataSource;
        CheckpointTable = "\"" + checkpointTable + "\"";
    }

    /// <summary>Gets the borrowed native Cassie data source.</summary>
    public CassieDataSource DataSource { get; }
    internal string CheckpointTable { get; }

    /// <summary>Creates the metadata table once; failed initialization remains retryable.</summary>
    /// <param name="ct">Cancels initialization or waiting for another initializer.</param>
    /// <returns>A task that completes once metadata can be used.</returns>
    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _initialization.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;
            await using var connection = await DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE IF NOT EXISTS {CheckpointTable} (identity TEXT PRIMARY KEY, cursor TEXT NOT NULL, revision BIGINT NOT NULL)";
            _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _initialized = true;
        }
        finally { _initialization.Release(); }
    }

    /// <summary>Releases initialization resources after repository scopes have stopped.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _initialization.Dispose();
        GC.SuppressFinalize(this);
    }
}
