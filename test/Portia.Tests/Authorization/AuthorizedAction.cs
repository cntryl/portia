namespace Cntryl.Portia;

sealed record AuthorizedAction(int OwnerId) : IRequest;
