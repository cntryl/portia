namespace Cntryl.Portia.Consumer;

public interface IConsumerEffects
{
    void Record(string component, Uuid aggregateId, int amount, Guid scopeId);
}
