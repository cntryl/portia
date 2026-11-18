namespace Cntryl.Portia;

// Reading default(Result) is a programming error in the component that produced it. The exception
// is marked so a runner that only sees it surface from application code can still tell it apart
// from an ordinary invalid operation, name the component, and stop instead of retrying.
static class UninitializedResult
{
    const string Marker = "Portia.UninitializedResult";

    internal static InvalidOperationException Exception() =>
        Mark(new InvalidOperationException("The result is uninitialized."));

    internal static InvalidOperationException ReturnedBy(string role, Type owner, Exception inner) =>
        Mark(new InvalidOperationException($"Portia {role} '{owner.FullName}' returned an uninitialized Result.",
            inner));

    internal static bool Is(Exception? exception) =>
        exception is InvalidOperationException && exception.Data.Contains(Marker);

    static InvalidOperationException Mark(InvalidOperationException exception)
    {
        exception.Data[Marker] = true;
        return exception;
    }
}
