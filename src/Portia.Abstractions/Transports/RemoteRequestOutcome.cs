namespace Cntryl.Portia;

/// <summary>Identifies how an out-of-process request send ended.</summary>
public enum RemoteRequestOutcomeKind
{
    /// <summary>A final response was received and decoded.</summary>
    Completed,

    /// <summary>The transport confirmed that the request did not reach a handler.</summary>
    NotDispatched,

    /// <summary>The transport confirmed that it forwarded a cancellation signal.</summary>
    CancellationForwarded,

    /// <summary>The request may have been dispatched, but no final response was received.</summary>
    OutcomeUnknown
}

/// <summary>The outcome of sending a remote request that has no result payload.</summary>
/// <remarks>
///     A completed case contains both successful and failed request results. Forwarding a cancellation signal does
///     not confirm that the remote handler received it, stopped, or had its effects rolled back. Reconcile effects
///     when the outcome is <see cref="OutcomeUnknown" />.
/// </remarks>
public abstract class RemoteRequestOutcome
{
    private protected RemoteRequestOutcome()
    {
    }

    /// <summary>Gets the outcome case.</summary>
    public abstract RemoteRequestOutcomeKind Kind { get; }

    /// <summary>A final response was received and decoded.</summary>
    /// <param name="result">The successful or failed request result.</param>
    public sealed class Completed(Result result) : RemoteRequestOutcome
    {
        /// <summary>Gets the successful or failed request result.</summary>
        public Result Result { get; } = result;

        /// <inheritdoc />
        public override RemoteRequestOutcomeKind Kind => RemoteRequestOutcomeKind.Completed;
    }

    /// <summary>The transport confirmed that the request did not reach a handler.</summary>
    public sealed class NotDispatched : RemoteRequestOutcome
    {
        /// <inheritdoc />
        public override RemoteRequestOutcomeKind Kind => RemoteRequestOutcomeKind.NotDispatched;
    }

    /// <summary>The transport confirmed that it forwarded the cancellation signal.</summary>
    /// <remarks>The signal may not reach the remote handler. It may continue running, and its effects may already have happened.</remarks>
    public sealed class CancellationForwarded : RemoteRequestOutcome
    {
        /// <inheritdoc />
        public override RemoteRequestOutcomeKind Kind => RemoteRequestOutcomeKind.CancellationForwarded;
    }

    /// <summary>The request may have been dispatched, but no final response was received.</summary>
    public sealed class OutcomeUnknown : RemoteRequestOutcome
    {
        /// <inheritdoc />
        public override RemoteRequestOutcomeKind Kind => RemoteRequestOutcomeKind.OutcomeUnknown;
    }
}

/// <summary>The outcome of sending a remote request that returns a value.</summary>
/// <typeparam name="TOut">The type returned when the remote request completed.</typeparam>
/// <remarks>
///     A completed case contains both successful and failed request results. Forwarding a cancellation signal does
///     not confirm that the remote handler received it, stopped, or had its effects rolled back. Reconcile effects
///     when the outcome is <see cref="OutcomeUnknown" />.
/// </remarks>
public abstract class RemoteRequestOutcome<TOut>
{
    private protected RemoteRequestOutcome()
    {
    }

    /// <summary>Gets the outcome case.</summary>
    public abstract RemoteRequestOutcomeKind Kind { get; }

    /// <summary>A final response was received and decoded.</summary>
    /// <param name="result">The successful or failed request result.</param>
    public sealed class Completed(Result<TOut> result) : RemoteRequestOutcome<TOut>
    {
        /// <summary>Gets the successful or failed request result.</summary>
        public Result<TOut> Result { get; } = result;

        /// <inheritdoc />
        public override RemoteRequestOutcomeKind Kind => RemoteRequestOutcomeKind.Completed;
    }

    /// <summary>The transport confirmed that the request did not reach a handler.</summary>
    public sealed class NotDispatched : RemoteRequestOutcome<TOut>
    {
        /// <inheritdoc />
        public override RemoteRequestOutcomeKind Kind => RemoteRequestOutcomeKind.NotDispatched;
    }

    /// <summary>The transport confirmed that it forwarded the cancellation signal.</summary>
    /// <remarks>The signal may not reach the remote handler. It may continue running, and its effects may already have happened.</remarks>
    public sealed class CancellationForwarded : RemoteRequestOutcome<TOut>
    {
        /// <inheritdoc />
        public override RemoteRequestOutcomeKind Kind => RemoteRequestOutcomeKind.CancellationForwarded;
    }

    /// <summary>The request may have been dispatched, but no final response was received.</summary>
    public sealed class OutcomeUnknown : RemoteRequestOutcome<TOut>
    {
        /// <inheritdoc />
        public override RemoteRequestOutcomeKind Kind => RemoteRequestOutcomeKind.OutcomeUnknown;
    }
}
