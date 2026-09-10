using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Cntryl.Portia;

sealed class FleetAssignmentCache
{
    readonly Dictionary<string, string?> _owners = new(StringComparer.Ordinal);
    string[] _workers = [];
    byte[][] _workerIds = [];

    public HashSet<string> GetAssignments(IReadOnlyCollection<string> partitions,
        IEnumerable<string> workers, string workerId)
    {
        var normalized = workers.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!_workers.SequenceEqual(normalized, StringComparer.Ordinal))
        {
            _workers = normalized;
            _workerIds = normalized.Select(Encoding.UTF8.GetBytes).ToArray();
            _owners.Clear();
        }

        var current = partitions.ToHashSet(StringComparer.Ordinal);
        foreach (var departed in _owners.Keys.Where(partition => !current.Contains(partition)).ToArray())
            _owners.Remove(departed);

        var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var partition in current)
        {
            if (!_owners.TryGetValue(partition, out var owner))
            {
                owner = GetOwner(partition);
                _owners.Add(partition, owner);
            }

            if (owner == workerId)
                assigned.Add(partition);
        }

        return assigned;
    }

    string? GetOwner(string partition)
    {
        string? winner = null;
        Span<byte> greatest = stackalloc byte[32];
        Span<byte> digest = stackalloc byte[32];
        var hasWinner = false;
        var route = Encoding.UTF8.GetBytes(partition);
        for (var index = 0; index < _workers.Length; index++)
        {
            var id = _workerIds[index];
            var length = 8 + route.Length + id.Length;
            var input = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                BinaryPrimitives.WriteInt32BigEndian(input, route.Length);
                route.CopyTo(input, 4);
                BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(4 + route.Length), id.Length);
                id.CopyTo(input, 8 + route.Length);
                if (!SHA256.TryHashData(input.AsSpan(0, length), digest, out var written) || written != digest.Length)
                    throw new InvalidOperationException("SHA-256 did not produce the expected digest.");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(input);
            }

            var comparison = hasWinner ? digest.SequenceCompareTo(greatest) : 1;
            if (comparison > 0 || (comparison == 0 && string.CompareOrdinal(_workers[index], winner) > 0))
            {
                digest.CopyTo(greatest);
                winner = _workers[index];
                hasWinner = true;
            }
        }

        return winner;
    }
}
