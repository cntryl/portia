using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>Transport-neutral validation of standard request attributes.</summary>
public sealed class ContractAttributeValidationTests
{
    /// <summary>Invalid values are rejected before the handler, even without a JSON serializer root.</summary>
    [Theory]
    [InlineData(null, 5)]
    [InlineData("", 5)]
    [InlineData("   ", 5)]
    [InlineData("name", 0)]
    [InlineData("name", 101)]
    public async Task ShouldRejectInvalidLocalRequestGivenStandardAttributes(string? name, int limit)
    {
        // Arrange
        var probe = new ContractValidationProbe();
        var services = new ServiceCollection();
        _ = services.AddSingleton(probe);
        _ = services.AddPortia().AddRequestHandler<ContractLocalValidatedHandler>();
        await using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IRequestBus>();

        // Act
        var result = await bus.SendAsync(new ContractLocalValidatedRequest(name, limit), RequestActor.System);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Validation, result.Error!.Kind);
        Assert.False(result.Error.IsTransient);
        Assert.Equal(0, probe.Invocations);
    }

    /// <summary>Valid request values retain ordinary handler behavior.</summary>
    [Fact]
    public async Task ShouldInvokeHandlerGivenValidAnnotatedLocalRequest()
    {
        // Arrange
        var probe = new ContractValidationProbe();
        var services = new ServiceCollection();
        _ = services.AddSingleton(probe);
        _ = services.AddPortia().AddRequestHandler<ContractLocalValidatedHandler>();
        await using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IRequestBus>();

        // Act
        var result = await bus.SendAsync(new ContractLocalValidatedRequest("name", 5), RequestActor.System);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("name", result.Value);
        Assert.Equal(1, probe.Invocations);
    }
    /// <summary>A purely local handler does not need a serializer root at host startup.</summary>
    [Fact]
    public async Task ShouldStartHostGivenLocalContractWithoutJsonRoot()
    {
        // Arrange
        var builder = Host.CreateApplicationBuilder();
        _ = builder.Services.AddSingleton(new ContractValidationProbe());
        _ = builder.Services.AddPortia().AddRequestHandler<ContractLocalValidatedHandler>();
        using var host = builder.Build();

        // Act
        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        // Assert
        Assert.Null(exception);
    }

    /// <summary>Every supported value rule is generated and enforced for nested request data.</summary>
    [Theory]
    [InlineData("required")]
    [InlineData("string-min")]
    [InlineData("string-max")]
    [InlineData("range-min")]
    [InlineData("range-max")]
    [InlineData("items-min")]
    [InlineData("items-max")]
    [InlineData("regex")]
    [InlineData("email")]
    [InlineData("url")]
    public async Task ShouldRejectInvalidValueGivenEachSupportedRule(string rule)
    {
        // Arrange
        var model = new ContractMetadataModel("Valid", 5, ["one"], "AB", "hi@example.com", "https://example.com");
        model = rule switch
        {
            "required" => model with { Name = null },
            "string-min" => model with { Name = "a" },
            "string-max" => model with { Name = new string('a', 13) },
            "range-min" => model with { Limit = 1 },
            "range-max" => model with { Limit = 11 },
            "items-min" => model with { Items = [] },
            "items-max" => model with { Items = ["a", "b", "c", "d"] },
            "regex" => model with { Code = "ABextra" },
            "email" => model with { Email = "invalid" },
            "url" => model with { Website = "invalid" },
            _ => throw new ArgumentOutOfRangeException(nameof(rule))
        };
        var services = new ServiceCollection();
        _ = services.AddPortia().AddRequestHandler<ContractMetadataInputHandler>();
        await using var provider = services.BuildServiceProvider();

        // Act
        var result = await provider.GetRequiredService<IRequestBus>().SendAsync(new ContractMetadataInputRequest(model), RequestActor.System);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Validation, result.Error!.Kind);
        Assert.False(result.Error.IsTransient);
    }

    /// <summary>Authorization denial precedes even evaluating validation getters.</summary>
    [Fact]
    public async Task ShouldSkipValidationGivenAuthorizationDenial()
    {
        // Arrange
        var services = new ServiceCollection();
        _ = services.AddPortia().AddRequestHandler<ContractDeniedHandler>().AddRequestAuthorizer<ContractDeniedAuthorizer>();
        await using var provider = services.BuildServiceProvider();

        // Act
        var result = await provider.GetRequiredService<IRequestBus>().SendAsync(new ContractDeniedRequest(), RequestActor.Anonymous);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Forbidden, result.Error!.Kind);
    }

    /// <summary>Traversal stops consuming work when the validation graph budget is exhausted.</summary>
    [Fact]
    public async Task ShouldStopTraversalGivenExcessiveCollection()
    {
        // Arrange
        var consumed = 0;
        IEnumerable<ContractValidatedItem> Items()
        {
            for (var index = 0; index < 100_010; index++)
            {
                consumed++;
                yield return new ContractValidatedItem("valid");
            }
        }
        var services = new ServiceCollection();
        _ = services.AddPortia().AddRequestHandler<ContractEnumerableValidatedHandler>();
        await using var provider = services.BuildServiceProvider();

        // Act
        var result = await provider.GetRequiredService<IRequestBus>().SendAsync(new ContractEnumerableValidatedRequest(Items()), RequestActor.System);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Validation, result.Error!.Kind);
        Assert.InRange(consumed, 1, 100_001);
    }

    /// <summary>Interface-typed members are validated through their declared generated contract.</summary>
    [Fact]
    public async Task ShouldRejectNestedValuesGivenInterfaceTypedCollection()
    {
        // Arrange
        var services = new ServiceCollection();
        _ = services.AddPortia().AddRequestHandler<ContractInterfaceValidatedHandler>();
        await using var provider = services.BuildServiceProvider();
        var request = new ContractInterfaceValidatedRequest(new List<ContractValidatedItem> { new("") });

        // Act
        var result = await provider.GetRequiredService<IRequestBus>().SendAsync(request, RequestActor.System);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Validation, result.Error!.Kind);
    }

    /// <summary>Nested collection and dictionary values receive the same validation as root values.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRejectNestedValuesGivenGeneratedCollectionAccess(bool dictionary)
    {
        // Arrange
        var services = new ServiceCollection();
        _ = services.AddPortia().AddRequestHandler<ContractNestedValidatedHandler>();
        await using var provider = services.BuildServiceProvider();
        var request = dictionary
            ? new ContractNestedValidatedRequest([], new Dictionary<string, ContractValidatedItem> { ["first"] = new("") })
            : new ContractNestedValidatedRequest([new("")], []);

        // Act
        var result = await provider.GetRequiredService<IRequestBus>().SendAsync(request, RequestActor.System);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Validation, result.Error!.Kind);
        Assert.Contains("Name", result.Error.Message, StringComparison.Ordinal);
    }

}

