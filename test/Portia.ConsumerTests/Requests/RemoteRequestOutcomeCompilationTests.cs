using System.Diagnostics;
using System.Runtime.Loader;

namespace Cntryl.Portia.Consumer;

/// <summary>Checks exhaustive outcome handling through referenced assembly metadata.</summary>
public sealed class RemoteRequestOutcomeCompilationTests
{
    static readonly Lazy<Task<string>> CompilerPath = new(ResolveCompilerPathAsync);

    /// <summary>All no-result cases can be handled without a fallback, including a completed business rejection.</summary>
    [Fact]
    public async Task ShouldCompileExhaustiveSwitchForNoResultOutcomes()
    {
        var describe = await CompileSwitchAsync<RemoteRequestOutcome>(resultBearing: false);

        Assert.Equal("completed", describe(new RemoteRequestOutcome.Completed(Result.Success)));
        Assert.Equal("rejected:business rejected", describe(new RemoteRequestOutcome.Completed(
            Result.Failure(RequestError.Conflict("business rejected")))));
        Assert.Equal("NotDispatched", describe(new RemoteRequestOutcome.NotDispatched()));
        Assert.Equal("CancellationForwarded", describe(new RemoteRequestOutcome.CancellationForwarded()));
        Assert.Equal("OutcomeUnknown", describe(new RemoteRequestOutcome.OutcomeUnknown()));
    }

    /// <summary>The generic cases retain result payloads and distinguish unresolved transport outcomes.</summary>
    [Fact]
    public async Task ShouldCompileExhaustiveSwitchForResultOutcomes()
    {
        var describe = await CompileSwitchAsync<RemoteRequestOutcome<int>>(resultBearing: true);

        Assert.Equal("completed:42", describe(new RemoteRequestOutcome<int>.Completed(Result<int>.Success(42))));
        Assert.Equal("rejected:business rejected", describe(new RemoteRequestOutcome<int>.Completed(
            Result<int>.Failure(RequestError.Conflict("business rejected")))));
        Assert.Equal("NotDispatched", describe(new RemoteRequestOutcome<int>.NotDispatched()));
        Assert.Equal("CancellationForwarded", describe(new RemoteRequestOutcome<int>.CancellationForwarded()));
        Assert.Equal("OutcomeUnknown", describe(new RemoteRequestOutcome<int>.OutcomeUnknown()));
    }

    /// <summary>A consumer cannot accidentally omit the unknown case while compiling with warnings as errors.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRequireExplicitUnknownOutcomeCase(bool resultBearing)
    {
        using var compiled = await CompileAsync(resultBearing, includeUnknown: false);

        Assert.NotEqual(0, compiled.ExitCode);
        Assert.Contains("error CS", compiled.Output, StringComparison.Ordinal);
        Assert.Contains("OutcomeUnknown", compiled.Output, StringComparison.Ordinal);
    }

    static async Task<Func<TOutcome, string>> CompileSwitchAsync<TOutcome>(bool resultBearing)
    {
        using var compiled = await CompileAsync(resultBearing, includeUnknown: true);
        Assert.True(compiled.ExitCode == 0, compiled.Output);
        using var image = File.OpenRead(compiled.AssemblyPath);
        var assembly = AssemblyLoadContext.Default.LoadFromStream(image);
        return assembly.GetType("Consumer")!.GetMethod("Describe")!.CreateDelegate<Func<TOutcome, string>>();
    }

    static async Task<CompilerResult> CompileAsync(bool resultBearing, bool includeUnknown)
    {
        var outcome = resultBearing ? "RemoteRequestOutcome<int>" : "RemoteRequestOutcome";
        var completed = resultBearing ? "\"completed:\" + completed.Result.Value" : "\"completed\"";
        var unknown = includeUnknown
            ? $"{outcome}.OutcomeUnknown unknown => unknown.Kind.ToString(),"
            : string.Empty;
        var source = $$"""
            using Cntryl.Portia;

            public static class Consumer
            {
                public static string Describe({{outcome}} outcome) => outcome switch
                {
                    {{outcome}}.Completed completed => completed.Result.IsSuccess
                        ? {{completed}} : "rejected:" + completed.Result.Error!.Message,
                    {{outcome}}.NotDispatched rejected => rejected.Kind.ToString(),
                    {{outcome}}.CancellationForwarded cancellation => cancellation.Kind.ToString(),
                    {{unknown}}
                };
            }
            """;
        var directory = Path.Combine(Path.GetTempPath(), "portia-outcome-consumer-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            var sourcePath = Path.Combine(directory, "Consumer.cs");
            var assemblyPath = Path.Combine(directory, "OutcomeConsumer_" + Guid.NewGuid().ToString("N") + ".dll");
            var responsePath = Path.Combine(directory, "compiler.rsp");
            await File.WriteAllTextAsync(sourcePath, source);
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Append(typeof(RemoteRequestOutcome).Assembly.Location)
                .Distinct(StringComparer.Ordinal);
            await File.WriteAllLinesAsync(responsePath,
            [
                "-nologo", "-target:library", "-langversion:15.0", "-nullable:enable", "-warnaserror+",
                "-nostdlib+", "-preferreduilang:en-US", "-out:" + Quote(assemblyPath),
                .. references.Select(path => "-reference:" + Quote(path)),
                Quote(sourcePath)
            ]);
            var output = await RunDotnetAsync(["exec", await CompilerPath.Value, "-noconfig", "@" + responsePath]);
            return new CompilerResult(directory, assemblyPath, output);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    // The NuGet Roslyn version used by generator tests can lag the SDK's language support. These
    // public API tests exercise stable C# 15 using the selected .NET 11 SDK, without Preview parsing.
    static async Task<string> ResolveCompilerPathAsync()
    {
        var selected = await RunDotnetAsync(["--version"]);
        Assert.True(selected.ExitStatus.ExitCode == 0, selected.StandardOutput + selected.StandardError);
        var version = selected.StandardOutput.Trim();
        Assert.True(version.StartsWith("11.", StringComparison.Ordinal) && !version.Contains('-'),
            "Outcome compilation requires a stable .NET 11 SDK; selected SDK: " + version);
        var installed = await RunDotnetAsync(["--list-sdks"]);
        Assert.True(installed.ExitStatus.ExitCode == 0, installed.StandardOutput + installed.StandardError);
        var sdk = Assert.Single(installed.StandardOutput.Split('\n'),
            line => line.StartsWith(version + " [", StringComparison.Ordinal));
        var root = sdk[(sdk.IndexOf('[') + 1)..sdk.LastIndexOf(']')];
        return Path.Combine(root, version, "Roslyn", "bincore", "csc.dll");
    }

    static async Task<ProcessTextOutput> RunDotnetAsync(IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await Process.RunAndCaptureTextAsync(startInfo, timeout.Token);
    }

    static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    sealed class CompilerResult(string directory, string assemblyPath, ProcessTextOutput output) : IDisposable
    {
        internal string AssemblyPath { get; } = assemblyPath;
        internal int ExitCode => output.ExitStatus.ExitCode;
        internal string Output => output.StandardOutput + output.StandardError;
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
