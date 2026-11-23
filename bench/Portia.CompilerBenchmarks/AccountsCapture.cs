using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cntryl.Portia.CompilerBenchmarks;

sealed record CapturedText(string Path, string Text);
sealed record CapturedReference(string File, string Hash, string[] Aliases, bool EmbedInteropTypes);
sealed record CapturedAnalyzer(string Name, string File, string Hash);
sealed record CapturedProject(string Name, string Directory, string[] Arguments, CapturedText[] Sources,
    CapturedText[] AdditionalFiles, CapturedText[] Configurations, CapturedReference[] References,
    CapturedAnalyzer[] Analyzers);
sealed record AccountsManifest(string Format, string Revision, string Sdk, CapturedProject[] Projects, Dictionary<string, string> Files);

static class AccountsCapture
{
    static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
    public const string Format = "portia-accounts-capture-1";
    public static readonly string[] Projects = ["Application", "Api", "Worker"];

    public static AccountsManifest Read(string directory)
    {
        var manifest = JsonSerializer.Deserialize<AccountsManifest>(File.ReadAllText(Path.Combine(directory, "accounts.json")), Campaign.Json)
            ?? throw new InvalidOperationException("Empty Accounts capture.");

        if (manifest.Format != Format)
            throw new InvalidOperationException("Unsupported Accounts capture format.");

        foreach (var project in manifest.Projects)
        {
            foreach (var (file, hash) in project.References.Select(reference => (reference.File, reference.Hash))
                         .Concat(project.Analyzers.Select(analyzer => (analyzer.File, analyzer.Hash))))
                if (!manifest.Files.TryGetValue(file, out var registered) || registered != hash)
                    throw new InvalidOperationException("Accounts input is missing from the hash registry or has a conflicting hash: " + file);
        }

        foreach (var file in manifest.Files)
            if (CompilerHost.FileHash(Resolve(directory, file.Key)) != file.Value)
                throw new InvalidOperationException("Accounts captured file changed: " + file.Key);

        return manifest;
    }

    internal static string Resolve(string directory, string relative)
    {
        var path = Path.GetFullPath(relative, directory);
        var within = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(within) && within != ".." && !within.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? path : throw new InvalidOperationException("Captured file escapes its directory.");
    }

