namespace Cntryl.Portia.Consumer;

public sealed class ContractAttributeDiagnosticTests
{
    [Theory]
    [InlineData("[Range(typeof(decimal), \"1\", \"10\")] public decimal Amount { get; init; }")]
    [InlineData("[DefaultValue(typeof(int), \"5\")] public int Amount { get; init; }")]
    [InlineData("[StringLength(2, MinimumLength = 3)] public string? Name { get; init; }")]
    [InlineData("[MinLength(-1)] public string? Name { get; init; }")]
    [InlineData("[MinLength(2)] public int Count { get; init; }")]
    [InlineData("[Required] public string? Name { private get; init; }")]
    [InlineData("[Range(typeof(decimal), \"1\", \"10\")] public decimal Amount;")]
    [InlineData("[RegularExpression(\"[\")] public string? Name { get; init; }")]
    public void ShouldRejectInvalidContractMetadataGivenUnsupportedDeclaration(string member)
    {
        // Arrange
        var source = $$"""
            using Cntryl.Portia;
            using System.ComponentModel;
            using System.ComponentModel.DataAnnotations;
            public sealed class Contract : IRequest { {{member}} }
            """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new JsonRootMarkerGenerator());

        // Assert
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA031");
    }
    [Fact]
    public void ShouldIgnoreUnsupportedAttributeGivenUnrelatedModel()
    {
        // Arrange
        const string source = """
            using System.ComponentModel.DataAnnotations;
            public sealed class UnrelatedModel
            {
                [Range(typeof(decimal), "1", "10")]
                public decimal Value { get; init; }
            }
            """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new JsonRootMarkerGenerator());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA031");
    }

    [Theory]
    [InlineData("[property: DefaultValue(null)] string? Value = null")]
    [InlineData("[property: DefaultValue(float.NaN)] float Value = float.NaN")]
    public void ShouldCompileMetadataGivenNullableOrNonFiniteDefault(string parameter)
    {
        // Arrange
        var source = ContextSource("public sealed record Model(" + parameter + ");");

        // Act
        var diagnostics = GeneratorCompilation.OutputDiagnostics(source, new JsonRootMarkerGenerator());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity is Microsoft.CodeAnalysis.DiagnosticSeverity.Error or Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    }

    [Fact]
    public void ShouldGenerateTitleGivenDisplayOnPrimaryConstructorParameter()
    {
        // Arrange
        var source = ContextSource("public sealed record Model([Display(Name = \"Value title\")] string Value);");

        // Act
        var generated = GeneratorCompilation.GeneratedSource(source, new JsonRootMarkerGenerator());

        // Assert
        Assert.Contains("Title = \"Value title\"", generated, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldPreserveDefaultValueTypeGivenEnumOrValueTypeDefault(bool nullableEnum)
    {
        // Arrange
        var model = nullableEnum
            ? "public enum Mode { First = 1 } public sealed record Model([property: DefaultValue(Mode.First)] Mode? Value = Mode.First);"
            : "public sealed record Model(DateTime Value = default);";
        var source = ContextSource(model);

        // Act
        var assembly = GeneratorCompilation.Compile(source, new JsonRootMarkerGenerator());
        var factory = assembly.GetTypes().Single(type => type.Name.StartsWith("PortiaJsonContextFactory_", StringComparison.Ordinal));
        var metadata = (PortiaContractMetadata)factory.GetMethod("GetMetadata")!.Invoke(null, [assembly.GetType("Model")!])!;
        var member = metadata.Members.Single();

        // Assert
        Assert.NotNull(member.BindingDefaultValue);
        if (nullableEnum)
            Assert.Equal(member.DefaultValue, member.BindingDefaultValue);
        else
            Assert.Equal(default(DateTime), Assert.IsType<DateTime>(member.BindingDefaultValue));
    }

    static string ContextSource(string model) => $$"""
        #nullable enable
        using System;
        using System.ComponentModel;
        using System.ComponentModel.DataAnnotations;
        using System.Text.Json;
        using System.Text.Json.Serialization;
        using System.Text.Json.Serialization.Metadata;
        using Cntryl.Portia;
        {{model}}
        [PortiaJsonContext, JsonSerializable(typeof(Model))]
        sealed class Context : JsonSerializerContext
        {
            public Context(JsonSerializerOptions options) : base(options) { }
            protected override JsonSerializerOptions? GeneratedSerializerOptions => null;
            public override JsonTypeInfo? GetTypeInfo(Type type) => null;
        }
        """;

}
