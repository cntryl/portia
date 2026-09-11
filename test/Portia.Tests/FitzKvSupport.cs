using System.Text;
using Cntryl.Fitz.Abstractions.Domains.Kv;

namespace Cntryl.Portia;

/// <summary>
///     An in-memory <see cref="IKvClient" /> for the Fitz KV adapter tests, with injectable failures at
///     every point the adapters have to clean up after: the staged put, the commit, the rollback that
///     follows a failure, and the transaction's own disposal.
/// </summary>
sealed class FakeKvClient : IKvClient
{
    public Dictionary<string, byte[]> Committed { get; } = new(StringComparer.Ordinal);

    public List<FakeKvTransaction> Transactions { get; } = [];

    public Exception? PutFailure { get; set; }

    public Exception? CommitFailure { get; set; }

    public Exception? RollbackFailure { get; set; }

    public Exception? DisposeFailure { get; set; }

    public FakeKvTransaction LastTransaction => Transactions[^1];

    public Task<IKvTransaction> BeginAsync(string route, KvDurability durability, KvMode mode = KvMode.ReadWrite,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transaction = new FakeKvTransaction(this, route, durability, mode);
        Transactions.Add(transaction);
        return Task.FromResult<IKvTransaction>(transaction);
    }

    public Task<KvSubscription> SubscribeAsync(string pattern, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <summary>Reads a committed value by its key bytes, as the adapters encode them.</summary>
    public byte[]? Read(ReadOnlyMemory<byte> key) =>
        Committed.GetValueOrDefault(Encoding.UTF8.GetString(key.Span));
}

/// <summary>One transaction opened against <see cref="FakeKvClient" />.</summary>
sealed class FakeKvTransaction(FakeKvClient client, string route, KvDurability durability, KvMode mode)
    : IKvTransaction
{
    readonly Dictionary<string, byte[]> _staged = new(StringComparer.Ordinal);

    public string Route => route;

    public KvDurability Durability => durability;

    public KvMode Mode => mode;

    public int Commits { get; private set; }

    public int Rollbacks { get; private set; }

    public int Disposals { get; private set; }

    public Task<KvGetResult> GetAsync(ReadOnlyMemory<byte> key, CancellationToken ct = default)
    {
        var name = Encoding.UTF8.GetString(key.Span);
        return Task.FromResult(_staged.TryGetValue(name, out var staged)
            ? new KvGetResult(true, staged)
            : client.Committed.TryGetValue(name, out var committed)
                ? new KvGetResult(true, committed)
                : new KvGetResult(false, null));
    }

    public Task PutAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, CancellationToken ct = default)
    {
        if (client.PutFailure is { } failure)
        {
            return Task.FromException(failure);
        }

        _staged[Encoding.UTF8.GetString(key.Span)] = value.ToArray();
        return Task.CompletedTask;
    }

    public Task InsertAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, CancellationToken ct = default) =>
        PutAsync(key, value, ct);

    public Task DeleteAsync(ReadOnlyMemory<byte> key, CancellationToken ct = default)
    {
        _ = _staged.Remove(Encoding.UTF8.GetString(key.Span));
        return Task.CompletedTask;
    }

    public Task DeleteRangeAsync(ReadOnlyMemory<byte> startKey, ReadOnlyMemory<byte> endKey,
        CancellationToken ct = default) => throw new NotSupportedException();

    public Task<KvScanResult> ScanAsync(KvScanQuery query, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task CommitAsync(CancellationToken ct = default)
    {
        Commits++;
        if (client.CommitFailure is { } failure)
        {
            return Task.FromException(failure);
        }

        foreach (var (key, value) in _staged)
            client.Committed[key] = value;
        _staged.Clear();
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken ct = default)
    {
        Rollbacks++;
        _staged.Clear();
        return client.RollbackFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposals++;
        return client.DisposeFailure is { } failure
            ? ValueTask.FromException(failure)
            : ValueTask.CompletedTask;
    }
}
