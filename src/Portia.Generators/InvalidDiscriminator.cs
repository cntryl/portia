namespace Cntryl.Portia;

sealed record InvalidDiscriminator(string TypeName, DiagnosticLocation Location);
