namespace Cntryl.Portia;

// Configuration that no restart can repair, so every restart boundary rethrows it.
sealed class QueueConfigurationException : InvalidOperationException
{
    public QueueConfigurationException(string message) : base(message)
    {
    }
}
