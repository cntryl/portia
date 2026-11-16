using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Cntryl.Portia;

/// <summary>Compile-time contract facts shared by Portia's execution and transport adapters.</summary>
/// <param name="type">The contract CLR type.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class PortiaContractMetadata(Type type)
{
    /// <summary>Gets the contract CLR type.</summary>
    public Type Type { get; } = type;
    /// <summary>Gets whether the provider was compiled alongside this type definition.</summary>
    public bool IsSourceDefinition { get; init; }
    /// <summary>Gets the reusable contract description.</summary>
    public string? Description { get; init; }
    /// <summary>Gets the human-readable contract title.</summary>
    public string? Title { get; init; }
    /// <summary>Gets whether the contract is deprecated.</summary>
    public bool Deprecated { get; init; }
    /// <summary>Gets the deprecation explanation.</summary>
    public string? DeprecationMessage { get; init; }
    /// <summary>Gets whether the generated contract graph contains value validation.</summary>
    public bool HasValidation { get; init; }
    /// <summary>Gets the declared collection item or dictionary value type.</summary>
    public Type? ElementType { get; init; }
    /// <summary>Gets generated member facts and accessors.</summary>
    public IReadOnlyList<PortiaContractMemberMetadata> Members { get; init; } = [];
    /// <summary>Gets generated collection items or dictionary values for recursive validation.</summary>
    public Func<object, IEnumerable<object?>>? Elements { get; init; }
}

/// <summary>Compile-time facts about a contract member, independent of HTTP or MCP.</summary>
/// <param name="name">The CLR member name.</param>
/// <param name="type">The member's CLR type.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class PortiaContractMemberMetadata(string name, Type type)
{
    /// <summary>Gets the CLR member name.</summary>
    public string Name { get; } = name;
    /// <summary>Gets the member's CLR type.</summary>
    public Type Type { get; } = type;
    /// <summary>Gets an explicit serializer property name, if declared.</summary>
    public string? JsonName { get; init; }
    /// <summary>Gets the reusable member description.</summary>
    public string? Description { get; init; }
    /// <summary>Gets the human-readable member title.</summary>
    public string? Title { get; init; }
    /// <summary>Gets whether the member is deprecated.</summary>
    public bool Deprecated { get; init; }
    /// <summary>Gets the deprecation explanation.</summary>
    public string? DeprecationMessage { get; init; }
    /// <summary>Gets whether the member lies on a generated validation path.</summary>
    public bool HasValidation { get; init; }
    /// <summary>Gets the generated value accessor.</summary>
    public Func<object, object?>? GetValue { get; init; }
    /// <summary>Gets a generated string or collection count accessor for length validation.</summary>
    public Func<object, int>? Count { get; init; }
    /// <summary>Gets validation rules constructed from standard attributes at compile time.</summary>
    public IReadOnlyList<ValidationAttribute> Rules { get; init; } = [];
    /// <summary>Gets whether a documentation default was declared.</summary>
    public bool HasDefault { get; init; }
    /// <summary>Gets the declared documentation default.</summary>
    public object? DefaultValue { get; init; }
    /// <summary>Gets whether a constructor parameter binds this member.</summary>
    public bool ConstructorBound { get; init; }
    /// <summary>Gets whether constructor binding or a known initializer supplies a default.</summary>
    public bool HasBindingDefault { get; init; }
    /// <summary>Gets the constructor binding default.</summary>
    public object? BindingDefaultValue { get; init; }
    /// <summary>Gets whether declared serializer annotations permit the member in input.</summary>
    public bool Input { get; init; } = true;
    /// <summary>Gets whether declared serializer annotations permit the member in output.</summary>
    public bool Output { get; init; } = true;
}
