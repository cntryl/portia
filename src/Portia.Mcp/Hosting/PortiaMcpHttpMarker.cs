namespace Cntryl.Portia;

/// <summary>Marks an application that serves its MCP tools over ASP.NET Core HTTP.</summary>
sealed class PortiaMcpHttpMarker
{
    static readonly AsyncLocal<bool> Current = new();

    internal static bool IsCurrentRequest => Current.Value;

    internal static async Task InvokeAsync(Func<Task> next)
    {
        var previous = Current.Value;
        Current.Value = true;
        try
        {
            await next().ConfigureAwait(false);
        }
        finally
        {
            Current.Value = previous;
        }
    }
}
