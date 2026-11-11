using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cntryl.Portia;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace Cntryl.Portia.RepositoryTools;

static class ApplicationStructureCommand
{
    static readonly string[] FileDiagnosticIds = ["PORTIA200", "PORTIA201"];

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var solutionPath = ParseSolutionPath(args);
            MSBuildLocator.RegisterDefaults();

            var plan = await PrepareAsync(solutionPath, CancellationToken.None).ConfigureAwait(false);
            if (plan.Operations.Count == 0)
            {
                Console.WriteLine("No PORTIA200 or PORTIA201 fixes are needed.");
                return 0;
            }

            Console.WriteLine($"Applying {plan.Operations.Count} application structure file operation(s).");
            plan.Apply();
            try
            {
                await VerifyAsync(solutionPath, plan, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                plan.Rollback();
                throw;
            }

            plan.Commit();
            Console.WriteLine("PORTIA200/201 diagnostics, build, and idempotence checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    static string ParseSolutionPath(string[] args)
    {
        if (args.Length != 3 || args[1] != "--solution" || string.IsNullOrWhiteSpace(args[2]))
            throw new ArgumentException("Usage: fix-application-structure --solution <path.sln|path.slnx>");

        var path = Path.GetFullPath(args[2]);
        if (!File.Exists(path) || Path.GetExtension(path) is not { } extension
            || !string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Solution file not found or unsupported: {path}");
        return path;
    }

    static async Task<FileOperationPlan> PrepareAsync(string solutionPath, CancellationToken cancellationToken)
    {
        using var workspace = await OpenWorkspaceAsync(solutionPath, cancellationToken).ConfigureAwait(false);
        var original = workspace.CurrentSolution;
        var changed = original;
        var renameDiagnostics = new Dictionary<DocumentId, ImmutableArray<Diagnostic>>();

        foreach (var id in FileDiagnosticIds)
        {
            var changes = await GetDiagnosticGroupsAsync(changed, id, cancellationToken).ConfigureAwait(false);
            if (changes.Count == 0)
                continue;
            if (id == "PORTIA201")
                renameDiagnostics = changes;

            changed = await ApplicationStructureCodeFixProvider.StructureFixAllProvider
                .ApplyAsync(changed, changes, id, cancellationToken).ConfigureAwait(false);
        }

        var remaining = await GetDiagnosticsAsync(changed, cancellationToken).ConfigureAwait(false);
        if (remaining.Length > 0)
        {
            throw new InvalidOperationException("Some PORTIA200/201 diagnostics have no safe file operation. " +
                "Resolve destination collisions, linked files, or project Compile-item rules, then retry.\n" +
                string.Join(Environment.NewLine, remaining.Select(FormatDiagnostic)));
        }

        return await FileOperationPlan.CreateAsync(original, changed, renameDiagnostics, cancellationToken)
            .ConfigureAwait(false);
    }

    static async Task VerifyAsync(string solutionPath, FileOperationPlan plan, CancellationToken cancellationToken)
    {
        using (var workspace = await OpenWorkspaceAsync(solutionPath, cancellationToken).ConfigureAwait(false))
        {
            await VerifyDocumentPathsAsync(workspace.CurrentSolution, plan, cancellationToken).ConfigureAwait(false);
            var diagnostics = await GetDiagnosticsAsync(workspace.CurrentSolution, cancellationToken).ConfigureAwait(false);
            if (diagnostics.Length > 0)
            {
                throw new InvalidOperationException("Reloaded solution still reports PORTIA200/201:\n" +
                    string.Join(Environment.NewLine, diagnostics.Select(FormatDiagnostic)));
            }
        }

        var build = await ProcessRunner.RunAsync("dotnet", ["build", solutionPath, "--no-restore"],
            Path.GetDirectoryName(solutionPath)!).ConfigureAwait(false);
        if (build.ExitCode != 0)
            throw new InvalidOperationException("The solution build failed; file changes will be rolled back.\n" + build.Output);

        using var verifiedWorkspace = await OpenWorkspaceAsync(solutionPath, cancellationToken).ConfigureAwait(false);
        await VerifyDocumentPathsAsync(verifiedWorkspace.CurrentSolution, plan, cancellationToken).ConfigureAwait(false);
        var finalDiagnostics = await GetDiagnosticsAsync(verifiedWorkspace.CurrentSolution, cancellationToken).ConfigureAwait(false);
        if (finalDiagnostics.Length > 0)
        {
            throw new InvalidOperationException("The second solution pass is not idempotent; file changes will be rolled back.\n" +
                string.Join(Environment.NewLine, finalDiagnostics.Select(FormatDiagnostic)));
        }
    }

    static async Task VerifyDocumentPathsAsync(Solution solution, FileOperationPlan plan, CancellationToken cancellationToken)
    {
        foreach (var operation in plan.Operations)
        {
            var projects = solution.Projects.Where(item => PathEquals(item.FilePath, operation.ProjectPath)).ToArray();
            if (projects.Length == 0)
                throw new InvalidOperationException($"Project was not reloaded: {operation.ProjectPath}");

            foreach (var project in projects)
            {
                var destinationDocuments = project.Documents.Where(document => PathEquals(document.FilePath, operation.DestinationPath)).ToArray();
                if (destinationDocuments.Length != 1)
                {
                    throw new InvalidOperationException($"Expected exactly one project document at '{operation.DestinationPath}', " +
                        $"but the reloaded project contains {destinationDocuments.Length}. The operation was rolled back. " +
                        "Check SDK default Compile globs and explicit Compile items.");
                }

                var actualText = await destinationDocuments[0].GetTextAsync(cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actualText.ToString(), operation.ContentText, StringComparison.Ordinal))
                    throw new InvalidOperationException($"The reloaded source differs from the planned content at '{operation.DestinationPath}'.");

                if (operation.SourcePath is { } sourcePath && !PathEquals(sourcePath, operation.DestinationPath)
                    && project.Documents.Any(document => PathEquals(document.FilePath, sourcePath)))
                {
                    throw new InvalidOperationException($"The old rename path '{sourcePath}' remains in the reloaded project. " +
                        "The operation was rolled back; check explicit Compile items.");
                }
            }
        }
    }

    static async Task<MSBuildWorkspace> OpenWorkspaceAsync(string solutionPath, CancellationToken cancellationToken)
    {
        var workspace = MSBuildWorkspace.Create();
        try
        {
            _ = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            return workspace;
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    static async Task<Dictionary<DocumentId, ImmutableArray<Diagnostic>>> GetDiagnosticGroupsAsync(
        Solution solution, string id, CancellationToken cancellationToken)
    {
        var groups = new Dictionary<DocumentId, ImmutableArray<Diagnostic>>();
        foreach (var project in solution.Projects)
        {
            var diagnostics = await GetProjectDiagnosticsAsync(project, cancellationToken).ConfigureAwait(false);
            foreach (var grouping in diagnostics.Where(diagnostic => diagnostic.Id == id)
                         .GroupBy(diagnostic => solution.GetDocument(diagnostic.Location.SourceTree!)?.Id))
            {
                if (grouping.Key is { } documentId)
                    groups[documentId] = grouping.ToImmutableArray();
            }
        }
        return groups;
    }

    static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(Solution solution, CancellationToken cancellationToken)
    {
        var result = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach (var project in solution.Projects)
        {
            var diagnostics = await GetProjectDiagnosticsAsync(project, cancellationToken).ConfigureAwait(false);
            result.AddRange(diagnostics.Where(diagnostic => FileDiagnosticIds.Contains(diagnostic.Id, StringComparer.Ordinal)));
        }
        return result.ToImmutable();
    }

    static async Task<ImmutableArray<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken)
    {
        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Could not load compilation for '{project.FilePath ?? project.Name}'.");
        var diagnostics = await compilation.WithAnalyzers([new ApplicationStructureAnalyzer()], project.AnalyzerOptions)
            .GetAnalyzerDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        var analyzerFailure = diagnostics.FirstOrDefault(diagnostic => diagnostic.Id == "AD0001");
        if (analyzerFailure is not null)
            throw new InvalidOperationException($"Portia's application structure analyzer failed in '{project.Name}': {analyzerFailure}");
        return [.. diagnostics.Where(diagnostic => !diagnostic.IsSuppressed)];
    }

    static string FormatDiagnostic(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        var path = string.IsNullOrWhiteSpace(span.Path) ? "<unknown>" : span.Path;
        var line = span.StartLinePosition.Line + 1;
        return $"{diagnostic.Id}: {diagnostic.GetMessage(CultureInfo.InvariantCulture)} ({path}:{line})";
    }

    static bool PathEquals(string? left, string? right) => left is not null && right is not null
        && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    sealed class FileOperationPlan
    {
        readonly List<OperationState> _states;

        FileOperationPlan(List<FileOperation> operations) =>
            (Operations, _states) = (operations, operations.Select(_ => new OperationState()).ToList());

        internal List<FileOperation> Operations { get; }

        internal static async Task<FileOperationPlan> CreateAsync(Solution original, Solution changed,
            Dictionary<DocumentId, ImmutableArray<Diagnostic>> renameDiagnostics, CancellationToken cancellationToken)
        {
            var operations = new List<FileOperation>();
            var renameAddedIds = new HashSet<DocumentId>();
            var renameRemovedIds = new HashSet<DocumentId>();

            foreach (var (documentId, diagnostics) in renameDiagnostics)
            {
                var source = original.GetDocument(documentId)
                    ?? throw new InvalidOperationException("A PORTIA201 source document disappeared before planning.");
                if (source.FilePath is null || source.Project.FilePath is null)
                    throw new InvalidOperationException("PORTIA201 requires a project-backed source file with a physical path.");

                foreach (var diagnostic in diagnostics)
                {
                    if (!diagnostic.Properties.TryGetValue("ExpectedFileName", out var filename) || string.IsNullOrWhiteSpace(filename)
                        || Path.GetFileName(filename) != filename)
                        throw new InvalidOperationException("PORTIA201 produced an invalid destination filename.");

                    var destination = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source.FilePath)!, filename));
                    var project = changed.GetProject(source.Project.Id)
                        ?? throw new InvalidOperationException($"Project was not retained: {source.Project.FilePath}");
                    var matches = project.Documents.Where(document => PathEquals(document.FilePath, destination)).ToArray();
                    if (matches.Length != 1)
                        throw new InvalidOperationException($"Expected one planned rename document at '{destination}', found {matches.Length}.");

                    var finalText = await matches[0].GetTextAsync(cancellationToken).ConfigureAwait(false);
                    var originalText = await source.GetTextAsync(cancellationToken).ConfigureAwait(false);
                    operations.Add(FileOperation.Rename(source.Project.FilePath, source.FilePath, destination,
                        originalText, finalText));
                    _ = renameAddedIds.Add(matches[0].Id);
                    _ = renameRemovedIds.Add(documentId);
                }
            }

            var solutionChanges = changed.GetChanges(original);
            foreach (var projectChange in solutionChanges.GetProjectChanges())
            {
                foreach (var removedId in projectChange.GetRemovedDocuments())
                {
                    if (!renameRemovedIds.Contains(removedId))
                        throw new InvalidOperationException("The planned file changes remove a document without a matching PORTIA201 rename.");
                }

                foreach (var addedId in projectChange.GetAddedDocuments())
                {
                    if (renameAddedIds.Contains(addedId))
                        continue;
                    var document = changed.GetDocument(addedId)
                        ?? throw new InvalidOperationException("A planned added document is missing from the changed solution.");
                    if (document.FilePath is null || document.Project.FilePath is null)
                        throw new InvalidOperationException("PORTIA200 requires project-backed documents with physical paths.");
                    var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                    operations.Add(FileOperation.Create(document.Project.FilePath, document.FilePath, text));
                }

                foreach (var changedId in projectChange.GetChangedDocuments())
                {
                    if (renameRemovedIds.Contains(changedId))
                        continue;
                    var oldDocument = original.GetDocument(changedId)
                        ?? throw new InvalidOperationException("A planned changed document is not present in the original solution.");
                    var newDocument = changed.GetDocument(changedId)
                        ?? throw new InvalidOperationException("A planned changed document is missing from the changed solution.");
                    if (oldDocument.FilePath is null || newDocument.FilePath is null || oldDocument.Project.FilePath is null)
                        throw new InvalidOperationException("PORTIA200 requires project-backed documents with physical paths.");
                    if (!PathEquals(oldDocument.FilePath, newDocument.FilePath))
                        throw new InvalidOperationException("A document path changed without a PORTIA201 rename plan.");
                    var oldText = await oldDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
                    var newText = await newDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
                    operations.Add(FileOperation.Replace(oldDocument.Project.FilePath, oldDocument.FilePath, oldText, newText));
                }
            }

            var plan = new FileOperationPlan(operations);
            plan.ValidateAndCaptureOriginalFiles();
            return plan;
        }

        void ValidateAndCaptureOriginalFiles()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var operation in Operations)
            {
                if (!paths.Add(Path.GetFullPath(operation.DestinationPath)))
                    throw new InvalidOperationException($"Multiple planned operations target '{operation.DestinationPath}'.");

                if (operation.SourcePath is not { } sourcePath)
                {
                    if (File.Exists(operation.DestinationPath) || Directory.Exists(operation.DestinationPath))
                        throw new InvalidOperationException($"Destination already exists: {operation.DestinationPath}");
                    operation.ExpectedBytes = null;
                    continue;
                }

                if (!File.Exists(sourcePath) || Directory.Exists(sourcePath))
                    throw new InvalidOperationException($"Source file is missing or is not a file: {sourcePath}");
                if ((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"Linked or reparse-point source files are not changed: {sourcePath}");

                var bytes = File.ReadAllBytes(sourcePath);
                if (!TextMatches(bytes, operation.OriginalText!))
                    throw new InvalidOperationException($"Source changed since the solution was loaded: {sourcePath}");
                operation.ExpectedBytes = bytes;

                if (!PathEquals(sourcePath, operation.DestinationPath)
                    && (File.Exists(operation.DestinationPath) || Directory.Exists(operation.DestinationPath)))
                    throw new InvalidOperationException($"Destination already exists: {operation.DestinationPath}");
            }

            foreach (var operation in Operations.Where(item => item.SourcePath is not null))
            {
                var source = Path.GetFullPath(operation.SourcePath!);
                if (paths.Contains(source) && !PathEquals(source, operation.DestinationPath))
                    throw new InvalidOperationException($"A planned rename collides with another destination: {source}");
            }
        }

