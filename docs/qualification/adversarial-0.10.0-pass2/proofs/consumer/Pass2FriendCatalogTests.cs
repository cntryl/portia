using Microsoft.CodeAnalysis;

namespace Cntryl.Portia.Consumer;

/// <summary>Explicit generated event catalog calls must remain usable in a friend consumer assembly.</summary>
public sealed class Pass2FriendCatalogTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldResolveLocalGeneratedCatalogGivenFriendAssembly(bool friend)
    {
        var declaration = friend ? "[assembly: InternalsVisibleTo(\"OutputDiagnostics\")]" : string.Empty;
        var reference = GeneratorCompilation.ReferenceNamed("Pass2.Catalog.Source" + friend, $$"""
            using System.Runtime.CompilerServices;
            using Cntryl.Portia;
            {{declaration}}
            namespace Shared;
            [Discriminator("pass2.shared.changed", 1)] public sealed record SharedChanged : DomainEvent;
            public static class Composition
            {
                public static DomainEventTypeCatalog Catalog(DomainEventTypeCatalog catalog)
                    => catalog.AddPortiaGeneratedDomainEvents();
            }
            """, new DomainEventCatalogGenerator());
        const string source = """
            using Cntryl.Portia;
            namespace Consumer;
            [Discriminator("pass2.consumer.changed", 1)] public sealed record ConsumerChanged : DomainEvent;
            public static class Composition
            {
                public static DomainEventTypeCatalog Catalog(DomainEventTypeCatalog catalog)
                    => catalog.AddPortiaGeneratedDomainEvents();
            }
            """;

        var diagnostics = GeneratorCompilation.OutputDiagnostics(source, [reference], new DomainEventCatalogGenerator());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }
}
