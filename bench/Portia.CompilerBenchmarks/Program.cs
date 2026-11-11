using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Cntryl.Portia;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

if (args.Length > 0 && args[0] == "namespace-code-action")
{
    if (args.Length < 2)
        throw new ArgumentException("Usage: namespace-code-action OUTPUT [REPETITIONS=20] [REFERENCES=1000]");
    await NamespaceCodeActionCampaign.RunAsync(args[1], args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 20,
        args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 1000);
    return;
}

if (args.Length > 0 && args[0] == "output-identity")
{
    if (args.Length < 3)
        throw new ArgumentException("Usage: output-identity OUTPUT JSON_SOURCE_GENERATOR_DLL");
    await CompilerCampaign.OutputIdentityAsync(args[1], args[2]);
    return;
}

if (args.Length < 2)
    throw new ArgumentException("Usage: OUTPUT JSON_SOURCE_GENERATOR_DLL [REPETITIONS=20] [WORKLOAD_FILTER]");
await CompilerCampaign.RunAsync(args[0], args[1], args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 20,
    args.Length > 3 ? args[3] : null);

static class CompilerCampaign
{
    static readonly Encoding SnapshotEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    static readonly JsonSerializerOptions OutputJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Latest)
        .WithFeatures([new KeyValuePair<string, string>("InterceptorsNamespaces", "Cntryl.Portia.Generated")]);
    static readonly CSharpCompilationOptions CompilationOptions = new(OutputKind.DynamicallyLinkedLibrary,
        optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable,
        deterministic: true);
    static readonly MetadataReference[] PlatformReferences = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator).Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
    static readonly string[] RuntimeProjects = ["Portia.Abstractions", "Portia.Core", "Portia.Storage.Abstractions",
        "Portia.DependencyInjection", "Portia.AspNetCore", "Portia.Testing"];
    static readonly string[] AnalyzerProjects = ["Portia.Generators", "Portia.AspNetCore.Generators"];
    static readonly Assembly[] GeneratorAssemblies = [typeof(RegistrationCallInterceptorGenerator).Assembly,
        typeof(RequestHttpBindingGenerator).Assembly];
    static readonly Type[] GeneratorTypes = GeneratorAssemblies.SelectMany(assembly => assembly.GetTypes())
        .Where(type => !type.IsAbstract && typeof(IIncrementalGenerator).IsAssignableFrom(type))
        .OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
    static readonly DiagnosticAnalyzer[] Analyzers = GeneratorAssemblies.SelectMany(assembly => assembly.GetTypes())
        .Where(type => !type.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(type))
        .OrderBy(type => type.FullName, StringComparer.Ordinal).Select(type => (DiagnosticAnalyzer)Activator.CreateInstance(type)!).ToArray();
    static readonly Workload[] Workloads =
    [
        new("plain", 10, 1, 1, 1, 0, false, false), new("small", 10, 1, 1, 1, 0), new("medium", 100, 1, 1, 1, 0),
        new("large", 1000, 1, 1, 1, 0), new("features", 100, 10, 1, 1, 0),
        new("json", 100, 1, 10, 1, 0), new("http-repeated", 10, 1, 1, 100, 0), new("http", 100, 1, 1, 100, 0),
        new("unrelated-1000", 100, 1, 1, 1, 1000), new("unrelated-10000", 100, 1, 1, 1, 10000),
        new("combined", 1000, 10, 10, 100, 10000)
    ];

    internal static async Task RunAsync(string output, string jsonGeneratorPath, int repetitions, string? filter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(repetitions, 2);
        Directory.CreateDirectory(output);
        var jsonGenerator = Assembly.LoadFrom(jsonGeneratorPath).GetTypes()
            .Single(type => !type.IsAbstract && typeof(IIncrementalGenerator).IsAssignableFrom(type));
        var rows = new List<Row>();
        var controls = new List<object>();
        foreach (var workload in Workloads.Where(item => string.IsNullOrEmpty(filter) || item.Name == filter))
        {
            var features = Enumerable.Range(0, workload.Features).Select(index => Feature(index, false, jsonGenerator)).ToArray();
            var replacement = Feature(0, true, jsonGenerator);
            var trees = Sources(workload);
            EmitFixture(output, workload.Name, trees, features);
            var compilation = CreateCompilation("CompilerConsumer", trees, features);
            // Setup, semantic validation, full output compilation and JIT warmup are outside measurements.
            GeneratorDriver controlDriver = Driver(jsonGenerator);
            controlDriver = controlDriver.RunGeneratorsAndUpdateCompilation(
                CreateCompilation("CompilerConsumer", trees, features), out var generated, out var diagnostics);
            Validate(generated, diagnostics);
            var generatorResults = controlDriver.GetRunResult().Results;
            var generatedHints = generatorResults.SelectMany(result => result.GeneratedSources)
                .Select(source => source.HintName).ToArray();
            var expectedOutputs = new[]
            {
                "DomainEventTypeCatalogGeneratedExtensions.g.cs",
                "PortiaJsonRoots.g.cs",
                "PortiaGeneratedRegistrationInterceptors.g.cs",
            };
            if (expectedOutputs.Any(expected => !generatedHints.Contains(expected, StringComparer.Ordinal))
                || !generatedHints.Any(hint => hint.StartsWith("PortiaHttpBinding.", StringComparison.Ordinal) || hint == "PortiaGeneratedHttpBinding.g.cs")
                || !generatedHints.Any(hint => hint.StartsWith("PortiaGeneratedServiceCollectionExtensions", StringComparison.Ordinal))
                || !generatedHints.Any(hint => hint.Contains("BenchmarkProjector", StringComparison.Ordinal))
                || !generatedHints.Any(hint => hint.Contains("BenchmarkReactor", StringComparison.Ordinal))
            )
                throw new InvalidOperationException("Every source generator must emit its expected benchmark output.");
            var diagnosticControl = Driver(jsonGenerator).RunGenerators(compilation
                .ReplaceSyntaxTree(trees[2], Tree("using System.Text.Json.Serialization; namespace CompilerFixtures; public sealed partial class JsonContext0 : JsonSerializerContext;", "JsonContexts.cs"))
                .AddSyntaxTrees(Tree("namespace CompilerFixtures; public sealed class InvalidHandler<T> : Cntryl.Portia.IRequestHandler<Command0>;", "InvalidShape.cs")));
            var targetedGeneratorDiagnostics = diagnosticControl.GetRunResult().Diagnostics;
            if (!targetedGeneratorDiagnostics.Any(diagnostic => diagnostic.Id == "PORTIA015")
                || !targetedGeneratorDiagnostics.Any(diagnostic => diagnostic.Id == "PORTIA025"))
                throw new InvalidOperationException("Diagnostic generators did not report their benchmark controls.");
            var generatedText = string.Join("\n", controlDriver.GetRunResult().GeneratedTrees.Select(tree => tree.GetText().ToString()));
            controls.Add(new
            {
                workload,
                sourceHash = Hash(string.Join("\n", trees.Select(tree => tree.GetText().ToString()))),
                generatedHash = Hash(generatedText),
                generatedBytes = Encoding.UTF8.GetByteCount(generatedText),
                generatedHints,
                generatorDiagnostics = generatorResults.SelectMany(result => result.Diagnostics)
                    .Concat(targetedGeneratorDiagnostics)
                    .Select(diagnostic => diagnostic.Id).ToArray(),
                featureHashes = features.Select(feature => feature.Hash).ToArray(),
                outputCompiled = true
            });
            var variants = Variants(compilation, trees, features[0], replacement);
            // Warm both the analyzer driver and the generator paths before recording distributions.
            await AnalyzeAsync(generated, workload.Name, "warmup", -1, rows: null);
            foreach (var (_, variant) in variants)
            {
                _ = controlDriver.RunGeneratorsAndUpdateCompilation(variant, out var variantOutput, out var errors);
                Validate(variantOutput, errors);
            }
            for (var repetition = 0; repetition < repetitions; repetition++)
            {
                var fresh = CreateCompilation("CompilerConsumer", trees, features);
                var edits = Variants(fresh, trees, features[0], replacement);
                var driver = Measure(Driver(jsonGenerator), fresh, workload.Name, "cold-driver", repetition, rows);
                foreach (var (name, variant) in edits)
                {
                    // Each edit starts from the same populated driver, preserving syntax identity.
                    _ = Measure(driver, variant, workload.Name, name, repetition, rows);
                }
                foreach (var type in GeneratorTypes.Append(jsonGenerator))
                {
                    GeneratorDriver isolated = CSharpGeneratorDriver.Create(
                        [((IIncrementalGenerator)Activator.CreateInstance(type)!).AsSourceGenerator()],
                        parseOptions: ParseOptions, driverOptions: new GeneratorDriverOptions(default, true));
                    var isolatedCompilation = CreateCompilation("CompilerConsumer", trees, features);
                    isolated = Measure(isolated, isolatedCompilation, workload.Name, "isolated.cold-driver", repetition, rows, type.FullName);
                    _ = Measure(isolated, isolatedCompilation, workload.Name, "isolated.unchanged", repetition, rows, type.FullName);
                }
                await AnalyzeAsync(generated, workload.Name, "analyzers", repetition, rows);
                // Persist outside measured operations. An interrupted later repetition must never
                // truncate the completed samples, and progress is announced only after the rename.
                await WriteAtomicAsync(Path.Combine(output, "raw.json"), JsonSerializer.Serialize(rows));
                await WriteAtomicAsync(Path.Combine(output, "controls.json"), JsonSerializer.Serialize(controls));
                Console.WriteLine($"{workload.Name}: saved repetition {repetition + 1}/{repetitions}.");
            }
            Console.WriteLine($"{workload.Name}: {repetitions} repetitions, valid generated output, {variants.Count} edits.");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "environment.json"), JsonSerializer.Serialize(new
        {
            framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processors = Environment.ProcessorCount,
            roslyn = typeof(CSharpCompilation).Assembly.GetName().Version?.ToString(),
            repetitions,
            references = PlatformReferences.Select(reference => new { reference.Display, hash = HashFile(reference.Display!) }),
            generators = GeneratorTypes.Select(type => type.FullName),
            analyzers = Analyzers.Select(analyzer => analyzer.GetType().FullName),
            jsonGeneratorPath,
            jsonGeneratorHash = HashFile(jsonGeneratorPath),
            allocationMethod = "GC.GetTotalAllocatedBytes(true); setup and validation excluded; joined per-generator allocation -1 is unassigned, measured separately in isolated rows; fresh compilations reset source semantic caches"
        }));
        await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(rows
            .GroupBy(row => (row.Workload, row.Edit, row.Component)).Select(group => new
            {
                group.Key.Workload,
                group.Key.Edit,
                group.Key.Component,
                samples = group.Count(),
                p50Milliseconds = Percentile(group.Select(row => row.Milliseconds), 0.50),
                p95Milliseconds = Percentile(group.Select(row => row.Milliseconds), 0.95),
                medianAllocatedBytes = group.Any(row => row.AllocatedBytes < 0) ? (double?)null : Percentile(group.Select(row => (double)row.AllocatedBytes), 0.50)
            })));
        var csv = new StringBuilder("workload,edit,component,repetition,milliseconds,allocated_bytes,generated_bytes,tracked_reasons\n");
        foreach (var row in rows)
            csv.Append(Csv(row.Workload)).Append(',').Append(Csv(row.Edit)).Append(',').Append(Csv(row.Component)).Append(',')
                .Append(row.Repetition).Append(',').Append(row.Milliseconds.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.AllocatedBytes).Append(',').Append(row.GeneratedBytes).Append(',').Append(Csv(JsonSerializer.Serialize(row.Reasons))).Append('\n');
        await File.WriteAllTextAsync(Path.Combine(output, "raw.csv"), csv.ToString());
        await CancellationAsync(output, jsonGenerator);
    }

    internal static async Task OutputIdentityAsync(string output, string jsonGeneratorPath)
    {
        Directory.CreateDirectory(output);
        var jsonGenerator = Assembly.LoadFrom(jsonGeneratorPath).GetTypes()
            .Single(type => !type.IsAbstract && typeof(IIncrementalGenerator).IsAssignableFrom(type));
        var workload = Workloads.Single(item => item.Name == "combined");
        var features = Enumerable.Range(0, workload.Features).Select(index => Feature(index, false, jsonGenerator)).ToArray();
        var replacement = Feature(0, true, jsonGenerator);
        var trees = Sources(workload);
        EmitFixture(output, workload.Name, trees, features);
        var replacementDirectory = Path.Combine(output, "inputs", "referenced-feature-replacement");
        Directory.CreateDirectory(replacementDirectory);
        await File.WriteAllTextAsync(Path.Combine(replacementDirectory, "CompilerFeature0.Feature.cs"), replacement.Source, SnapshotEncoding);
        await File.WriteAllBytesAsync(Path.Combine(replacementDirectory, "CompilerFeature0.replacement.dll"), replacement.Image);
        var compilation = CreateCompilation("CompilerConsumer", trees, features);
        var variants = Variants(compilation, trees, features[0], replacement);
        var initialDriver = Driver(jsonGenerator).RunGeneratorsAndUpdateCompilation(
            compilation, out var initialGenerated, out var initialDiagnostics);
        Validate(initialGenerated, initialDiagnostics);

        var contentRoot = Path.Combine(Directory.GetParent(Path.GetFullPath(output))!.FullName, "generated-source-content");
        Directory.CreateDirectory(contentRoot);
        var baselineOutputs = OutputFiles(initialDriver.GetRunResult(), jsonGenerator);
        var cases = new List<object>();
        foreach (var (name, variant) in variants)
        {
            GeneratorDriver variantDriver;
            Compilation generated;
            ImmutableArray<Diagnostic> diagnostics;
            if (name == "unchanged")
            {
                variantDriver = initialDriver;
                generated = initialGenerated;
                diagnostics = initialDiagnostics;
            }
            else
            {
                variantDriver = initialDriver.RunGeneratorsAndUpdateCompilation(variant, out generated, out diagnostics);
                Validate(generated, diagnostics);
            }

            var runResult = variantDriver.GetRunResult();
            var outputFiles = OutputFiles(runResult, jsonGenerator);
            var contracts = VerifyGeneratedContracts(variant, runResult);
            foreach (var file in outputFiles)
            {
                var path = Path.Combine(contentRoot, file.Hash + ".g.cs");
                if (!File.Exists(path) || HashFile(path) != file.Hash)
                    await File.WriteAllTextAsync(path, file.Source, SnapshotEncoding);
            }
            var currentMap = outputFiles.ToDictionary(file => file.Generator + "|" + file.HintName, file => file.Hash,
                StringComparer.Ordinal);
            var initialMap = baselineOutputs.ToDictionary(file => file.Generator + "|" + file.HintName, file => file.Hash,
                StringComparer.Ordinal);
            var added = currentMap.Keys.Except(initialMap.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var removed = initialMap.Keys.Except(currentMap.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var changed = currentMap.Keys.Intersect(initialMap.Keys, StringComparer.Ordinal)
                .Where(key => currentMap[key] != initialMap[key]).Order(StringComparer.Ordinal).ToArray();
            var sourceInputs = variant.SyntaxTrees.OrderBy(tree => tree.FilePath, StringComparer.Ordinal)
                .Select(tree => new
                {
                    path = tree.FilePath,
                    sha256 = Hash(tree.GetText().ToString())
                }).ToArray();
            var inputDirectory = Path.Combine(output, "inputs", name);
            Directory.CreateDirectory(inputDirectory);
            foreach (var tree in variant.SyntaxTrees)
                await File.WriteAllTextAsync(Path.Combine(inputDirectory, Path.GetFileName(tree.FilePath)),
                    tree.GetText().ToString(), SnapshotEncoding);

            cases.Add(new
            {
                edit = name,
                sourceInputs,
                sourceHash = Hash(string.Join("\n", variant.SyntaxTrees.Select(tree => tree.GetText().ToString()))),
                generatedSourceHash = Hash(string.Join("\n", outputFiles.OrderBy(file => file.Generator, StringComparer.Ordinal)
                    .ThenBy(file => file.HintName, StringComparer.Ordinal).Select(file => file.Hash))),
                generatedBytes = outputFiles.Sum(file => Encoding.UTF8.GetByteCount(file.Source)),
                outputCompiled = true,
                contracts,
                outputs = outputFiles.Select(file => new
                {
                    file.Generator,
                    file.HintName,
                    file.Hash,
                    file.Bytes,
                    content = file.Hash + ".g.cs"
                }).ToArray(),
                deltaFromUnchanged = new { added, removed, changed },
                trackedSteps = runResult.Results.Select((result, index) => new
                {
                    generator = index < GeneratorTypes.Length ? GeneratorTypes[index].FullName : jsonGenerator.FullName,
                    steps = result.TrackedSteps.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new
                    {
                        name = pair.Key,
                        reasons = Reasons(pair.Value)
                    })
                }).ToArray()
            });
        }

        await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new
        {
            sourceCommit = Environment.GetEnvironmentVariable("PORTIA_SOURCE_COMMIT"),
            harnessSourceHash = Environment.GetEnvironmentVariable("PORTIA_HARNESS_HASH"),
            framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            roslyn = typeof(CSharpCompilation).Assembly.GetName().Version?.ToString(),
            workload = "combined",
            sourceHash = Hash(string.Join("\n", trees.Select(tree => tree.GetText().ToString()))),
            featureHashes = features.Select(feature => feature.Hash).ToArray(),
            featureSourceHashes = features.Select(feature => Hash(feature.Source)).ToArray(),
            replacementFeatureHash = replacement.Hash,
            replacementFeatureSourceHash = Hash(replacement.Source),
            jsonGeneratorHash = HashFile(jsonGeneratorPath),
            generators = GeneratorAssemblies.Select(assembly => new
            {
                name = assembly.GetName().Name,
                version = assembly.GetName().Version?.ToString(),
                hash = HashFile(assembly.Location)
            }),
            cases
        }, OutputJsonOptions));
    }

    static OutputContracts VerifyGeneratedContracts(Compilation compilation, GeneratorDriverRunResult result)
    {
        var expectedEvents = compilation.SyntaxTrees.SelectMany(tree =>
            {
                var semanticModel = compilation.GetSemanticModel(tree);
                return tree.GetRoot().DescendantNodes().OfType<RecordDeclarationSyntax>()
                    .Select(declaration => semanticModel.GetDeclaredSymbol(declaration))
                    .Where(symbol => symbol?.BaseType?.ToDisplayString() == "Cntryl.Portia.DomainEvent")
                    .Select(symbol =>
                    {
                        var attribute = symbol!.GetAttributes().Single(item =>
                            item.AttributeClass?.ToDisplayString() == "Cntryl.Portia.DiscriminatorAttribute");
                        return new EventIdentity(TypeName(symbol), (string)attribute.ConstructorArguments[0].Value!,
                            (int)attribute.ConstructorArguments[1].Value!);
                    });
            }).OrderBy(item => item.Type, StringComparer.Ordinal).ToArray();
        var catalogIndex = Array.FindIndex(GeneratorTypes, type => type.Name == "DomainEventCatalogGenerator");
        var actualEvents = result.Results[catalogIndex].GeneratedSources.SelectMany(source =>
                CSharpSyntaxTree.ParseText(source.SourceText, ParseOptions).GetRoot().DescendantNodes()
                    .OfType<InvocationExpressionSyntax>())
            .Select(ParseEventRegistration).Where(item => item is not null).Select(item => item!)
            .OrderBy(item => item.Type, StringComparer.Ordinal).ToArray();
        if (!expectedEvents.SequenceEqual(actualEvents))
        {
            var expectedSet = expectedEvents.ToHashSet();
            var actualSet = actualEvents.ToHashSet();
            throw new InvalidOperationException($"Generated domain-event catalog does not match source event identities "
                + $"(expected {expectedEvents.Length}, actual {actualEvents.Length}; "
                + $"expected sample: {string.Join(";", expectedEvents.Take(3))}; "
                + $"actual sample: {string.Join(";", actualEvents.Take(3))}; "
                + $"expected-only: {string.Join(";", expectedSet.Except(actualSet).Take(3))}; "
                + $"actual-only: {string.Join(";", actualSet.Except(expectedSet).Take(3))}).");
        }

        var expectedRoots = compilation.SyntaxTrees.SelectMany(tree =>
                tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>()
                    .Select(declaration => compilation.GetSemanticModel(tree).GetDeclaredSymbol(declaration)))
            .Where(symbol => symbol?.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.ToDisplayString() == "Cntryl.Portia.PortiaJsonContextAttribute") == true)
            .SelectMany(context => context!.GetAttributes().Where(attribute =>
                    attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonSerializableAttribute")
                .Select(attribute => new JsonRootIdentity(TypeName((INamedTypeSymbol)attribute.ConstructorArguments[0].Value!), TypeName(context))))
            .Distinct().OrderBy(item => item.ContextType, StringComparer.Ordinal)
            .ThenBy(item => item.RootType, StringComparer.Ordinal).ToArray();
        var rootsIndex = Array.FindIndex(GeneratorTypes, type => type.Name == "JsonRootMarkerGenerator");
        var actualRoots = result.Results[rootsIndex].GeneratedSources.SelectMany(source =>
                CSharpSyntaxTree.ParseText(source.SourceText, ParseOptions).GetRoot().DescendantNodes()
                    .OfType<AttributeListSyntax>().Where(list => list.Target?.Identifier.ValueText == "assembly")
                    .SelectMany(list => list.Attributes).Where(attribute =>
                        attribute.Name.ToString().EndsWith("PortiaJsonRootAttribute", StringComparison.Ordinal)))
            .Select(attribute => attribute.ArgumentList!.Arguments.Select(argument => argument.Expression)
                .OfType<TypeOfExpressionSyntax>().Select(typeOf => NormalizeTypeName(typeOf.Type.ToString())).Take(2).ToArray())
            .Where(arguments => arguments.Length == 2)
            .Select(arguments => new JsonRootIdentity(arguments[0], arguments[1]))
            .Distinct().OrderBy(item => item.ContextType, StringComparer.Ordinal)
            .ThenBy(item => item.RootType, StringComparer.Ordinal).ToArray();
        if (!expectedRoots.SequenceEqual(actualRoots))
            throw new InvalidOperationException("Generated JSON-root identities do not match source contexts.");

        return new OutputContracts(expectedEvents, actualEvents, expectedRoots, actualRoots);
    }

    static EventIdentity? ParseEventRegistration(InvocationExpressionSyntax invocation)
    {
        var generic = invocation.Expression switch
        {
            GenericNameSyntax genericName => genericName,
            MemberAccessExpressionSyntax { Name: GenericNameSyntax genericName } => genericName,
            _ => null
        };
        if (generic?.TypeArgumentList.Arguments.SingleOrDefault() is not { } typeArgument)
            return null;
        var arguments = invocation.ArgumentList.Arguments.Select(argument => argument.Expression).ToArray();
        var eventName = arguments.OfType<LiteralExpressionSyntax>().Select(literal => literal.Token.Value)
            .OfType<string>().SingleOrDefault(value => value.StartsWith("compiler.", StringComparison.Ordinal));
        var version = arguments.OfType<LiteralExpressionSyntax>().Select(literal => literal.Token.Value)
            .OfType<int>().SingleOrDefault();
        if (eventName is null || !NormalizeTypeName(typeArgument.ToString()).StartsWith("CompilerFixtures.", StringComparison.Ordinal))
            return null;
        return new EventIdentity(NormalizeTypeName(typeArgument.ToString()), eventName, version);
    }

    static string TypeName(ITypeSymbol type) => NormalizeTypeName(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
    static string NormalizeTypeName(string value) => value.Replace("global::", string.Empty, StringComparison.Ordinal);
    sealed record EventIdentity(string Type, string Name, int Version);
    sealed record JsonRootIdentity(string RootType, string ContextType);
    sealed record OutputContracts(EventIdentity[] ExpectedEvents, EventIdentity[] GeneratedEvents,
        JsonRootIdentity[] ExpectedJsonRoots, JsonRootIdentity[] GeneratedJsonRoots);

    static OutputFile[] OutputFiles(GeneratorDriverRunResult result, Type jsonGenerator) => result.Results
        .SelectMany((generator, index) => generator.GeneratedSources.Select(source => new OutputFile(
            index < GeneratorTypes.Length ? GeneratorTypes[index].FullName! : jsonGenerator.FullName!,
            source.HintName,
            source.SourceText.ToString(),
            Hash(source.SourceText.ToString()),
            Encoding.UTF8.GetByteCount(source.SourceText.ToString()))))
        .OrderBy(file => file.Generator, StringComparer.Ordinal).ThenBy(file => file.HintName, StringComparer.Ordinal).ToArray();

    sealed record OutputFile(string Generator, string HintName, string Source, string Hash, int Bytes);

    static async Task WriteAtomicAsync(string path, string value)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, value);
        File.Move(temporary, path, overwrite: true);
    }

    static GeneratorDriver Measure(GeneratorDriver driver, Compilation compilation, string workload, string edit, int repetition, List<Row> rows, string? isolatedComponent = null)
    {
        var allocated = GC.GetTotalAllocatedBytes(true);
        var started = Stopwatch.GetTimestamp();
        driver = driver.RunGenerators(compilation);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
        var result = driver.GetRunResult();
        if (result.Results.Any(item => item.Exception is not null))
            throw new InvalidOperationException(string.Join("\n", result.Diagnostics));
        rows.Add(new Row(workload, edit, isolatedComponent ?? "all-generators", repetition, elapsed, bytes,
            result.GeneratedTrees.Sum(tree => Encoding.UTF8.GetByteCount(tree.GetText().ToString())), Reasons(result.Results.SelectMany(item => item.TrackedSteps.Values).SelectMany(steps => steps))));
        if (isolatedComponent is not null)
            return driver;
        foreach (var timing in driver.GetTimingInfo().GeneratorTimes)
        {
            var individual = result.Results.Single(item => ReferenceEquals(item.Generator, timing.Generator));
            var index = result.Results.IndexOf(individual);
            var name = index < GeneratorTypes.Length ? GeneratorTypes[index].FullName! : "System.Text.Json.SourceGeneration.JsonSourceGenerator";
            rows.Add(new Row(workload, edit, name, repetition, timing.ElapsedTime.TotalMilliseconds,
                -1, individual.GeneratedSources.Sum(source => Encoding.UTF8.GetByteCount(source.SourceText.ToString())),
                Reasons(individual.TrackedSteps.Values.SelectMany(steps => steps))));
        }
        return driver;
    }

    static async Task AnalyzeAsync(Compilation compilation, string workload, string edit, int repetition, List<Row>? rows)
    {
        // Isolate each analyzer to attribute both allocation and latency without concurrent callbacks.
        foreach (var analyzer in Analyzers)
        {
            var driver = compilation.WithAnalyzers([analyzer], new CompilationWithAnalyzersOptions(
                new AnalyzerOptions([], new BenchmarkAnalyzerConfigProvider(analyzer is ApplicationContractAnalyzer ? "Common" : "Core")),
                null, concurrentAnalysis: false, logAnalyzerExecutionTime: true, reportSuppressedDiagnostics: false));
            var allocated = GC.GetTotalAllocatedBytes(true);
            var started = Stopwatch.GetTimestamp();
            var diagnostics = await driver.GetAnalyzerDiagnosticsAsync();
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
            if (diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                throw new InvalidOperationException(string.Join("\n", diagnostics));
            var expected = analyzer switch
            {
                ApplicationStructureAnalyzer => "PORTIA200",
                ApplicationBoundaryAnalyzer => "PORTIA207",
                ApplicationContractAnalyzer => "PORTIA209",
                ComponentPracticeAnalyzer => "PORTIA102",
                ScenarioObservationAnalyzer => "PORTIA107",
                _ => null
            };
            if (expected is not null && !diagnostics.Any(diagnostic => diagnostic.Id == expected))
                throw new InvalidOperationException($"{analyzer.GetType().Name} did not produce the {expected} benchmark control.");
            rows?.Add(new Row(workload, edit, analyzer.GetType().FullName!, repetition, elapsed, bytes, 0,
                new Dictionary<string, int>(StringComparer.Ordinal) { ["diagnostics"] = diagnostics.Length }));
        }
    }

    static CSharpGeneratorDriver Driver(Type jsonGenerator) => CSharpGeneratorDriver.Create(
        GeneratorTypes.Select(type => ((IIncrementalGenerator)Activator.CreateInstance(type)!).AsSourceGenerator())
            .Append(((IIncrementalGenerator)Activator.CreateInstance(jsonGenerator)!).AsSourceGenerator()),
        parseOptions: ParseOptions, driverOptions: new GeneratorDriverOptions(default, trackIncrementalGeneratorSteps: true));

    static CSharpCompilation CreateCompilation(string name, IEnumerable<SyntaxTree> trees, IEnumerable<FeatureReference> features) =>
        CSharpCompilation.Create(name, trees, PlatformReferences.Concat(features.Select(feature => feature.Reference)), CompilationOptions);

    static void EmitFixture(string output, string name, SyntaxTree[] trees, FeatureReference[] features)
    {
        var directory = Path.Combine(output, "fixtures", name);
        Directory.CreateDirectory(directory);
        foreach (var tree in trees)
            File.WriteAllText(Path.Combine(directory, tree.FilePath), tree.GetText().ToString());
        foreach (var (feature, index) in features.Select((feature, index) => (feature, index)))
        {
            File.WriteAllBytes(Path.Combine(directory, "CompilerFeature" + index + ".dll"), feature.Image);
            var featureSourceDirectory = Path.Combine(output, "feature-inputs", "CompilerFeature" + index);
            Directory.CreateDirectory(featureSourceDirectory);
            File.WriteAllText(Path.Combine(featureSourceDirectory, "Feature.cs"), feature.Source, SnapshotEncoding);
        }
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var project = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup", new XElement("TargetFramework", "net11.0"),
                new XElement("Nullable", "enable"), new XElement("LangVersion", "15.0"),
                new XElement("InterceptorsNamespaces", "Cntryl.Portia.Generated"),
                new XElement("IsPackable", "false"), new XElement("EnableNETAnalyzers", "false"),
                new XElement("RestorePackagesWithLockFile", "true")),
            new XElement("ItemGroup", new XElement("FrameworkReference", new XAttribute("Include", "Microsoft.AspNetCore.App")),
                RuntimeProjects.Select(reference =>
                    new XElement("ProjectReference", new XAttribute("Include", Path.Combine(root, "src", reference, reference + ".csproj")))),
                AnalyzerProjects.Select(reference =>
                    new XElement("ProjectReference", new XAttribute("Include", Path.Combine(root, "src", reference, reference + ".csproj")),
                        new XAttribute("OutputItemType", "Analyzer"), new XAttribute("ReferenceOutputAssembly", "false"))),
                features.Select((_, index) => new XElement("Reference", new XAttribute("Include", "CompilerFeature" + index),
                    new XElement("HintPath", "CompilerFeature" + index + ".dll")))));
        new XDocument(project).Save(Path.Combine(directory, "CompilerConsumer.csproj"));
    }

    static FeatureReference Feature(int index, bool replacement, Type jsonGenerator)
    {
        var source = $$"""
                       using Cntryl.Portia;
                       using System.Text.Json.Nodes;
                       using System.Text.Json.Serialization;
                       namespace CompilerFeature{{index}};
                       [Discriminator("compiler.feature{{index}}.event", 2)]
                       public sealed record FeatureEvent(int Value) : DomainEvent;
                       public sealed class FeatureUpcaster : IJsonDomainEventUpcaster
                       {
                           public string EventName => "compiler.feature{{index}}.event";
                           public int FromVersion => 1;
                           public JsonObject Upcast(JsonObject json) { json["Value"] = {{(replacement ? 2 : 1)}}; return json; }
                       }
                       [PortiaJsonContext, JsonSerializable(typeof(FeatureEvent))]
                       public sealed partial class FeatureJsonContext : JsonSerializerContext;
                       """;
        var compilation = CreateCompilation("CompilerFeature" + index, [Tree(source, "Feature.cs")], []);
        var driver = Driver(jsonGenerator).RunGeneratorsAndUpdateCompilation(compilation, out var generated, out var diagnostics);
        Validate(generated, diagnostics);
        using var image = new MemoryStream();
        var emission = generated.Emit(image);
        if (!emission.Success)
            throw new InvalidOperationException(string.Join("\n", emission.Diagnostics));
        var bytes = image.ToArray();
        return new FeatureReference(MetadataReference.CreateFromImage(bytes), Convert.ToHexString(SHA256.HashData(bytes)), bytes, source);
    }

    static SyntaxTree[] Sources(Workload workload)
    {
        const string header = "using System; using System.Threading; using System.Threading.Tasks; using Cntryl.Portia;\n";
        var contracts = new StringBuilder(header).AppendLine("namespace CompilerFixtures;");
        var events = new StringBuilder(header).AppendLine("namespace CompilerFixtures;");
        for (var index = 0; index < workload.Commands; index++)
        {
            var requestRoute = index == 0 ? "[RequestRoute(\"app\", \"compiler\", \"commands\", \"run\")]" : "";
            contracts.Append(CultureInfo.InvariantCulture, $"{requestRoute}[Discriminator(\"compiler.command{index}\", 1)] public sealed record Command{index}(int Id = 1) : IRequest, ICallable;\n")
                .Append(CultureInfo.InvariantCulture, $"public sealed class Handler{index} : IRequestHandler<Command{index}> {{ public ValueTask<Result> HandleAsync(IRequestContext<Command{index}> context, CancellationToken ct) => ValueTask.FromResult(Result.Success); }}\n");
            events.Append(CultureInfo.InvariantCulture, $"[Discriminator(\"compiler.event{index}\", 1)] public sealed record Event{index}(int Value) : DomainEvent;\n");
        }
        events.AppendLine("[Discriminator(\"compiler.processor_event\", 1)] public sealed record ProcessorEvent : DomainEvent;");
        contracts.AppendLine("public sealed record Query(int Id = 1) : IRequest<int>; public sealed class QueryHandler : IRequestHandler<Query, int> { public ValueTask<Result<int>> HandleAsync(IRequestContext<Query> context, CancellationToken ct) => ValueTask.FromResult(Result<int>.Success(context.Request.Id)); }");
        contracts.AppendLine("public sealed class ContractBehavior { public void Validate() { } }");
        contracts.AppendLine("public sealed class BenchmarkAggregate(Uuid id, IServiceProvider services) : Aggregate(id, new EventStreamAddress(\"compiler\", \"aggregate\", id.ToString()));");
        contracts.AppendLine("public static class ScenarioControl { public static void Run(IServiceProvider services) { Cntryl.Portia.Testing.RequestScenario.For(services).When(new Command0()); } }");
        if (workload.Authorizers)
            contracts.AppendLine("public sealed class Authorizer : IRequestAuthorizer<Command0> { public ValueTask<Result> AuthorizeAsync(IRequestContext<Command0> context, CancellationToken ct) => ValueTask.FromResult(Result.Success); }");
        if (workload.Guards)
            contracts.AppendLine("public sealed class Guard : IRequestGuard<Command0> { public ValueTask<Result> GuardAsync(IRequestContext<Command0> context, CancellationToken ct) => ValueTask.FromResult(Result.Success); }");
        var contexts = new StringBuilder("using Cntryl.Portia; using System.Text.Json.Serialization; namespace CompilerFixtures;\n");
        for (var context = 0; context < workload.Contexts; context++)
        {
            contexts.AppendLine("[PortiaJsonContext][JsonSerializable(typeof(int))][JsonSerializable(typeof(Query))][JsonSerializable(typeof(ProcessorEvent))]");
            for (var index = context; index < workload.Commands; index += workload.Contexts)
                contexts.Append(CultureInfo.InvariantCulture, $"[JsonSerializable(typeof(Command{index}))][JsonSerializable(typeof(Event{index}))]\n");
            contexts.Append(CultureInfo.InvariantCulture, $"public sealed partial class JsonContext{context} : JsonSerializerContext;\n");
        }
        var composition = new StringBuilder("using Cntryl.Portia; using Microsoft.Extensions.DependencyInjection; using Microsoft.AspNetCore.Routing; using Microsoft.AspNetCore.Builder; using Microsoft.AspNetCore.Http; using FirstHandler = CompilerFixtures.Handler0; namespace CompilerFixtures;\n")
            .AppendLine("public static class Composition { public static PortiaBuilder Add(IServiceCollection services) { var builder = services.AddPortia(); builder.AddRequestHandler<FirstHandler>().AddRequestHandler<QueryHandler>();");
        if (workload.Authorizers)
            composition.AppendLine("builder.AddRequestAuthorizer<Authorizer>();");
        if (workload.Guards)
            composition.AppendLine("builder.AddRequestGuard<Guard>();");
        for (var index = 1; index < workload.Commands; index++)
            composition.Append(CultureInfo.InvariantCulture, $"builder.AddRequestHandler<Handler{index}>();\n");
        composition.AppendLine("return builder; } public static void Map(IEndpointRouteBuilder app) {");
        for (var index = 0; index < workload.Mappings; index++)
            composition.Append(CultureInfo.InvariantCulture, $"app.MapPortiaPost<Command{index % workload.Commands}>(\"/command{index}\"){(workload.Mappings > workload.Commands ? ".ExcludeFromDescription()" : "")};\n");
        composition.AppendLine("} }");
        var unrelated = new StringBuilder("namespace Unrelated; public static class Editing { public static int Value() => 1; }\n");
        for (var index = 0; index < workload.UnrelatedTypes; index++)
            unrelated.Append(CultureInfo.InvariantCulture, $"public sealed class Type{index} {{ public int Value {{ get; init; }} }}\n");
        var processors = new StringBuilder(header).AppendLine("namespace CompilerFixtures;")
            .AppendLine("public sealed partial class BenchmarkProjector : Projector, IProjectorHandler<ProcessorEvent>")
            .AppendLine("{ public BenchmarkProjector() : base(null!, EventStreamPattern.ForPattern(\"events\")) { } public ValueTask<Result> HandleAsync(IProjectorContext<ProcessorEvent> context, CancellationToken ct) => ValueTask.FromResult(Result.Success); }")
            .AppendLine("public sealed partial class BenchmarkReactor : Reactor, IReactorHandler<ProcessorEvent>")
            .AppendLine("{ public BenchmarkReactor() : base(null!, EventStreamPattern.ForPattern(\"events\")) { } public ValueTask<Result> HandleAsync(IReactorContext<ProcessorEvent> context, CancellationToken ct) => ValueTask.FromResult(Result.Success); }");
        return [Tree(contracts.ToString(), "Contracts.cs"), Tree(events.ToString(), "Events.cs"),
            Tree(contexts.ToString(), "JsonContexts.cs"), Tree(composition.ToString(), "Composition.cs"),
            Tree(processors.ToString(), "Processors.cs"), Tree(unrelated.ToString(), "Unrelated.cs")];
    }

    static Dictionary<string, CSharpCompilation> Variants(CSharpCompilation compilation, SyntaxTree[] trees,
        FeatureReference original, FeatureReference replacement)
    {
        var extraEvent = Tree("using Cntryl.Portia; namespace CompilerFixtures; [Discriminator(\"compiler.extra\",1)] public sealed record ExtraEvent : DomainEvent;", "ExtraEvent.cs");
        var eventText = trees[1].GetText().ToString();
        var withoutFirst = string.Join('\n', eventText.Split('\n').Where(line => !line.Contains("record Event0(", StringComparison.Ordinal)));
        // Remove/rename the matching JSON root as well: controls must remain valid consumers.
        var jsonText = trees[2].GetText().ToString();
        return new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal)
        {
            ["unchanged"] = compilation,
            ["unrelated-body-edit"] = compilation.ReplaceSyntaxTree(trees[5], Tree(trees[5].GetText().ToString().Replace("=> 1;", "=> 2;", StringComparison.Ordinal), "Unrelated.cs")),
            ["call-site-whitespace"] = compilation.ReplaceSyntaxTree(trees[3], Tree(trees[3].GetText().ToString().Replace("services.AddPortia()", "services. AddPortia( )", StringComparison.Ordinal), "Composition.cs")),
            ["event-add"] = compilation.AddSyntaxTrees(extraEvent).ReplaceSyntaxTree(trees[2],
                Tree(jsonText.Replace("[PortiaJsonContext]", "[PortiaJsonContext][JsonSerializable(typeof(ExtraEvent))]", StringComparison.Ordinal), "JsonContexts.cs")),
            ["event-remove"] = compilation.ReplaceSyntaxTree(trees[1], Tree(withoutFirst, "Events.cs"))
                .ReplaceSyntaxTree(trees[2], Tree(jsonText.Replace("[JsonSerializable(typeof(Event0))]", "", StringComparison.Ordinal), "JsonContexts.cs")),
            ["event-rename"] = compilation.ReplaceSyntaxTree(trees[1], Tree(eventText.Replace("record Event0(", "record RenamedEvent0(", StringComparison.Ordinal), "Events.cs"))
                .ReplaceSyntaxTree(trees[2], Tree(jsonText.Replace("typeof(Event0)", "typeof(RenamedEvent0)", StringComparison.Ordinal), "JsonContexts.cs")),
            ["route-change"] = compilation.ReplaceSyntaxTree(trees[3], Tree(trees[3].GetText().ToString().Replace("/command0", "/changed0", StringComparison.Ordinal), "Composition.cs")),
            ["referenced-feature-replacement"] = compilation.RemoveReferences(original.Reference).AddReferences(replacement.Reference)
        };
    }

    static async Task CancellationAsync(string output, Type jsonGenerator)
    {
        var compilation = CreateCompilation("IncompleteEdit", Sources(new Workload("incomplete", 1000, 1, 1, 100, 10000))
            .Append(Tree("namespace Editing; public class Incomplete { public void Change(", "Incomplete.cs")), []);
        var rows = new List<object>();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var cancellation = new CancellationTokenSource();
            var driver = Driver(jsonGenerator);
            long requestedAt = 0;
            using var registration = cancellation.Token.Register(() => Interlocked.Exchange(ref requestedAt, Stopwatch.GetTimestamp()));
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(5));
            var started = Stopwatch.GetTimestamp();
            var observed = false;
            try
            { _ = driver.RunGenerators(compilation, cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { observed = true; }
            rows.Add(new
            {
                attempt,
                observed,
                operationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                responseMilliseconds = requestedAt == 0 ? (double?)null : Stopwatch.GetElapsedTime(requestedAt).TotalMilliseconds
            });
        }
        await File.WriteAllTextAsync(Path.Combine(output, "cancellation.json"), JsonSerializer.Serialize(rows));
    }

    static void Validate(Compilation compilation, ImmutableArray<Diagnostic> diagnostics)
    {
        var errors = diagnostics.Concat(compilation.GetDiagnostics()).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0)
            throw new InvalidOperationException(string.Join("\n", errors.Select(error => error.ToString())));
        using var stream = new MemoryStream();
        var emission = compilation.Emit(stream);
        if (!emission.Success)
            throw new InvalidOperationException(string.Join("\n", emission.Diagnostics));
    }

    static Dictionary<string, int> Reasons(IEnumerable<IncrementalGeneratorRunStep> steps) => steps.SelectMany(step => step.Outputs)
        .GroupBy(output => output.Reason.ToString()).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    static double Percentile(IEnumerable<double> values, double fraction)
    {
        var sorted = values.Order().ToArray();
        return sorted[(int)Math.Ceiling((sorted.Length - 1) * fraction)];
    }
    static string Csv(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    static SyntaxTree Tree(string source, string path) => CSharpSyntaxTree.ParseText(source, ParseOptions, path, Encoding.UTF8);
    sealed class BenchmarkAnalyzerConfigProvider : AnalyzerConfigOptionsProvider
    {
        readonly AnalyzerConfigOptions _global;
        static readonly AnalyzerConfigOptions Empty = new BenchmarkAnalyzerConfigOptions(new Dictionary<string, string>());

        public BenchmarkAnalyzerConfigProvider(string role) => _global = new BenchmarkAnalyzerConfigOptions(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["build_property.PortiaProjectStructure"] = "true",
            ["build_property.MSBuildProjectDirectory"] = "/benchmark/CompilerConsumer",
            ["build_property.MSBuildProjectName"] = "CompilerConsumer." + role,
            ["build_property.RootNamespace"] = "CompilerConsumer." + role,
            ["build_property.IsTestProject"] = "false"
        });

        public override AnalyzerConfigOptions GlobalOptions => _global;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Empty;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Empty;
    }

    sealed class BenchmarkAnalyzerConfigOptions(IReadOnlyDictionary<string, string> options) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => options.TryGetValue(key, out value!);
    }

    sealed record Workload(string Name, int Commands, int Features, int Contexts, int Mappings, int UnrelatedTypes, bool Authorizers = true, bool Guards = true);
    sealed record FeatureReference(MetadataReference Reference, string Hash, byte[] Image, string Source);
    sealed record Row(string Workload, string Edit, string Component, int Repetition, double Milliseconds,
        long AllocatedBytes, int GeneratedBytes, Dictionary<string, int> Reasons);
}
