namespace Cntryl.Portia;

sealed record FitzHostedRequest : IRequest, ICallable, IQueuable, INotifiable, ISchedulable;
