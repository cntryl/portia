namespace Cntryl.Portia;

/// <summary>
///     Computes the next checkpoint offset for an event-stream pattern's scope.
/// </summary>
static class EventStreamOffsets
{
    public static ulong GetNextOffset(EventStreamPattern pattern, DomainEventRecord record) => pattern.Scope switch
    {
        EventStreamPatternScope.Resource => checked(record.ResourceOffset + 1),
        EventStreamPatternScope.Area => checked((record.AreaOffset ??
                                                 throw new InvalidOperationException(
                                                     "Missing area checkpoint offset.")) + 1),
        EventStreamPatternScope.Realm => checked((record.RealmOffset ??
                                                  throw new InvalidOperationException(
                                                      "Missing realm checkpoint offset.")) + 1),
        _ => throw new ArgumentOutOfRangeException(nameof(pattern))
    };
}
