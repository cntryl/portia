namespace Cntryl.Portia;

sealed record FitzScheduledRequestEnvelope(
    int Version,
    string SystemSubject,
    string SystemIssuer,
    byte[] RequestEnvelope);
