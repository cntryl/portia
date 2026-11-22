using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Cntryl.Portia;

/// <summary>One-call OpenAPI YAML export coverage.</summary>
[Collection(OpenApiYamlExportTestGroup.Name)]
public sealed class OpenApiYamlExportTests : IAsyncDisposable
{
    WebApplication? _app;

    /// <summary>The default path receives a parseable document without starting the host.</summary>
    [Fact]
    public async Task ShouldWriteYamlToDefaultPathGivenUnstartedConfiguredApp()
    {
        // Arrange
        var previousDirectory = Environment.CurrentDirectory;
        var directory = Path.Combine(Path.GetTempPath(), $"portia-openapi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Environment.CurrentDirectory = directory;
            var app = CreateApp();
            _app = app;
            _ = app.MapGet("/health", () => Results.Ok()).WithName("health");

            // Act
            await app.WritePortiaOpenApiYamlAsync(cancellationToken: TestContext.Current.CancellationToken);

            // Assert
            var path = Path.Combine(directory, "openapi.yml");
            Assert.True(File.Exists(path));
            AssertHealthEndpoint(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            await DisposeAppAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The supplied path receives the configured OpenAPI document.</summary>
    [Fact]
    public async Task ShouldWriteYamlToCustomPathGivenUnstartedConfiguredApp()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), $"portia-openapi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "custom-openapi.yml");
        try
        {
            var app = CreateApp();
            _app = app;
            _ = app.MapGet("/health", () => Results.Ok()).WithName("health");

            // Act
            await app.WritePortiaOpenApiYamlAsync(path, cancellationToken: TestContext.Current.CancellationToken);

            // Assert
            Assert.True(File.Exists(path));
            AssertHealthEndpoint(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            await DisposeAppAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Pre-start export and the running endpoint serve identical bytes.</summary>
    [Fact]
    public async Task ShouldServeSameYamlGivenExportBeforeHostStartup()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"portia-openapi-{Guid.NewGuid():N}.yml");
        var app = CreateApp();
        _app = app;
        var group = app.MapGroup("/ops");
        _ = group.MapGet("/health", () => Results.Ok()).WithName("opsHealth");
        _ = app.MapPortiaOpenApi();
        try
        {
            // Act
            await app.WritePortiaOpenApiYamlAsync(path, cancellationToken: TestContext.Current.CancellationToken);
            await app.StartAsync(TestContext.Current.CancellationToken);
            using var client = app.GetTestClient();
            var served = await client.GetByteArrayAsync("/openapi/v1.yml", TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken), served);
            Assert.Contains("/ops/health:", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Invalid paths are rejected before document composition.</summary>
    [Fact]
    public async Task ShouldRejectInvalidPathGivenConfiguredDocument()
    {
        // Arrange
        var composed = false;
        var app = CreateApp(options => options.OpenApi.ConfigureDocument = _ => composed = true);
        _app = app;

        // Act
        var exception = await Record.ExceptionAsync(() => app.WritePortiaOpenApiYamlAsync(" ", cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<ArgumentException>(exception);
        Assert.False(composed);
    }

    /// <summary>Export explains when HTTP services were not registered.</summary>
    [Fact]
    public async Task ShouldExplainMissingHttpRegistrationGivenYamlExport()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        _app = app;

        // Act
        var exception = await Record.ExceptionAsync(() => app.WritePortiaOpenApiYamlAsync(cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("AddHttp()", invalidOperation.Message, StringComparison.Ordinal);
    }

    /// <summary>Failed document composition leaves the destination absent.</summary>
    [Fact]
    public async Task ShouldNotWriteArtifactGivenDocumentGenerationFailure()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), $"portia-openapi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "failed-openapi.yml");
        var generationFailure = new InvalidOperationException("OpenAPI generation failed.");
        try
        {
            var app = CreateApp(options => options.OpenApi.ConfigureDocument = _ => throw generationFailure);
            _app = app;
            _ = app.MapGet("/health", () => Results.Ok()).WithName("health");

            // Act
            var exception = await Record.ExceptionAsync(() => app.WritePortiaOpenApiYamlAsync(path, cancellationToken: TestContext.Current.CancellationToken));

            // Assert
            Assert.Same(generationFailure, exception);
            Assert.False(File.Exists(path));
        }
        finally
        {
            await DisposeAppAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Cancellation during the export preserves the previous destination.</summary>
    [Fact]
    public async Task ShouldPreserveExistingFileGivenCancellationDuringComposition()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"portia-openapi-{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(path, "previous document", TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var app = CreateApp(options => options.OpenApi.ConfigureDocument = _ => cancellation.Cancel());
        _app = app;
        try
        {
            // Act
            var exception = await Record.ExceptionAsync(() => app.WritePortiaOpenApiYamlAsync(path, cancellation.Token));

            // Assert
            Assert.IsAssignableFrom<OperationCanceledException>(exception);
            Assert.Equal("previous document", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => DisposeAppAsync();

    async ValueTask DisposeAppAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
            _app = null;
        }
    }

    static WebApplication CreateApp(Action<PortiaHttpOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddHttp(configure);
        return builder.Build();
    }

    static void AssertHealthEndpoint(string yaml)
    {
        var settings = new OpenApiReaderSettings();
        settings.AddYamlReader();
        var parsed = OpenApiDocument.Parse(yaml, "yaml", settings);
        var diagnostic = Assert.IsType<OpenApiDiagnostic>(parsed.Diagnostic);
        var document = Assert.IsType<OpenApiDocument>(parsed.Document);
        Assert.Empty(diagnostic.Errors);
        Assert.Equal(OpenApiSpecVersion.OpenApi3_1, diagnostic.SpecificationVersion);
        Assert.Contains("/health", document.Paths.Keys);
        Assert.Contains(HttpMethod.Get, document.Paths["/health"].Operations!.Keys);
    }
}

/// <summary>Serializes tests that temporarily change the process working directory.</summary>
[CollectionDefinition(OpenApiYamlExportTestGroup.Name, DisableParallelization = true)]
public sealed class OpenApiYamlExportTestGroup
{
    /// <summary>The test collection name.</summary>
    public const string Name = "OpenAPI YAML export";
}
