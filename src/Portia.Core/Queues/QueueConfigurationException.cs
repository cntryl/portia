namespace Cntryl.Portia;

sealed class QueueConfigurationException(Type missingServiceType)
    : InvalidOperationException(
        $"QueueRunner requires the service '{missingServiceType.FullName}' before queue consumption can begin.")
{
    public Type MissingServiceType { get; } = missingServiceType;
}
