using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public sealed class GeneratedRegistrationTests
{
    [Fact]
    public void ShouldCreateOneContextGivenRepeatedGeneratedApplicationEntry()
    {
        // Arrange
        var assembly = GeneratorCompilation.Compile("""
            using System;
            using System.Text.Json;
            using System.Text.Json.Serialization;
            using System.Text.Json.Serialization.Metadata;
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            [PortiaJsonContext]
            public partial class CountingContext : JsonSerializerContext
            {
                public static int Instances;
                public CountingContext(JsonSerializerOptions options) : base(options) { Instances++; }
                protected override JsonSerializerOptions GeneratedSerializerOptions => new();
                public override JsonTypeInfo GetTypeInfo(Type type) => null;
            }
            public static class Composition
            {
                public static PortiaBuilder Compose(IServiceCollection services) => services.AddPortia();
            }
            """, new RegistrationCallInterceptorGenerator());
        var services = new ServiceCollection();
        var compose = assembly.GetType("Composition")!.GetMethod("Compose")!;

        // Act
        _ = compose.Invoke(null, [services]);
        _ = compose.Invoke(null, [services]);
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredKeyedService<System.Text.Json.JsonSerializerOptions>(PortiaServiceKeys.Json);

        // Assert
        Assert.Equal(1, assembly.GetType("CountingContext")!.GetField("Instances")!.GetValue(null));
    }

    [Fact]
    public void ShouldApplyEachBlockOnceGivenRepeatedCallsAndSeparateBuilders()
    {
        // Arrange
        var first = new ServiceCollection().AddPortia();
        var second = new ServiceCollection().AddPortia();
        var key = new object();
        var calls = 0;
        void Register(PortiaBuilder _) => calls++;

        // Act
        _ = first.AddGeneratedRegistrations(key, Register).AddGeneratedRegistrations(key, Register);
        _ = second.AddGeneratedRegistrations(key, Register).AddGeneratedRegistrations(key, Register);

        // Assert
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ShouldRetryBlockGivenRegistrationFailure()
    {
        // Arrange
        var builder = new ServiceCollection().AddPortia();
        var key = new object();
        var attempts = 0;
        void Register(PortiaBuilder _)
        {
            if (++attempts == 1)
                throw new InvalidOperationException("interrupted registration");
        }

        // Act
        var error = Record.Exception(() => builder.AddGeneratedRegistrations(key, Register));
        _ = builder.AddGeneratedRegistrations(key, Register).AddGeneratedRegistrations(key, Register);

        // Assert
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void ShouldKeepBlocksDistinctGivenValueEqualIdentityObjects()
    {
        // Arrange
        var builder = new ServiceCollection().AddPortia();
        object first = new string('x', 1);
        object second = new string('x', 1);
        var calls = 0;
        void Register(PortiaBuilder _) => calls++;

        // Act
        _ = builder.AddGeneratedRegistrations(first, Register).AddGeneratedRegistrations(second, Register);

        // Assert
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ShouldAllocateNothingGivenCompletedRegistrationBlockWhenRepeated()
    {
        // Arrange
        var builder = new ServiceCollection().AddPortia();
        var key = new object();
        static void Repeat(PortiaBuilder target, object identity, int count)
        {
            for (var index = 0; index < count; index++)
                _ = target.AddGeneratedRegistrations(identity, static _ => { });
        }
        Repeat(builder, key, 100);

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        Repeat(builder, key, 10_000);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.Equal(0, allocated);
    }
}
