namespace Cntryl.Portia;

/// <summary>Explicit OpenAPI description contracts.</summary>
public sealed class OpenApiDescriptionConfigurationTests
{
    /// <summary>Validates explicit description configuration and generated document behavior.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ShouldRejectBlankDescriptionGivenSharedMember(string description)
    {
        // Arrange
        var options = new PortiaOpenApiOptions();

        // Act
        var exception = Record.Exception(() => options.DescribeMember("limit", description));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    /// <summary>Validates explicit description configuration and generated document behavior.</summary>
    [Fact]
    public void ShouldRejectNestedSelectorGivenSchemaProperty()
    {
        // Arrange
        var options = new PortiaOpenApiOptions();

        // Act
        var exception = Record.Exception(() => options.DescribeSchema<OpenApiDescribedRequest>("Request.",
            d => d.Property(x => x.Payload.Name, "Name.")));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    /// <summary>Validates explicit description configuration and generated document behavior.</summary>
    [Fact]
    public void ShouldRejectComputedSelectorGivenEndpointDescription()
    {
        // Arrange
        var endpoint = new PortiaEndpointConfiguration<OpenApiDescribedRequest, OpenApiDescribedNode>();

        // Act
        var exception = Record.Exception(() => endpoint.Describe(x => x.Limit + 1, "Limit."));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    /// <summary>Validates explicit description configuration and generated document behavior.</summary>
    [Theory]
    [InlineData("member")]
    [InlineData("schema")]
    [InlineData("property")]
    public void ShouldRejectDuplicateRegistrationGivenDescriptionScope(string scope)
    {
        // Arrange
        var options = new PortiaOpenApiOptions();
        _ = options.DescribeMember("limit", "Limit.").DescribeSchema<OpenApiDescribedRequest>("Request.");

        // Act
        var exception = Record.Exception(() =>
        {
            if (scope == "member")
                _ = options.DescribeMember("limit", "Again.");
            else if (scope == "schema")
                _ = options.DescribeSchema<OpenApiDescribedRequest>("Again.");
            else
                _ = options.DescribeSchema<OpenApiDescribedNode>("Node.", d => d
                    .Property(x => x.Name, "Name.").Property(x => x.Name, "Again."));
        });

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Validates explicit description configuration and generated document behavior.</summary>
    [Fact]
    public void ShouldRejectLateDescriptionGivenMappedEndpoint()
    {
        // Arrange
        var endpoint = new PortiaEndpointConfiguration<OpenApiDescribedRequest, OpenApiDescribedNode>();
        endpoint.Validate(members: [new("Limit", "query", true), new("Payload", "body", false)]);

        // Act
        var exception = Record.Exception(() => endpoint.Describe(x => x.Limit, "Limit."));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Validates explicit description configuration and generated document behavior.</summary>
    [Fact]
    public void ShouldRejectNonConstructorDescriptionGivenEndpointValidation()
    {
        // Arrange
        var endpoint = new PortiaEndpointConfiguration<OpenApiDescribedRequest, OpenApiDescribedNode>();
        _ = endpoint.Describe(x => x.Limit, "Limit.");

        // Act
        var exception = Record.Exception(() => endpoint.Validate(members: []));

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("not a constructor-bound request member", failure.Message, StringComparison.Ordinal);
    }
}
