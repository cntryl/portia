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

        var output = await Process.RunAndCaptureTextAsync(startInfo).ConfigureAwait(false);
        return new ProcessResult(output.ExitStatus.ExitCode, output.StandardOutput + output.StandardError);
    }

    internal sealed record ProcessResult(int ExitCode, string Output);
}
