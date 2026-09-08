namespace Cntryl.Portia;

/// <summary>Marks an application-owned source-generated JSON context for Portia composition.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PortiaJsonContextAttribute : Attribute;
