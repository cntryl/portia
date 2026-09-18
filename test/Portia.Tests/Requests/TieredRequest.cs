namespace Cntryl.Portia;

sealed record TieredRequest(Uuid AccountId) : IRequest, IAccountRequest, IMfaConfirmedRequest;