    public static async Task<string> CaptureAsync(string repository, string directory, CancellationToken ct)
    {
        repository = Path.GetFullPath(repository);
        directory = Path.GetFullPath(directory);

        if (Directory.Exists(directory) || File.Exists(directory))
            throw new InvalidOperationException("Accounts capture requires a new output directory.");

        Directory.CreateDirectory(directory);
        var projects = new List<CapturedProject>();
        var copied = new Dictionary<string, string>(StringComparer.Ordinal);
        var frozenAnalyzers = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in Projects)
        {
            var project = Path.Combine(repository, "examples", "Portia.Accounts", "Portia.Accounts." + name,
                "Portia.Accounts." + name + ".csproj");
            _ = await DotnetAsync(["build", project, "--configuration", "Release", "--nologo"], repository, ct)
                .ConfigureAwait(false);
            var json = await DotnetAsync(["msbuild", project, "-nologo", "-target:Compile",
                "-property:Configuration=Release", "-property:SkipCompilerExecution=true",
                "-property:ProvideCommandLineArgs=true", "-property:NonExistentFile=" + Path.Combine(directory, "missing-" + Guid.NewGuid().ToString("N")),
                "-getItem:CscCommandLineArgs", "-getProperty:MSBuildProjectDirectory,AssemblyName"], repository, ct).ConfigureAwait(false);
            using var result = JsonDocument.Parse(json);
            var properties = result.RootElement.GetProperty("Properties");
            var projectDirectory = properties.GetProperty("MSBuildProjectDirectory").GetString()!;
            var arguments = result.RootElement.GetProperty("Items").GetProperty("CscCommandLineArgs")
                .EnumerateArray().Select(item => item.GetProperty("Identity").GetString()!).ToArray();

            if (arguments.Length == 0)
                throw new InvalidOperationException("SDK did not expose compiler arguments.");

            var parsed = CSharpCommandLineParser.Default.Parse(arguments, projectDirectory, sdkDirectory: null);
            var errors = parsed.Errors.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();

            if (errors.Length != 0)
                throw new InvalidOperationException(string.Join("\n", errors.Select(error => error.ToString())));

            string Full(string path) => Path.GetFullPath(path, projectDirectory);
            CapturedText Text(string path) => new(Full(path), File.ReadAllText(Full(path)));
            string Copy(string path)
            {
                if (copied.TryGetValue(path, out var existing))
                    return existing;

                var bytes = File.ReadAllBytes(path);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                var relative = "files/" + hash + "/" + Path.GetFileName(path);
                var target = Path.Combine(directory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (!File.Exists(target))
                    File.WriteAllBytes(target, bytes);

                copied.Add(path, relative);
                return relative;
            }
            var references = parsed.MetadataReferences.Select(reference =>
            {
                var path = Full(reference.Reference);
                return new CapturedReference(Copy(path), CompilerHost.FileHash(path), reference.Properties.Aliases.ToArray(),
                    reference.Properties.EmbedInteropTypes);
            }).ToArray();
            var analyzers = parsed.AnalyzerReferences.Select(reference =>
            {
                var path = Full(reference.FilePath);
                var sourceFolder = Path.GetDirectoryName(path)!;

                if (!frozenAnalyzers.TryGetValue(sourceFolder, out var folder))
                {
                    var siblings = System.IO.Directory.EnumerateFiles(sourceFolder, "*.dll").Order(StringComparer.Ordinal).ToArray();
                    var identity = CompilerHost.Hash(string.Join("\n", siblings.Select(file => Path.GetFileName(file) + "|" + CompilerHost.FileHash(file))));
                    folder = "analyzers/" + identity;
                    System.IO.Directory.CreateDirectory(Path.Combine(directory, folder));

                    foreach (var sibling in siblings)
                        File.Copy(sibling, Path.Combine(directory, folder, Path.GetFileName(sibling)));

                    frozenAnalyzers.Add(sourceFolder, folder);
                }

                var file = folder + "/" + Path.GetFileName(path);
                return new CapturedAnalyzer(Path.GetFileNameWithoutExtension(path), file, CompilerHost.FileHash(path));
            }).ToArray();
            projects.Add(new CapturedProject(properties.GetProperty("AssemblyName").GetString()!, projectDirectory,
                arguments, parsed.SourceFiles.Select(source => Text(source.Path)).ToArray(),
                parsed.AdditionalFiles.Select(file => Text(file.Path)).ToArray(),
                parsed.AnalyzerConfigPaths.Select(Text).ToArray(), references, analyzers));
            Console.Error.WriteLine("Captured " + name + ": " + projects[^1].Sources.Length + " source files.");
        }

        var revision = await CommandAsync("git", ["rev-parse", "HEAD"], repository, ct).ConfigureAwait(false);
        var sdk = await DotnetAsync(["--version"], repository, ct).ConfigureAwait(false);
        var manifest = new AccountsManifest(Format, revision.Trim(), sdk.Trim(), projects.ToArray(),
            System.IO.Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories)
                .ToDictionary(file => Path.GetRelativePath(directory, file).Replace('\\', '/'), CompilerHost.FileHash, StringComparer.Ordinal));
        var pathToManifest = Path.Combine(directory, "accounts.json");
        await File.WriteAllTextAsync(pathToManifest, JsonSerializer.Serialize(manifest, PrettyJson), ct)
            .ConfigureAwait(false);
        return pathToManifest;
    }

    static Task<string> DotnetAsync(string[] arguments, string directory, CancellationToken ct) => CommandAsync("dotnet", arguments, directory, ct);

    static async Task<string> CommandAsync(string command, string[] arguments, string directory, CancellationToken ct)
    {
        var info = new ProcessStartInfo(command)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start " + command);
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        { await process.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(command + " exited " + process.ExitCode + ":\n" + error + output);

        return output;
    }
}
