namespace Cntryl.Portia;

/// <summary>Configures retained evidence and recovery limits.</summary>
public sealed class OperationalOptions
{
    /// <summary>Gets or sets the immutable plan lifetime; defaults to five minutes.</summary>
    public TimeSpan RepairPlanLifetime { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Gets or sets completed evidence retention; unresolved work is always retained.</summary>
    public TimeSpan CompletedRecordRetention { get; set; } = TimeSpan.FromDays(30);
    /// <summary>Gets or sets the maximum extra metadata observations per attempt.</summary>
    public int MaxTraceEntries { get; set; } = 128;
    /// <summary>Gets or sets the delay while required operational persistence is unavailable.</summary>
    public TimeSpan JournalRetryDelay { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>
    /// Gets or sets whether the host acknowledges a quiesced cutover from legacy checkpoint owners.
    /// Operations rejects activation until this is explicitly true.
    /// </summary>
    public bool QuiescedWorkerCutover { get; set; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RepairPlanLifetime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(CompletedRecordRetention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTraceEntries);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(JournalRetryDelay, TimeSpan.Zero);
    }
}
