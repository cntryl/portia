namespace Cntryl.Portia.Consumer;

/// <summary>A metadata-only contract used to prove referenced contexts compose at runtime.</summary>
public sealed record ReferencedContractJsonPayload(string Value);
