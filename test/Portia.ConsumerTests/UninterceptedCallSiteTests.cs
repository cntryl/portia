namespace Cntryl.Portia.Consumer;

/// <summary>
///     Verifies the failure a consumer actually meets first when the source generator cannot see a call
///     site — a generic helper that forwards the type argument, or a build with the analyzer assets
///     switched off. Every <c>MapPortia*</c> and typed registration method is a body that only runs when
///     interception did not happen, so the message has to name the cause instead of silently registering
///     nothing and failing much later with an empty pipeline.
/// </summary>
public sealed class UninterceptedCallSiteTests
{
    [Theory]
    [InlineData("MapPortiaGet<Binding>(app, \"/binding\")")]
    [InlineData("MapPortiaPost<Binding>(app, \"/binding\")")]
    [InlineData("MapPortiaPut<Binding>(app, \"/binding\")")]
    [InlineData("MapPortiaPatch<Binding>(app, \"/binding\")")]
    [InlineData("MapPortiaDelete<Binding>(app, \"/binding\")")]
    public void UninterceptedEndpointMappingExplainsWhyItWasNotGenerated(string call)
    {
        var error = RunGeneric($$"""
                                 using Cntryl.Portia;
                                 using Cntryl.Portia.Testing;
                                 using Microsoft.AspNetCore.Builder;
                                 using Microsoft.AspNetCore.Routing;
                                 public sealed record Binding : IRequest, ICallable;
                                 public static class Scenario
                                 {
                                     // A generic forwarder: there is no concrete request type at the call site the
                                     // generator sees, so it cannot emit a binding for it.
                                     static void Map<TRequest>(IEndpointRouteBuilder app, string pattern)
                                         where TRequest : IRequest, ICallable
                                         => PortiaEndpointRouteBuilderExtensions.MapPortiaPost<TRequest>(app, pattern);
                                     public static void Run()
                                     {
                                         var app = WebApplication.CreateBuilder().Build();
                                         _ = PortiaEndpointRouteBuilderExtensions.{{call}};
                                     }
                                 }
                                 """);

        Assert.Contains("did not intercept this endpoint mapping", error.Message, StringComparison.Ordinal);
        Assert.Contains("analyzer assets", error.Message, StringComparison.Ordinal);
        Assert.Contains("generic method", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UninterceptedStreamingEndpointMappingExplainsWhyItWasNotGenerated()
    {
        var error = RunGeneric("""
                               using Cntryl.Portia;
                               using Cntryl.Portia.Testing;
                               using Microsoft.AspNetCore.Builder;
                               using Microsoft.AspNetCore.Routing;
                               public sealed record Feed : IStreamRequest<string>, ICallable;
                               public static class Scenario
                               {
                                   public static void Run()
                                   {
                                       var app = WebApplication.CreateBuilder().Build();
                                       _ = PortiaEndpointRouteBuilderExtensions.MapPortiaGetSse<Feed, string>(app, "/feed");
                                   }
                               }
                               """);

        Assert.Contains("did not intercept this endpoint mapping", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("app", null)]
    [InlineData(null, "")]
    [InlineData(null, "   ")]
    public void UninterceptedEndpointMappingChecksItsArgumentsBeforeReportingTheCause(string? app, string? pattern)
    {
        var route = app is null
            ? "WebApplication.CreateBuilder().Build()"
            : "null!";
        var patternText = pattern is null ? "\"/binding\"" : "\"" + pattern + "\"";
        var error = RunGeneric($$"""
                                 using Cntryl.Portia;
                                 using Cntryl.Portia.Testing;
                                 using Microsoft.AspNetCore.Builder;
                                 public sealed record Binding : IRequest, ICallable;
                                 public static class Scenario
                                 {
                                     public static void Run() =>
                                         _ = PortiaEndpointRouteBuilderExtensions.MapPortiaGet<Binding>({{route}}, {{patternText}});
                                 }
                                 """);

        _ = app is null
            ? Assert.IsType<ArgumentException>(error, exactMatch: false)
            : Assert.IsType<ArgumentNullException>(error);
    }

    [Theory]
    [InlineData("AddRequestHandler<Handler>()", "request handler")]
    [InlineData("AddRequestAuthorizer<Authorizer>()", "request authorizer at stage 'ResourceAccess'")]
    [InlineData("AddRequestPipelineBehavior<Behavior>(3)", "request pipeline behavior at order '3'")]
    [InlineData("RegisterDynamicRequest<Command>()", "request")]
    [InlineData("AddEvent<Happened>()", "domain event")]
    public void UninterceptedRegistrationNamesTheTypeAndTheRoleItWasRegisteringAs(string call, string role)
    {
        var error = RunGeneric($$"""
                                 using System.Threading;
                                 using System.Threading.Tasks;
                                 using Cntryl.Portia;
                                 using Cntryl.Portia.Testing;
                                 using Microsoft.Extensions.DependencyInjection;
                                 public sealed record Command : IRequest;
                                 public sealed record Happened : DomainEvent;
                                 public sealed class Handler : IRequestHandler<Command>
                                 {
                                     public ValueTask<Result> HandleAsync(IRequestContext<Command> c, CancellationToken ct)
                                         => ValueTask.FromResult(Result.Success);
                                 }
                                 public sealed class Authorizer : IRequestAuthorizer<Command>
                                 {
                                     public ValueTask<Result> AuthorizeAsync(IRequestContext<Command> c, CancellationToken ct)
                                         => ValueTask.FromResult(Result.Success);
                                 }
                                 public sealed class Behavior : IRequestPipelineBehavior<Command>
                                 {
                                     public ValueTask<Result> HandleAsync(IRequestContext<Command> c,
                                         RequestPipelineNext next, CancellationToken ct) => next(ct);
                                 }
                                 public static class Scenario
                                 {
                                     // Reflection reaches the uninterceptable body directly: the generator rewrites a
                                     // direct call site, so this is the only way to reach what a consumer hits when
                                     // interception did not happen at all.
                                     public static void Run()
                                     {
                                         var builder = new ServiceCollection().AddPortia();
                                         _ = builder.{{call}};
                                     }
                                 }
                                 """);

        Assert.Contains("did not intercept registration", error.Message, StringComparison.Ordinal);
        Assert.Contains(role, error.Message, StringComparison.Ordinal);
        Assert.Contains("analyzer assets", error.Message, StringComparison.Ordinal);
    }

    // Compiles the scenario without Portia's generators, which is exactly the shape of a build whose
    // analyzer assets never ran, then surfaces the exception the generated-free call throws.
    static Exception RunGeneric(string source)
    {
        var assembly = GeneratorCompilation.Compile(source);
        var run = assembly.GetType("Scenario")!.GetMethod("Run")!;
        var error = Record.Exception(() => run.Invoke(null, null));
        return Assert.IsType<System.Reflection.TargetInvocationException>(error).InnerException!;
    }
}
