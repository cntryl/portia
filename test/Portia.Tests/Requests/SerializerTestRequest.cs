namespace Cntryl.Portia;

sealed record SerializerTestRequest(string Name, int Count) : IRequest;