sealed record ContractLocalValidatedRequest([property: Required] string? Name, [property: Range(1, 100)] int Limit) : IRequest<string>;

sealed class ContractValidationProbe
{
    public int Invocations { get; set; }
}

sealed class ContractLocalValidatedHandler(ContractValidationProbe probe) : IRequestHandler<ContractLocalValidatedRequest, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<ContractLocalValidatedRequest> context, CancellationToken ct)
    {
        probe.Invocations++;
        return ValueTask.FromResult(Result<string>.Success(context.Request.Name!));
    }
}

sealed record ContractValidatedItem([property: Required] string Name);

sealed record ContractNestedValidatedRequest(ContractValidatedItem[] Items, Dictionary<string, ContractValidatedItem> Lookup) : IRequest;

sealed class ContractNestedValidatedHandler : IRequestHandler<ContractNestedValidatedRequest>
{
    public ValueTask<Result> HandleAsync(IRequestContext<ContractNestedValidatedRequest> context, CancellationToken ct) => ValueTask.FromResult(Result.Success);
}

sealed record ContractInterfaceValidatedRequest(IReadOnlyList<ContractValidatedItem> Items) : IRequest;

sealed class ContractInterfaceValidatedHandler : IRequestHandler<ContractInterfaceValidatedRequest>
{
    public ValueTask<Result> HandleAsync(IRequestContext<ContractInterfaceValidatedRequest> context, CancellationToken ct) => ValueTask.FromResult(Result.Success);
}

sealed record ContractDeniedRequest : IRequest
{
    [Required]
    public string Name => throw new InvalidOperationException(GetType().Name + ": validation must not read a denied request.");
}

sealed class ContractDeniedHandler : IRequestHandler<ContractDeniedRequest>
{
    public ValueTask<Result> HandleAsync(IRequestContext<ContractDeniedRequest> context, CancellationToken ct) => throw new InvalidOperationException("Handler must not run.");
}

sealed class ContractDeniedAuthorizer : IRequestAuthorizer<ContractDeniedRequest>
{
    public ValueTask<Result> AuthorizeAsync(IRequestContext<ContractDeniedRequest> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Failure(new RequestError(RequestErrorKind.Forbidden, "Denied.")));
}

sealed record ContractEnumerableValidatedRequest(IEnumerable<ContractValidatedItem> Items) : IRequest;

sealed class ContractEnumerableValidatedHandler : IRequestHandler<ContractEnumerableValidatedRequest>
{
    public ValueTask<Result> HandleAsync(IRequestContext<ContractEnumerableValidatedRequest> context, CancellationToken ct) => ValueTask.FromResult(Result.Success);
}
