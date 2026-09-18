namespace Cntryl.Portia;

sealed record InvalidRoute(string TypeName, string Segment, DiagnosticLocation Location);
