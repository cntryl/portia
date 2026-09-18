using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

static class RequestEnvelopeFailure
{
    static readonly ConditionalWeakTable<Exception, Classification> Kinds = [];

    public static InvalidOperationException Create(RequestEnvelopeFailureKind kind, string message,
        Exception? innerException = null)
    {
        var exception = new InvalidOperationException(message, innerException);
        Kinds.Add(exception, new Classification(kind));
        return exception;
    }

    public static RequestEnvelopeFailureKind? GetKind(Exception exception) =>
        Kinds.TryGetValue(exception, out var classification) ? classification.Kind : null;

    public static T Classify<T>(T exception, RequestEnvelopeFailureKind kind) where T : Exception
    {
        Kinds.Add(exception, new Classification(kind));
        return exception;
    }

    sealed record Classification(RequestEnvelopeFailureKind Kind);
}
