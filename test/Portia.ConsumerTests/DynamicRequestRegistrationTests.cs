namespace Cntryl.Portia.Consumer;

/// <summary>
///     Covers <c>RegisterDynamicRequest&lt;T&gt;()</c>, the escape hatch for a request whose concrete
///     type never appears at a dispatch call site the generator can infer from.
/// </summary>
public sealed class DynamicRequestRegistrationTests
{
    /// <summary>
    ///     Verifies the generated interceptor actually adds the request's transport descriptor to the
    ///     builder. Constructing the descriptor without registering it leaves the request absent from
    ///     the transport catalog, so its declared route and discriminator are unreachable — and the
    ///     omission is invisible whenever a handler or an inferred dispatch happens to register the
    ///     same request by another route.
    /// </summary>
    [Fact]
    public void ShouldAddTheTransportDescriptorForADynamicallyRegisteredRequest()
    {
        const string source = """
                              using Cntryl.Portia;
                              using Microsoft.Extensions.DependencyInjection;

                              [RequestRoute("dynamic", "area", "resource", "run")]
                              [Discriminator("dynamic.only.request", 1)]
                              public sealed record DynamicOnly : IRequest, ICallable;

                              public static class Composition
                              {
                                  public static PortiaBuilder Compose(PortiaBuilder builder) =>
                                      builder.RegisterDynamicRequest<DynamicOnly>();
                              }
                              """;

        var generated = GeneratorCompilation.GeneratedSource(source, new RegistrationCallInterceptorGenerator());

        Assert.Contains("DynamicOnly", generated, StringComparison.Ordinal);
        Assert.Contains("builder.AddGeneratedRequest(", generated, StringComparison.Ordinal);
    }
}
