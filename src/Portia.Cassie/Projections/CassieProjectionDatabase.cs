using System.Runtime.ExceptionServices;
using Cntryl.Cassie;
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

    /// <summary>Creates the metadata table once and verifies that an existing table can hold Portia's metadata.</summary>
    /// <param name="ct">Cancels initialization or waiting for another initializer.</param>
    /// <returns>A task that completes once metadata can be used.</returns>
    /// <exception cref="NotSupportedException">An existing table does not match the metadata schema.</exception>
    /// <remarks>Failed initialization remains retryable.</remarks>
    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _initialization.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;
            await using var connection = await DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using (var create = connection.CreateCommand())
            {
                create.CommandText = $"CREATE TABLE IF NOT EXISTS {CheckpointTable} (identity TEXT PRIMARY KEY, cursor TEXT NOT NULL, revision BIGINT NOT NULL)";
                _ = await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await VerifySchemaAsync(connection, ct).ConfigureAwait(false);
            _initialized = true;
        }
        finally { _initialization.Release(); }
    }

    // Exercises the statements batches depend on inside a discarded transaction. The empty identity is
    // never a projection key, so the probe row cannot collide with real progress.
    async ValueTask VerifySchemaAsync(CassieConnection connection, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        Exception? failure = null;
        try
        {
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = $"INSERT INTO {CheckpointTable} (identity, cursor, revision) VALUES ('', '', 0) ON CONFLICT (identity) DO NOTHING";
                _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = $"SELECT identity, cursor, revision FROM {CheckpointTable} WHERE identity = ''";
            await using var row = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await row.ReadAsync(ct).ConfigureAwait(false) || row.GetFieldType(0) != typeof(string)
                || row.GetFieldType(1) != typeof(string) || row.GetFieldType(2) != typeof(long))
                failure = Incompatible(null);
        }
        catch (CassieException ex) when (!ex.IsTransient) { failure = Incompatible(ex); }
        catch (Exception ex) { failure = ex; }

        try
        { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception cleanup) when (failure is not null) { failure.Data["Portia.CleanupException"] = cleanup; }
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    NotSupportedException Incompatible(Exception? inner) => new(
        $"Cassie checkpoint table {CheckpointTable} is incompatible; it must be "
        + "(identity TEXT PRIMARY KEY, cursor TEXT NOT NULL, revision BIGINT NOT NULL). Rename or drop it, "
        + "or configure another checkpoint table.", inner);

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
