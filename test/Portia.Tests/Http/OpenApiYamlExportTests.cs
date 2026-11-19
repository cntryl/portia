using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
            await app.WritePortiaOpenApiYamlAsync();

            // Assert
            var path = Path.Combine(directory, "openapi.yml");
            Assert.True(File.Exists(path));
            AssertHealthEndpoint(await File.ReadAllTextAsync(path));
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
            await app.WritePortiaOpenApiYamlAsync(path);

            // Assert
            Assert.True(File.Exists(path));
            AssertHealthEndpoint(await File.ReadAllTextAsync(path));
        }
        finally
        {
            await DisposeAppAsync();
            Directory.Delete(directory, recursive: true);
        }
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
        var exception = await Record.ExceptionAsync(() => app.WritePortiaOpenApiYamlAsync());

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
            var exception = await Record.ExceptionAsync(() => app.WritePortiaOpenApiYamlAsync(path));

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
