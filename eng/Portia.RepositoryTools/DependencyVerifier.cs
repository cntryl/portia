using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;

namespace Cntryl.Portia.RepositoryTools;

static class DependencyVerifier
{
    internal static int Verify(string[] args)
    {
        var root = ParseRoot(args);
        var failures = new List<string>();

        foreach (var lockFile in EnumerateLockFiles(root).Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(lockFile));
            var dependencies = document.RootElement.GetProperty("dependencies");
            foreach (var framework in dependencies.EnumerateObject())
            {
                foreach (var package in framework.Value.EnumerateObject())
                {
                    var version = package.Value.TryGetProperty("resolved", out var resolved)
                        ? resolved.GetString() ?? string.Empty
                        : string.Empty;
                    if (!package.Name.StartsWith("Cntryl.Portia.", StringComparison.Ordinal) && version.Contains('-'))
                    {
                        var relativePath = Path.GetRelativePath(root, lockFile);
                        failures.Add($"{relativePath} [{framework.Name}] {package.Name} {version}");
                    }
                }
            }
        }

        var centralVersions = Path.Combine(root, "Directory.Packages.props");
        if (File.Exists(centralVersions))
        {
            var document = XDocument.Load(centralVersions);
            foreach (var package in document.Descendants("PackageVersion"))
            {
                var name = (string?)package.Attribute("Include") ?? string.Empty;
                var version = (string?)package.Attribute("Version") ?? string.Empty;
                if (!name.StartsWith("Cntryl.Portia.", StringComparison.Ordinal) && version.Contains('-'))
                    failures.Add($"Directory.Packages.props {name} {version}");
            }
        }

        if (failures.Count > 0)
        {
            Console.Error.WriteLine("Prerelease external dependencies are not allowed:");
            foreach (var failure in failures)
                Console.Error.WriteLine(failure);
            return 1;
        }

        Console.WriteLine("All declared and locked external NuGet dependencies are stable.");
        return 0;
    }

    static string ParseRoot(string[] args)
    {
        var root = FindRepositoryRoot();
        for (var index = 1; index < args.Length; index++)
        {
            if (args[index] == "--root")
            {
                if (++index >= args.Length)
                    throw new ArgumentException("Expected a path after --root.");
                root = args[index];
            }
            else
            {
                throw new ArgumentException($"Unknown verify-stable-dependencies argument: {args[index]}");
            }
        }

        return Path.GetFullPath(root);
    }

    static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Portia.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Portia.slnx above the repository-tools assembly.");
    }

    static IEnumerable<string> EnumerateLockFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "packages.lock.json"))
                yield return file;

            foreach (var child in Directory.EnumerateDirectories(directory)
                         .Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(child);
                if (name is not ("obj" or "bin" or "artifacts" or ".git"))
                    pending.Push(child);
            }
        }
    }
}
