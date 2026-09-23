namespace Cntryl.Portia;

sealed record InaccessibleRequest(string TypeName, string Reason, DiagnosticLocation Location);
