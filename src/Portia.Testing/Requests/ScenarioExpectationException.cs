namespace Cntryl.Portia.Testing;

/// <summary>Indicates that a request scenario did not meet one or more of its expectations.</summary>
/// <param name="message">Every unmet expectation, followed by the observed lifecycle and result.</param>
public sealed class ScenarioExpectationException(string message) : Exception(message);
