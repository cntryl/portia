using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cntryl.Portia;

/// <summary>Adversarial contract metadata cases found during the implementation walk.</summary>
public sealed class ContractMetadataEdgeCaseTests
{
    /// <summary>A CLR identity must not collide with another member's distinct JSON wire name.</summary>
    [Fact]
    public void ShouldResolveDeclaredMemberGivenCrossedClrAndJsonNames()
    {
        // Arrange
        var first = new PortiaContractMemberMetadata("First", typeof(string)) { JsonName = "second" };
        var other = new PortiaContractMemberMetadata("Other", typeof(string)) { JsonName = "First" };
        var registry = new PortiaContractMetadataRegistry([type => type == typeof(object)
            ? new PortiaContractMetadata(type) { Members = [first, other] } : null]);

        // Act
        var result = registry.Member(typeof(object), "First", JsonNamingPolicy.SnakeCaseLower);

        // Assert
        Assert.Same(first, result);
    }

    /// <summary>Integer Range conversion on fractional values cannot advertise literal numeric bounds.</summary>
    [Fact]
    public void ShouldAvoidMisleadingBoundsGivenIntegerRangeOnFractionalValue()
    {
        // Arrange
        var range = new RangeAttribute(1, 10);
        var member = new PortiaContractMemberMetadata("Value", typeof(double)) { Rules = [range] };
        var schema = new JsonObject { ["type"] = "number" };

        // Act
        PortiaJsonConstraints.Apply(schema, member, JsonSerializerOptions.Web);

        // Assert
        Assert.True(range.IsValid(10.4));
        Assert.Null(schema["maximum"]);
        Assert.NotNull(schema["x-portia-range"]);
    }

    /// <summary>A valid astral character must not be rejected by a stronger JSON code-point minimum.</summary>
    [Fact]
    public void ShouldPreserveValidUnicodeGivenUtf16LengthConstraint()
    {
        // Arrange
        const string value = "😀";
        var length = new StringLengthAttribute(3) { MinimumLength = 2 };
        var member = new PortiaContractMemberMetadata("Value", typeof(string)) { Rules = [length] };
        var schema = new JsonObject { ["type"] = "string" };

        // Act
        PortiaJsonConstraints.Apply(schema, member, JsonSerializerOptions.Web);

        // Assert
        Assert.True(length.IsValid(value));
        Assert.True((schema["minLength"]?.GetValue<int>() ?? 0) <= value.EnumerateRunes().Count());
        Assert.Equal(2, schema["x-portia-utf16-minLength"]!.GetValue<int>());
    }

    /// <summary>A declaration without an accessible generated getter cannot silently skip its rules.</summary>
    [Fact]
    public void ShouldRejectConfigurationGivenValidationWithoutValueAccessor()
    {
        // Arrange
        var registry = new PortiaContractMetadataRegistry([type => new PortiaContractMetadata(type)
        {
            HasValidation = true,
            Members = [new PortiaContractMemberMetadata("Value", typeof(string)) { HasValidation = true, Rules = [new RequiredAttribute()] }]
        }]);

        // Act
        var exception = Record.Exception(() => registry.Type(typeof(object)));

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("Value", failure.Message, StringComparison.Ordinal);
        Assert.Contains("accessor", failure.Message, StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>An omission rejected by a value rule is required and has no advertised invalid default.</summary>
    [Fact]
    public void ShouldRequirePresenceGivenDefaultRejectedByRange()
    {
        // Arrange
        var member = new PortiaContractMemberMetadata("Limit", typeof(int))
        {
            HasBindingDefault = true,
            BindingDefaultValue = 0,
            Rules = [new RangeAttribute(1, 10)]
        };
        var schema = new JsonObject { ["type"] = "integer" };

        // Act
        PortiaJsonConstraints.Apply(schema, member, JsonSerializerOptions.Web);
        var required = PortiaJsonConstraints.RequiresPresence(member);

        // Assert
        Assert.True(required);
        Assert.Null(schema["default"]);
    }

}
