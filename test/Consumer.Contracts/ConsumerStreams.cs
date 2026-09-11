namespace Cntryl.Portia.Consumer;

/// <summary>
///     The one place the consumer fixture's event-stream identity is declared, exactly as a real
///     application would declare it once and share it between its aggregate and the projectors and
///     reactors that read it.
///     <para>
///         The realm carries a per-process suffix because the fixture's stream store is durable while
///         its checkpoints are not: a broker that outlives the test run keeps every earlier run's
///         events in this area, and the next run's projectors and reactors start from checkpoint zero
///         and replay all of it. The documented workflow removes the broker between runs
///         (<c>docker compose down --volumes</c>), so this only matters to a developer reusing a
///         long-lived local broker — for whom the suite would otherwise get slower every run, without
///         bound.
///     </para>
/// </summary>
public static class ConsumerStreams
{
    /// <summary>Gets the fixture's stream realm, unique to this test process.</summary>
    public static string Realm { get; } = "consumer-" + Guid.NewGuid().ToString("N");

    /// <summary>Gets the area every account stream and audit session belongs to.</summary>
    public const string Accounts = "accounts";

    /// <summary>Gets the pattern the fixture's projectors and reactors consume.</summary>
    public static EventStreamPattern AccountsPattern { get; } = EventStreamPattern.ForPattern(Realm, Accounts);

    /// <summary>Builds one account's stream address within the fixture's area.</summary>
    /// <param name="id">The account's identity.</param>
    /// <returns>The account's stream address.</returns>
    public static EventStreamAddress AccountStream(Uuid id) => new(Realm, Accounts, id.ToString());
}
