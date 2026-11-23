using System.Text.Json;
using Cntryl.Portia.CompilerBenchmarks;

namespace Cntryl.Portia.Consumer;

public sealed class CompilerAccountsCaptureTests
{
    [Theory]
    [InlineData("unlisted")]
    [InlineData("reference-hash")]
    [InlineData("analyzer-hash")]
    [InlineData("modified-file")]
    [InlineData("escape")]
    [InlineData("format")]
    public void ShouldRejectCaptureGivenUnverifiedInput(string mutation)
    {
        // Arrange
        using var capture = new Capture(mutation);

        // Act
        var error = Record.Exception(() => AccountsCapture.Read(capture.Directory));

        // Assert
        Assert.IsType<InvalidOperationException>(error);
    }

    [Fact]
    public void ShouldReadCaptureGivenEveryInputMatchesManifest()
    {
        // Arrange
        using var capture = new Capture("none");

        // Act
        var manifest = AccountsCapture.Read(capture.Directory);

        // Assert
        Assert.Single(manifest.Projects);
        Assert.Single(manifest.Files);
    }

    sealed class Capture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "portia-accounts-test-" + Guid.NewGuid().ToString("N"));

        public Capture(string mutation)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var file = Path.Combine(Directory, "input.dll");
            File.WriteAllText(file, "frozen input");
            var hash = CompilerHost.FileHash(file);
            var relative = mutation == "escape" ? "../input.dll" : "input.dll";
            var project = new CapturedProject("Accounts.Api", Directory, [], [], [], [],
                [new CapturedReference(relative, mutation == "reference-hash" ? "wrong" : hash, [], false)],
                [new CapturedAnalyzer("Example", relative, mutation == "analyzer-hash" ? "wrong" : hash)]);
            var manifest = new AccountsManifest(mutation == "format" ? "unsupported" : AccountsCapture.Format,
                "revision", "sdk", [project], mutation == "unlisted" ? [] : new Dictionary<string, string> { [relative] = hash });
            File.WriteAllText(Path.Combine(Directory, "accounts.json"), JsonSerializer.Serialize(manifest));

            if (mutation == "modified-file")
                File.WriteAllText(file, "changed input");
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
