using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cntryl.Portia;

// Application conventions are opt-in. Portia's own public namespaces and package layout are
// framework contracts, and are not an application of the Common/Core/Api/Worker convention.
sealed class ApplicationProject(string directory, string name, string rootNamespace, bool isTest)
{
    public string Name { get; } = name;
    public string RootNamespace { get; } = rootNamespace;
    public bool IsTest { get; } = isTest;
    public string Role { get; } = name.Split('.').Last();
    public string ApplicationName { get; } = name.Contains('.') ? name.Substring(0, name.LastIndexOf('.')) : name;

    public static ApplicationProject? From(AnalyzerConfigOptionsProvider options)
    {
        var global = options.GlobalOptions;
        if (!global.TryGetValue("build_property.PortiaProjectStructure", out var enabled)
            || !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase)
            || !global.TryGetValue("build_property.MSBuildProjectDirectory", out var directory)
            || !global.TryGetValue("build_property.MSBuildProjectName", out var name)
            || !global.TryGetValue("build_property.RootNamespace", out var rootNamespace)
            || string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(rootNamespace))
        {
            return null;
        }

        _ = global.TryGetValue("build_property.IsTestProject", out var isTest);
        return new ApplicationProject(directory.Replace('\\', '/').TrimEnd('/'), name, rootNamespace,
            string.Equals(isTest, "true", StringComparison.OrdinalIgnoreCase));
    }

    public string[] RelativePath(SyntaxTree tree)
    {
        var path = tree.FilePath.Replace('\\', '/');
        // Linked files outside the project have no logical Link path in Roslyn's model.
        // Do not invent one or perform filesystem I/O in an analyzer.
        return path.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase)
            ? path.Substring(directory.Length + 1).Split('/')
            : [];
    }

    public static bool DerivesFrom(INamedTypeSymbol symbol, string name)
    {
        for (var current = symbol.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolNames.Is(current, "Cntryl.Portia." + name))
                return true;
        }

        return false;
    }

    public static INamedTypeSymbol[] Requests(INamedTypeSymbol symbol) =>
        [.. symbol.AllInterfaces.Where(iface => PortiaComponentRoles.Is(iface, PortiaComponentRoles.Handler))];

    public static bool IsRequest(INamedTypeSymbol symbol) =>
        symbol.AllInterfaces.Any(iface => SymbolNames.Is(iface, "Cntryl.Portia.IRequestBase"));

    public static bool IsReactor(INamedTypeSymbol symbol) => DerivesFrom(symbol, "Reactor")
        || symbol.AllInterfaces.Any(iface => SymbolNames.Is(iface, "Cntryl.Portia.IReactorHandler`1")
                                            || SymbolNames.Is(iface, "Cntryl.Portia.IBatchReactorHandler`1"));

    public static bool IsProjector(INamedTypeSymbol symbol) => DerivesFrom(symbol, "Projector")
        || symbol.AllInterfaces.Any(iface => SymbolNames.Is(iface, "Cntryl.Portia.IProjectorHandler`1")
                                            || SymbolNames.Is(iface, "Cntryl.Portia.IBatchProjectorHandler`1"));

    public static bool IsContractIn(ITypeSymbol symbol, string folder)
    {
        var space = symbol.ContainingNamespace;
        return space is not null && space.Name == folder
            && space.ContainingNamespace?.ContainingNamespace?.Name == "Features";
    }
}
