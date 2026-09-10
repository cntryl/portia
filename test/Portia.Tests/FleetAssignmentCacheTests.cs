using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Cntryl.Portia;

/// <summary>Verifies cached fleet ownership preserves the rendezvous contract.</summary>
public sealed class FleetAssignmentCacheTests
{
    /// <summary>Membership order and fixed partition changes match an independent implementation.</summary>
    [Fact]
    public void ShouldMatchIndependentRendezvousImplementationAcrossChanges()
    {
        var cache = new FleetAssignmentCache();
        var random = new Random(8421);
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var workers = Enumerable.Range(0, random.Next(1, 8)).Select(index => $"worker-{index}")
                .OrderBy(_ => random.Next()).ToArray();
            var partitions = Enumerable.Range(0, random.Next(1, 80)).Select(index => $"lease://p/{index}").ToArray();
            foreach (var worker in workers)
            {
                var expected = partitions.Where(partition => LegacyOwner(partition, workers) == worker)
                    .ToHashSet(StringComparer.Ordinal);
                Assert.Equal(expected, cache.GetAssignments(partitions, workers, worker));
            }
        }
    }

    static string? LegacyOwner(string partition, IEnumerable<string> workers)
    {
        string? winner = null;
        byte[]? greatest = null;
        var route = Encoding.UTF8.GetBytes(partition);
        foreach (var worker in workers)
        {
            var id = Encoding.UTF8.GetBytes(worker);
            var input = new byte[8 + route.Length + id.Length];
            BinaryPrimitives.WriteInt32BigEndian(input, route.Length);
            route.CopyTo(input, 4);
            BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(4 + route.Length), id.Length);
            id.CopyTo(input, 8 + route.Length);
            var digest = SHA256.HashData(input);
            var comparison = greatest is null ? 1 : digest.AsSpan().SequenceCompareTo(greatest);
            if (comparison > 0 || comparison == 0 && string.CompareOrdinal(worker, winner) > 0)
            {
                greatest = digest;
                winner = worker;
            }
        }
        return winner;
    }
}