        internal void Apply()
        {
            try
            {
                for (var index = 0; index < Operations.Count; index++)
                {
                    var operation = Operations[index];
                    var directory = Path.GetDirectoryName(operation.DestinationPath)!;
                    _states[index].StagedPath = UniqueSiblingPath(directory, ".portia-stage");
                    using var stream = new FileStream(_states[index].StagedPath!, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    stream.Write(operation.ContentBytes);
                }

                for (var index = 0; index < Operations.Count; index++)
                {
                    var operation = Operations[index];
                    if (operation.SourcePath is not { } sourcePath)
                        continue;
                    if (!File.Exists(sourcePath) || !CryptographicOperations.FixedTimeEquals(
                            SHA256.HashData(File.ReadAllBytes(sourcePath)), SHA256.HashData(operation.ExpectedBytes!)))
                        throw new InvalidOperationException($"Source changed immediately before applying: {sourcePath}");

                    _states[index].BackupPath = UniqueSiblingPath(Path.GetDirectoryName(sourcePath)!, ".portia-backup");
                    File.Move(sourcePath, _states[index].BackupPath!);
                    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(_states[index].BackupPath!)),
                            SHA256.HashData(operation.ExpectedBytes!)))
                    {
                        File.Move(_states[index].BackupPath!, sourcePath);
                        _states[index].BackupPath = null;
                        throw new InvalidOperationException($"Source changed while applying: {sourcePath}");
                    }
                }

