using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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

    static readonly DiagnosticDescriptor InvalidTransportId = new("PORTIA029", "Invalid request transport ID",
        "Transported request '{0}' declares invalid transport ID '{1}'; use ASCII letters, digits, '.', '-', and '_'",
        "Portia", DiagnosticSeverity.Error, true);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var analyses = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => RequestTransportDiscovery.IsCandidate(node),
                static (syntaxContext, ct) => RequestTransportDiscovery.Analyze(syntaxContext, ct))
            .Where(static analysis => analysis is not null)
            .Select(static (analysis, _) => analysis!);
        var requests = analyses.Select(static (analysis, _) => analysis.Component)
            .Where(static model => model is not null).Select(static (model, _) => model!)
            .WithTrackingName("PortiaRequestTransports").Collect();
        var duplicateCandidates = analyses.Select(static (analysis, _) => analysis.DuplicateCandidate)
            .Where(static model => model is not null).Select(static (model, _) => model!)
            .WithTrackingName("PortiaRequestTransportLocations").Collect();
        context.RegisterSourceOutput(duplicateCandidates, static (sourceContext, candidates) =>
            ReportDuplicateDiscriminators(sourceContext, candidates));
        var invalidDiscriminators = analyses.Select(static (analysis, _) => analysis.Discriminator)
            .Where(static model => model is not null).Select(static (model, _) => model!)
            .WithTrackingName("PortiaInvalidRequestDiscriminators").Collect();
        context.RegisterSourceOutput(invalidDiscriminators, static (sourceContext, invalid) =>
        {
            foreach (var model in invalid)
                sourceContext.ReportDiagnostic(Diagnostic.Create(InvalidDiscriminator, model.Location.ToLocation(),
                    model.TypeName));
        });
        var invalidRoutes = analyses.Select(static (analysis, _) => analysis.Route)
            .Where(static model => model is not null).Select(static (model, _) => model!)
            .WithTrackingName("PortiaInvalidRequestRoutes").Collect();
        context.RegisterSourceOutput(invalidRoutes, static (sourceContext, invalid) =>
        {
            foreach (var model in invalid)
                sourceContext.ReportDiagnostic(Diagnostic.Create(InvalidRoute, model.Location.ToLocation(),
                    model.TypeName, model.Segment));
        });
        var invalidTransportIds = analyses.Select(static (analysis, _) => analysis.TransportId)
            .Where(static model => model is not null).Select(static (model, _) => model!)
            .WithTrackingName("PortiaInvalidRequestTransportIds").Collect();
        context.RegisterSourceOutput(invalidTransportIds, static (sourceContext, invalid) =>
        {
            foreach (var model in invalid)
                sourceContext.ReportDiagnostic(Diagnostic.Create(InvalidTransportId, model.Location.ToLocation(),
                    model.TypeName, model.Id));
        });
        var inaccessibleRequests = analyses.Select(static (analysis, _) => analysis.Inaccessible)
            .Where(static model => model is not null).Select(static (model, _) => model!)
            .Collect();
        context.RegisterSourceOutput(inaccessibleRequests, static (sourceContext, inaccessible) =>
        {
            // A partial request reaches the pipeline once per declaration; report it once.
            foreach (var model in inaccessible.GroupBy(model => model.TypeName, StringComparer.Ordinal)
                         .Select(group => group.First()))
            {
                sourceContext.ReportDiagnostic(Diagnostic.Create(GeneratedTypeShape.Unsupported,
                    model.Location.ToLocation(), model.TypeName, model.Reason));
            }
        });
        var components = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax && SyntaxFilters.HasBaseListOrPartial(node),
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
        var names = GeneratedRegistrationNames.Resolve(ordered.Select(request => request.TypeName).Concat(components));

        foreach (var group in GeneratedSource.Buckets(ordered, request => request.TypeName, context.CancellationToken))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            GenerateGroup(context, group.Items, names, group.Key);
        }
    }

    static void GenerateGroup(SourceProductionContext context, RequestTransportComponent[] ordered,
        Dictionary<string, string> names, string bucket)
    {

        var source = new StringBuilder()
            .AppendLine("// <auto-generated />")
            .AppendLine("#nullable enable")
            .AppendLine("namespace Cntryl.Portia;")
            .AppendLine("internal static partial class PortiaGeneratedRegistrations")
            .AppendLine("{");

        foreach (var request in ordered)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
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
        GeneratedSource.Add(context, "PortiaGeneratedServiceCollectionExtensions" + (bucket.Length == 0 ? string.Empty : "." + bucket) + ".g.cs",
            source.ToString());
    }

    static void ReportDuplicateDiscriminators(SourceProductionContext context,
        ImmutableArray<RequestTransportDiscovery.DuplicateDiscriminatorCandidate> candidates)
    {
        var ordered = candidates.GroupBy(candidate => candidate.TypeName, StringComparer.Ordinal)
            .Select(group => group.OrderBy(candidate => candidate.Location.Path, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Location.SpanStart).First())
            .OrderBy(candidate => candidate.TypeName, StringComparer.Ordinal).ToArray();
        foreach (var group in ordered.GroupBy(candidate => (candidate.DiscriminatorName, candidate.DiscriminatorVersion)))
        {
            var declarations = group.OrderBy(candidate => candidate.Location.Path, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Location.SpanStart).ToArray();
            var original = declarations[0];
            foreach (var duplicate in declarations.Skip(1))
            {
                context.ReportDiagnostic(Diagnostic.Create(DuplicateDiscriminator, duplicate.Location.ToLocation(),
                    Display(original.TypeName), Display(duplicate.TypeName), group.Key.DiscriminatorName,
                    group.Key.DiscriminatorVersion));
            }
        }
    }

    static string Display(string typeName) => typeName.StartsWith("global::", StringComparison.Ordinal)
        ? typeName.Substring(8)
        : typeName;
}
