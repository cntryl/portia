using System.Collections.Immutable;
using System.Globalization;
using Cntryl.Portia;
using Microsoft.CodeAnalysis;

namespace Cntryl.Portia.Consumer;

/// <summary>Checks analyzer warnings are limited to registered aggregate event handlers.</summary>
public sealed class AggregateOnHandlerAnalyzerTests
{
    const string Prefix = "using Cntryl.Portia; namespace Shop.Core; public sealed record Changed(int Amount) : DomainEvent; ";

    /// <summary>A direct throw in a registered inline handler points business decisions to the command.</summary>
    [Fact]
    public void ShouldWarnAboutThrowGivenRegisteredInlineHandler()
    {
        // Arrange
        const string source = Prefix + """
            public sealed class Order : Aggregate
            {
                public Order(Uuid id) : base(id, new EventStreamAddress("shop", "orders", id.ToString()))
                {
                    On<Changed>(e => { if (e.Amount < 0) throw new System.ArgumentOutOfRangeException(); Value = e.Amount; });
                }
                public int Value { get; private set; }
            }
            """;

        // Act
        var diagnostics = Analyze(source);

        // Assert
        var diagnostic = Assert.Single(diagnostics.Where(static item => item.Id == "PORTIA108"));
        Assert.Contains("command", diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A throw expression in an inline event handler is reported.</summary>
    [Fact]
    public void ShouldWarnAboutThrowExpressionGivenRegisteredInlineHandler()
    {
        // Arrange
        const string source = Prefix + """
            public sealed class Order : Aggregate
            {
                public Order(Uuid id) : base(id, new EventStreamAddress("shop", "orders", id.ToString()))
                {
                    On<Changed>(_ => throw new System.InvalidOperationException());
                }
            }
            """;

        // Act
        var diagnostics = Analyze(source);

        // Assert
        Assert.Single(diagnostics, static item => item.Id == "PORTIA108");
    }

    /// <summary>A direct anonymous-method handler is analyzed as registered event application.</summary>
    [Fact]
    public void ShouldWarnAboutAnonymousMethodGivenRegisteredHandler()
    {
        // Arrange
        const string source = Prefix + """
            public sealed class Order : Aggregate
            {
                public Order(Uuid id) : base(id, new EventStreamAddress("shop", "orders", id.ToString()))
                {
                    On<Changed>(delegate(Changed _) { throw new System.InvalidOperationException(); });
                }
            }
            """;

        // Act
        var diagnostics = Analyze(source);

        // Assert
        Assert.Single(diagnostics, static item => item.Id == "PORTIA108");
    }

    /// <summary>A named method-group handler is analyzed when that method returns a Portia Result.</summary>
    [Fact]
    public void ShouldWarnAboutResultCallGivenRegisteredMethodGroup()
    {
        // Arrange
        const string source = Prefix + """
            public sealed class Order : Aggregate
            {
                public Order(Uuid id) : base(id, new EventStreamAddress("shop", "orders", id.ToString()))
                {
                    On<Changed>(Apply);
                }
                public int Value { get; private set; }
                void Apply(Changed e) { _ = Result.Failure(RequestError.Validation("invalid")); Value = e.Amount; }
            }
            """;

        // Act
        var diagnostics = Analyze(source);

        // Assert
        Assert.Single(diagnostics, static item => item.Id == "PORTIA108");
    }

    /// <summary>A broader event parameter remains a valid contravariant method-group handler.</summary>
    [Fact]
    public void ShouldWarnAboutContravariantMethodGroupGivenRegisteredHandler()
    {
        // Arrange
        const string source = Prefix + """
            public sealed class Order : Aggregate
            {
                public Order(Uuid id) : base(id, new EventStreamAddress("shop", "orders", id.ToString()))
                {
                    On<Changed>(Apply);
                }
                public int Value { get; private set; }
                void Apply(DomainEvent e) { _ = Result.Failure(RequestError.Validation("invalid")); }
            }
            """;

        // Act
        var diagnostics = Analyze(source);

        // Assert
        Assert.Single(diagnostics, static item => item.Id == "PORTIA108");
    }

    /// <summary>A constructed generic handler method group is matched to its declaration.</summary>
    [Fact]
    public void ShouldWarnAboutConstructedGenericMethodGroupGivenRegisteredHandler()
    {
        // Arrange
        const string source = Prefix + """
            public sealed class Order : Aggregate
            {
                public Order(Uuid id) : base(id, new EventStreamAddress("shop", "orders", id.ToString()))
                {
                    On<Changed>(Apply);
                }
                void Apply<TEvent>(TEvent e) where TEvent : DomainEvent
                {
                    _ = Result<int>.Failure(RequestError.Validation("invalid"));
                }
            }
            """;

        // Act
        var diagnostics = Analyze(source);

        // Assert
        Assert.Single(diagnostics, static item => item.Id == "PORTIA108");
    }

    /// <summary>A generic Result-returning invocation in a registered handler is also reported.</summary>
    [Fact]
    public void ShouldWarnAboutGenericResultCallGivenRegisteredInlineHandler()
    {
        // Arrange
        const string source = Prefix + """
            public sealed class Order : Aggregate
            {
                public Order(Uuid id) : base(id, new EventStreamAddress("shop", "orders", id.ToString()))
                {
                    On<Changed>(e => { _ = Result<int>.Failure(RequestError.Validation("invalid")); Value = e.Amount; });
                }
                public int Value { get; private set; }
            }
            """;

        // Act
        var diagnostics = Analyze(source);

        // Assert
        Assert.Single(diagnostics, static item => item.Id == "PORTIA108");
    }

    /// <summary>A handler method declared in a separate partial type file is analyzed with its own semantic model.</summary>
    [Fact]
    public void ShouldWarnAboutMethodGroupDeclaredInOtherFileGivenRegisteredHandler()
    {
        // Arrange
        var files = new (string Path, string Source)[]
        {
            ("Order.cs", Prefix + """
                public sealed partial class Order : Aggregate
                {
                    public Order(Uuid id) : base(id, new EventStreamAddress("shop", "orders", id.ToString()))
                    {
                        On<Changed>(Apply);
                    }
                    public int Value { get; private set; }
                }
                """),
            ("Order.Apply.cs", """
                using Cntryl.Portia;
                namespace Shop.Core;
                public sealed partial class Order
                {
                    void Apply(Changed e) { _ = Result<int>.Failure(RequestError.Validation("invalid")); Value = e.Amount; }
                }
                """),
        };

        // Act
        var diagnostics = Analyze(files);

        // Assert
        Assert.Single(diagnostics, static item => item.Id == "PORTIA108");
    }

    /// <summary>Conditional projections and unregistered or nested throw code are left unreported.</summary>
    [Fact]
    public void ShouldNotWarnAboutPureProjectionOrUnregisteredCodeGivenAggregate()
    {
        // Arrange
        const string source = Prefix + """
            public sealed class Order : Aggregate
            {
                public Order(Uuid id) : base(id, new EventStreamAddress("shop", "orders", id.ToString()))
                {
                    On<Changed>(e => { System.Action nested = () => throw new System.Exception(); Value = e.Amount < 0 ? 0 : e.Amount; });
                    new Other().On<Changed>(_ => throw new System.Exception());
                }
                public int Value { get; private set; }
                void Unregistered(Changed e) { throw new System.Exception(); }
                sealed class Other { public void On<T>(System.Action<T> handler) { } }
            }
            """;

        // Act
        var diagnostics = Analyze(source);

        // Assert
        Assert.DoesNotContain(diagnostics, static item => item.Id == "PORTIA108");
    }

    static ImmutableArray<Diagnostic> Analyze(string source) => ApplicationAnalyzerCompilation.Analyze(
        "Shop.Core", new AggregateOnHandlerAnalyzer(), [("Order.cs", source)], rootNamespace: "Shop.Core");

    static ImmutableArray<Diagnostic> Analyze((string Path, string Source)[] files) =>
        ApplicationAnalyzerCompilation.Analyze("Shop.Core", new AggregateOnHandlerAnalyzer(), files,
            rootNamespace: "Shop.Core");
}
