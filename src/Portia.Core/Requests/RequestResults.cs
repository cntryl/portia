namespace Cntryl.Portia;

// A component that returns default(Result) has not decided anything. Every stage of dispatch rejects
// that here, naming the application's own component so the mistake is found where it was made.
static class RequestResults
{
    internal static void Validate(Result result, Type owner, string role)
    {
        try
        {
            _ = result.IsSuccess;
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"Portia {role} '{owner.FullName}' returned an uninitialized Result.", ex);
        }
    }

    internal static void Validate<TOut>(Result<TOut> result, Type owner, string role)
    {
        try
        {
            _ = result.IsSuccess;
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"Portia {role} '{owner.FullName}' returned an uninitialized Result<{typeof(TOut).FullName}>.", ex);
        }
    }
}
