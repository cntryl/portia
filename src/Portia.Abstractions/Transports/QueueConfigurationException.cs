namespace Cntryl.Portia;

// Configuration that no restart can repair, so every restart boundary rethrows it.
sealed class QueueConfigurationException : InvalidOperationException
{
    public QueueConfigurationException(Type missingServiceType)
        : base($"QueueRunner requires the service '{missingServiceType.FullName}' before queue consumption can begin.") =>
        MissingServiceType = missingServiceType;

    public QueueConfigurationException(string message) : base(message)
    {
    }

    public Type? MissingServiceType { get; }
}
