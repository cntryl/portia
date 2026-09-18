namespace Cntryl.Portia;

sealed record InvalidTransportId(string TypeName, string Id, DiagnosticLocation Location);
