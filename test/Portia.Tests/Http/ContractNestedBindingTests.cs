using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

namespace Cntryl.Portia;

/// <summary>HTTP nested binding follows the same non-null constructor contract advertised to clients.</summary>
public sealed class ContractNestedBindingTests
{
    /// <summary>Missing or null nested constructor members are rejected before the handler.</summary>
    [Theory]
    [InlineData("{\"payload\":{}}")]
    [InlineData("{\"payload\":{\"name\":null}}")]
    public async Task ShouldRejectNestedInputGivenMissingNonNullableConstructorValue(string json)
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddHttp().AddRequestHandler<ContractNestedBindingHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaPost<ContractNestedBindingRequest, string>("/nested-binding");
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        using var client = app.GetTestClient();

        // Act
        using var response = await client.PostAsync("/nested-binding", body, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

sealed record ContractNestedBindingModel(string Name);

sealed record ContractNestedBindingRequest(ContractNestedBindingModel Payload) : IRequest<string>, ICallable;

sealed class ContractNestedBindingHandler : IRequestHandler<ContractNestedBindingRequest, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<ContractNestedBindingRequest> context,
        CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success(context.Request.Payload.Name));
}
