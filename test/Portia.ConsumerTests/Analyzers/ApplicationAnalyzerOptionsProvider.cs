using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cntryl.Portia.Consumer;

sealed class ApplicationAnalyzerOptionsProvider(ImmutableDictionary<string, string> values) : AnalyzerConfigOptionsProvider
{
    public override AnalyzerConfigOptions GlobalOptions { get; } = new ApplicationAnalyzerOptions(values);
    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;
    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;
}
