namespace Cntryl.Portia;

/// <summary>Configures a fleet whose workers share a membership selector and fixed partition set.</summary>
public sealed record FleetRunOptions
{
    /// <summary>Gets the dedicated membership selector, shaped lease://realm/area/*.</summary>
    public required string MembershipSelector { get; init; }
    /// <summary>Gets the worker ID, or null to generate one UUIDv4 per run.</summary>
    public string? WorkerId { get; init; }
    /// <summary>Gets the renewable membership and partition lease TTL; defaults to 30 seconds.</summary>
    public TimeSpan LeaseTtl { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Gets the authoritative snapshot reconciliation interval; defaults to one second.</summary>
    public TimeSpan ReconciliationInterval { get; init; } = TimeSpan.FromSeconds(1);

    internal ulong TtlSeconds => checked((ulong)Math.Ceiling(LeaseTtl.TotalSeconds));

    internal void Validate(IReadOnlyCollection<string> partitions)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(LeaseTtl, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ReconciliationInterval, TimeSpan.Zero);
        var segments = MembershipSelector?.StartsWith("lease://", StringComparison.Ordinal) == true
            ? MembershipSelector[8..].Split('/') : [];
        if (segments.Length != 3 || !IsSegment(segments[0]) || !IsSegment(segments[1]) || segments[2] != "*")
            throw new ArgumentException("Membership selector must be lease://realm/area/*.");
        if (WorkerId is not null && !IsSegment(WorkerId))
            throw new ArgumentException("Worker ID must be a nonblank exact lease resource segment.");
        var prefix = MembershipSelector![..^1];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var partition in partitions)
        {
            var parts = partition?.StartsWith("lease://", StringComparison.Ordinal) == true ? partition[8..].Split('/') : [];
            if (parts.Length != 3 || !parts.All(IsSegment))
                throw new ArgumentException("Partition must be lease://{realm}/{area}/{resource} with exact nonblank segments.", nameof(partitions));
            if (partition!.StartsWith(prefix, StringComparison.Ordinal))
                throw new ArgumentException("Membership must use a dedicated area outside partition routes.", nameof(partitions));
            if (!seen.Add(partition))
                throw new ArgumentException($"Partition '{partition}' appears more than once.", nameof(partitions));
        }
    }

    internal static bool IsSegment(string value) => !string.IsNullOrWhiteSpace(value)
        && !value.Any(char.IsWhiteSpace) && !value.Contains('/') && !value.Contains('*');
}
