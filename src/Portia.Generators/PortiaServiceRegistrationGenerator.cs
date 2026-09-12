using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Cntryl.Portia;

/// <summary>Generates typed transport descriptors for explicitly selected request contracts.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class PortiaServiceRegistrationGenerator : IIncrementalGenerator
{
    static readonly DiagnosticDescriptor InvalidDiscriminator = new("PORTIA020",
        "Missing or invalid request discriminator",
        "Transported request '{0}' must declare [Discriminator(\"name\", version)] with a non-empty name and positive version",
        "Portia", DiagnosticSeverity.Error, true);

    static readonly DiagnosticDescriptor DuplicateDiscriminator = new("PORTIA022", "Duplicate request discriminator",
        "Request CLR types '{0}' and '{1}' both declare discriminator '{2}' version {3}", "Portia",
        DiagnosticSeverity.Error, true);

    static readonly DiagnosticDescriptor InvalidRoute = new("PORTIA024", "Invalid request route segment",
        "Transported request '{0}' declares invalid route segment '{1}'; use '*' or letters, digits, '.', '_', '-', and '~'",
        "Portia", DiagnosticSeverity.Error, true);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var requests = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => RequestTransportDiscovery.IsCandidate(node),
                static (syntaxContext, _) => RequestTransportDiscovery.GetRequestTransportComponent(syntaxContext))
            .Where(static request => request is not null)
            .Select(static (request, _) => request!)
            .Collect();
        var invalidDiscriminators = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => RequestTransportDiscovery.IsCandidate(node),
                static (syntaxContext, _) => RequestTransportDiscovery.GetInvalidRequestDiscriminator(syntaxContext))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect();
        context.RegisterSourceOutput(invalidDiscriminators, static (sourceContext, invalid) =>
        {
            foreach (var model in invalid)
                sourceContext.ReportDiagnostic(Diagnostic.Create(InvalidDiscriminator, model.Location.ToLocation(), model.TypeName));
        });
        var invalidRoutes = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => RequestTransportDiscovery.IsCandidate(node),
                static (syntaxContext, _) => RequestTransportDiscovery.GetInvalidRoute(syntaxContext))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect();
        context.RegisterSourceOutput(invalidRoutes, static (sourceContext, invalid) =>
        {
            foreach (var model in invalid)
            {
                sourceContext.ReportDiagnostic(Diagnostic.Create(InvalidRoute, model.Location.ToLocation(), model.TypeName,
                    model.Segment));
            }
        });
        var components = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax,
                static (syntaxContext, _) => RegistrationComponentDiscovery.GetComponentTypeName(syntaxContext))
            .Where(static component => component is not null)
            .Select(static (component, _) => component!)
            .Collect();
        context.RegisterSourceOutput(requests.Combine(components),
            static (sourceContext, pair) => Generate(sourceContext, pair.Left, pair.Right));
    }

    static void Generate(
        SourceProductionContext context,
        ImmutableArray<RequestTransportComponent> requests,
        ImmutableArray<string> components)
    {
        if (requests.IsDefaultOrEmpty)
        {
            return;
        }

        var ordered = requests
            .GroupBy(request => request.TypeName, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(request => request.TypeName, StringComparer.Ordinal)
            .ToArray();
        foreach (var group in ordered.GroupBy(request => (request.DiscriminatorName, request.DiscriminatorVersion)))
        {
            var declarations = group.OrderBy(request => request.Location.Path, StringComparer.Ordinal)
                .ThenBy(request => request.Location.SpanStart).ToArray();
            var original = declarations[0];
            foreach (var duplicate in declarations.Skip(1))
            {
                context.ReportDiagnostic(Diagnostic.Create(DuplicateDiscriminator, duplicate.Location.ToLocation(),
                    Display(original.TypeName), Display(duplicate.TypeName), group.Key.DiscriminatorName,
                    group.Key.DiscriminatorVersion));
            }
        }

        var names = GeneratedRegistrationNames.Resolve(ordered.Select(request => request.TypeName).Concat(components));

        var source = new StringBuilder()
            .AppendLine("// <auto-generated />")
            .AppendLine("#nullable enable")
            .AppendLine("namespace Cntryl.Portia;")
            .AppendLine("internal static partial class PortiaGeneratedRegistrations")
            .AppendLine("{");

        foreach (var request in ordered)
        {
            _ = source.Append("    /// <summary>Registers the transports <see cref=\"")
                .Append(request.TypeName.StartsWith("global::", StringComparison.Ordinal)
                    ? request.TypeName.Substring(8)
                    : request.TypeName)
                .AppendLine("\" /> declares.</summary>")
                .Append("    public static global::Cntryl.Portia.PortiaBuilder ").Append(names[request.TypeName])
                .AppendLine("(this global::Cntryl.Portia.PortiaBuilder builder)")
                .AppendLine("    {")
                .AppendLine("        global::System.ArgumentNullException.ThrowIfNull(builder);")
                .Append("        return builder.AddGeneratedRequest(");
            RequestTransportRegistrationEmitter.AppendConstruction(source, request);
            _ = source.AppendLine(");").AppendLine("    }");
        }

        _ = source.AppendLine("}");
        context.AddSource("PortiaGeneratedServiceCollectionExtensions.g.cs",
            SourceText.From(source.ToString(), Encoding.UTF8));
    }

    static string Display(string typeName) => typeName.StartsWith("global::", StringComparison.Ordinal)
        ? typeName.Substring(8)
        : typeName;
}
