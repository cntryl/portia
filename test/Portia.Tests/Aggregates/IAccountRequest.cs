namespace Cntryl.Portia;

interface IAccountRequest : IRequestBase
{
    Uuid AccountId { get; }
}
