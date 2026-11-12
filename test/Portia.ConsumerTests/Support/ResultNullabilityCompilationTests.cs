using Microsoft.CodeAnalysis;

namespace Cntryl.Portia.Consumer;

/// <summary>Verifies the public nullable annotations seen by an independent consumer compilation.</summary>
public sealed class ResultNullabilityCompilationTests
{
    /// <summary>Nullable successes and invalid value access retain their runtime behavior.</summary>
    [Fact]
    public void ShouldPreserveValueStateValidationGivenNullableSuccess()
    {
        // Arrange
        var nullableSuccess = Result<string?>.Success(null);
        var failure = Result<string>.Failure(new RequestError(RequestErrorKind.NotFound, "missing"));

        // Act
        Result<string> uninitialized = default;

        // Assert
        Assert.True(nullableSuccess.IsSuccess);
        Assert.Null(nullableSuccess.Value);
        _ = Assert.Throws<InvalidOperationException>(() => failure.Value);
        _ = Assert.Throws<InvalidOperationException>(() => uninitialized.Value);
    }

    /// <summary>A non-nullable success value can be returned without suppressing nullable warnings.</summary>
    [Fact]
    public void ShouldCompileWithoutWarningsGivenNonNullableResultValue()
    {
        // Arrange
        const string source = """
                                                                           #nullable enable
                                                                           using Cntryl.Portia;

                                                                           public static class Consumer
                                                                           {
                                                                               public static string Read(Result<string> result)
                                                                               {
                                                                                   if (!result.IsSuccess)
                                                                                       throw new System.InvalidOperationException();

                                                                                   return result.Value;
                                                                               }

                                                                               public static string? ReadNullable(Result<string?> result)
                                                                               {
                                                                                   if (!result.IsSuccess)
                                                                                       throw new System.InvalidOperationException();

                                                                                   return result.Value;
                                                                               }
                                                                           }
                                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.WarningsAsErrorsDiagnostics(source);

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>A nullable type argument remains nullable even in the successful branch.</summary>
    [Fact]
    public void ShouldReportNullableDereferenceGivenNullableResultValue()
    {
        // Arrange
        const string source = """
                                                                           #nullable enable
                                                                           using Cntryl.Portia;

                                                                           public static class Consumer
                                                                           {
                                                                               public static int Read(Result<string?> result)
                                                                               {
                                                                                   if (!result.IsSuccess)
                                                                                       throw new System.InvalidOperationException();

                                                                                   return result.Value.Length;
                                                                               }
                                                                           }
                                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.WarningsAsErrorsDiagnostics(source);

        // Assert
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Id == "CS8602" && diagnostic.Severity == DiagnosticSeverity.Error);
    }
}
