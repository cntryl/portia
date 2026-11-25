using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Cntryl.Portia.Consumer;

public sealed class RequestActorAnalyzerTests
{
    static readonly string[] MissingActorRequestNames = ["Missing", "MissingStruct", "Multiple", "PartialMissing"];

    [Fact]
    public void ShouldReportMissingOrMultipleActorsGivenOptedInConcreteRequests()
    {
        // Arrange
        const string source = """
                              using Cntryl.Portia;
                              public sealed record Missing : IRequest;
                              public sealed record Multiple : IRequest, IPublicRequest, ISystemRequest;
                              public readonly record struct MissingStruct : IRequest;
                              public sealed partial record PartialMissing : IRequest;
                              public sealed partial record PartialMissing;
                              public sealed record PublicOne : IRequest, IPublicRequest;
                              public sealed record StreamOne : IStreamRequest<string>, IAuthenticatedRequest;
                              public abstract record AbstractMissing : IRequest;
                              public interface InterfaceMissing : IRequest;
                              """;
        var options = ImmutableDictionary<string, string>.Empty
            .Add("build_property.PortiaRequireRequestActor", "true");

        // Act
        var diagnostics = GeneratorCompilation.DiagnosticsWithOptions(source,
            ImmutableDictionary<string, ReportDiagnostic>.Empty, options, new RequestActorAnalyzer());

        // Assert
        Assert.Equal(4, diagnostics.Count(diagnostic => diagnostic.Id == "PORTIA033"));
        var actorDiagnostics = diagnostics.Where(diagnostic => diagnostic.Id == "PORTIA033").ToArray();
        Assert.Equal(MissingActorRequestNames, actorDiagnostics
            .Select(diagnostic => diagnostic.GetMessage(CultureInfo.InvariantCulture).Split('\'')[1])
            .Order(StringComparer.Ordinal));
        Assert.Contains("found none", Assert.Single(actorDiagnostics, diagnostic =>
            diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains("'Missing'", StringComparison.Ordinal))
            .GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.Contains("found IPublicRequest, ISystemRequest", Assert.Single(actorDiagnostics, diagnostic =>
            diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains("'Multiple'", StringComparison.Ordinal))
            .GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldClassifyInheritedAndDiamondFamiliesOnceGivenPartialRequest()
    {
        // Arrange
        const string source = """
                              using Cntryl.Portia;
                              public interface ILeft : IPublicRequest;
                              public interface IRight : IAuthenticatedRequest;
                              public interface IDiamond : ILeft, IRight;
                              public abstract record Family : IRequest, IDiamond;
                              public sealed partial record Concrete : Family;
                              public sealed partial record Concrete;
                              """;
        var options = ImmutableDictionary<string, string>.Empty
            .Add("build_property.PortiaRequireRequestActor", "true");

        // Act
        var diagnostics = GeneratorCompilation.DiagnosticsWithOptions(source,
            ImmutableDictionary<string, ReportDiagnostic>.Empty, options, new RequestActorAnalyzer());

        // Assert
        var diagnostic = Assert.Single(diagnostics, item => item.Id == "PORTIA033");
        Assert.Contains("found IAuthenticatedRequest, IPublicRequest", diagnostic.GetMessage(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldDoNothingGivenNoOptIn()
    {
        // Arrange
        const string source = "public sealed record Request : Cntryl.Portia.IRequest;";

        // Act
        var diagnostics = GeneratorCompilation.DiagnosticsWithOptions(source,
            ImmutableDictionary<string, ReportDiagnostic>.Empty, ImmutableDictionary<string, string>.Empty,
            new RequestActorAnalyzer());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA033");
    }
}
