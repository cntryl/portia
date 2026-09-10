namespace Cntryl.Portia.Consumer;

public sealed class WebApplicationOpenApiGeneratorTests
{
    [Fact]
    public void ShouldInterceptBuildGivenPortiaAspNetCoreWhenApplicationIsBuilt()
    {
        var generated = GeneratorCompilation.GeneratedSource("""
                                                             using Cntryl.Portia;
                                                             using Cntryl.Portia.Testing;
                                                             using Microsoft.AspNetCore.Builder;
                                                             public static class Scenario
                                                             {
                                                                 public static WebApplication Build()
                                                                 {
                                                                     var builder = WebApplication.CreateBuilder();
                                                                     return builder.Build();
                                                                 }
                                                             }
                                                             """, new WebApplicationOpenApiGenerator());

        Assert.Contains("PortiaOpenApi.Build(builder)", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldInterceptCreateGivenPortiaAspNetCoreWhenApplicationIsCreated()
    {
        var generated = GeneratorCompilation.GeneratedSource("""
                                                             using Cntryl.Portia;
                                                             using Cntryl.Portia.Testing;
                                                             using Microsoft.AspNetCore.Builder;
                                                             public static class Scenario
                                                             {
                                                                 public static WebApplication Create(string[] args) => WebApplication.Create(args);
                                                             }
                                                             """, new WebApplicationOpenApiGenerator());

        Assert.Contains("PortiaOpenApi.Create(args)", generated, StringComparison.Ordinal);
    }
}
