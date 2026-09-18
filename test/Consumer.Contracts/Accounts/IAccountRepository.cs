namespace Cntryl.Portia.Consumer;

public interface IAccountRepository : IProjectionStore
{
    void Add(Uuid aggregateId, int amount, Guid scopeId);
}