                for (var index = 0; index < Operations.Count; index++)
                {
                    var operation = Operations[index];
                    if (File.Exists(operation.DestinationPath) || Directory.Exists(operation.DestinationPath))
                        throw new InvalidOperationException($"Destination appeared while applying: {operation.DestinationPath}");
                    File.Move(_states[index].StagedPath!, operation.DestinationPath);
                    _states[index].StagedPath = null;
                    _states[index].Installed = true;
                }
            }
            catch
            {
                Rollback();
                throw;
            }
        }

        internal void Rollback()
        {
            var failures = new List<string>();
            for (var index = Operations.Count - 1; index >= 0; index--)
            {
                var operation = Operations[index];
                var state = _states[index];
                try
                {
                    if (state.Installed && File.Exists(operation.DestinationPath))
                    {
                        var installedHash = SHA256.HashData(File.ReadAllBytes(operation.DestinationPath));
                        if (!CryptographicOperations.FixedTimeEquals(installedHash, SHA256.HashData(operation.ContentBytes)))
                        {
                            failures.Add($"Changed destination was preserved: {operation.DestinationPath}; backup: {state.BackupPath ?? "none"}");
                            continue;
                        }
                        File.Delete(operation.DestinationPath);
                    }

                    if (state.BackupPath is { } backupPath && File.Exists(backupPath))
                    {
                        if (File.Exists(operation.SourcePath) || Directory.Exists(operation.SourcePath))
                        {
                            failures.Add($"Original path was recreated and was preserved: {operation.SourcePath}; backup: {backupPath}");
                            continue;
                        }
                        File.Move(backupPath, operation.SourcePath!);
                        state.BackupPath = null;
                    }
                }
                catch (Exception exception)
                {
                    failures.Add($"Rollback failed for '{operation.DestinationPath}': {exception.Message}");
                }
                finally
                {
                    TryDelete(state.StagedPath);
                }
            }

            if (failures.Count > 0)
                throw new InvalidOperationException("File rollback was incomplete:\n" + string.Join(Environment.NewLine, failures));
        }

        internal void Commit()
        {
            foreach (var state in _states)
            {
                TryDelete(state.StagedPath);
                if (state.BackupPath is { } backupPath)
                {
                    try
                    {
                        File.Delete(backupPath);
                    }
                    catch (IOException exception)
                    {
                        Console.Error.WriteLine($"Could not remove recovery backup '{backupPath}': {exception.Message}");
                    }
                    catch (UnauthorizedAccessException exception)
                    {
                        Console.Error.WriteLine($"Could not remove recovery backup '{backupPath}': {exception.Message}");
                    }
                }
            }
        }

        static string UniqueSiblingPath(string directory, string suffix)
        {
            string path;
            do
            {
                path = Path.Combine(directory, suffix + "-" + Guid.NewGuid().ToString("N"));
            } while (File.Exists(path) || Directory.Exists(path));
            return path;
        }

        static bool TextMatches(byte[] bytes, SourceText expected)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new StreamReader(stream, expected.Encoding ?? new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return string.Equals(reader.ReadToEnd(), expected.ToString(), StringComparison.Ordinal);
        }

        static void TryDelete(string? path)
        {
            if (path is not null && File.Exists(path))
                File.Delete(path);
        }
    }

    sealed class OperationState
    {
        internal string? StagedPath { get; set; }
        internal string? BackupPath { get; set; }
        internal bool Installed { get; set; }
    }

    sealed class FileOperation
    {
        FileOperation(string projectPath, string? sourcePath, string destinationPath, SourceText? originalText, SourceText finalText)
        {
            ProjectPath = Path.GetFullPath(projectPath);
            SourcePath = sourcePath is null ? null : Path.GetFullPath(sourcePath);
            DestinationPath = Path.GetFullPath(destinationPath);
            OriginalText = originalText;
            ContentBytes = ToBytes(finalText);
            ContentText = finalText.ToString();
        }

        internal string ProjectPath { get; }
        internal string? SourcePath { get; }
        internal string DestinationPath { get; }
        internal SourceText? OriginalText { get; }
        internal byte[] ContentBytes { get; }
        internal string ContentText { get; }
        internal byte[]? ExpectedBytes { get; set; }

        internal static FileOperation Create(string projectPath, string path, SourceText text) =>
            new(projectPath, null, path, null, text);

        internal static FileOperation Replace(string projectPath, string path, SourceText original, SourceText updated) =>
            new(projectPath, path, path, original, updated);

        internal static FileOperation Rename(string projectPath, string source, string destination, SourceText original, SourceText updated) =>
            new(projectPath, source, destination, original, updated);

        static byte[] ToBytes(SourceText text)
        {
            using var stream = new MemoryStream();
            using (var writer = new StreamWriter(stream, text.Encoding ?? new UTF8Encoding(false), 1024, leaveOpen: true))
            {
                text.Write(writer);
                writer.Flush();
            }
            return stream.ToArray();
        }
    }
}
