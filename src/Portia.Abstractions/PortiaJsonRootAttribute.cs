using System.ComponentModel;

namespace Cntryl.Portia;

/// <summary>Advertises a source-generated JSON root and its owning Portia context to referencing assemblies.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class PortiaJsonRootAttribute(Type rootType, Type contextType, Type factoryType) : Attribute
{
    /// <summary>Gets the serializer root supplied by the context.</summary>
    public Type RootType { get; } = rootType;

    /// <summary>Gets the source-generated context that owns the root.</summary>
    public Type ContextType { get; } = contextType;

    /// <summary>Gets the generated public factory that can construct the owning context.</summary>
    public Type FactoryType { get; } = factoryType;
}
