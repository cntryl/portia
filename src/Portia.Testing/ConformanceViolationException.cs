namespace Cntryl.Portia;

/// <summary>Indicates that an application implementation violated a Portia conformance invariant.</summary>
/// <param name="message">The invariant that the implementation violated.</param>
public sealed class ConformanceViolationException(string message) : Exception(message);
