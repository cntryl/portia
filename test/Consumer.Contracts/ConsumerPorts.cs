namespace Cntryl.Portia.Consumer;

public interface IConsumerScope
{
    Guid Id { get; }
}

public interface IConsumerEffects
{
    void Record(string component, Uuid aggregateId, int amount, Guid scopeId);
}

public interface IAccountRepository : IProjectionStore
{
    void Add(Uuid aggregateId, int amount, Guid scopeId);
}
