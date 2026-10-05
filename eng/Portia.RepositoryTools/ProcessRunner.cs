using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Cntryl.Portia.RepositoryTools;

static class ProcessRunner
{
    internal static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments,
        string workingDirectory, IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var pair in environment)
                startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"Could not start {executable}.");

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return new ProcessResult(process.ExitCode,
            await standardOutput.ConfigureAwait(false) + await standardError.ConfigureAwait(false));
    }

    internal sealed record ProcessResult(int ExitCode, string Output);
}
