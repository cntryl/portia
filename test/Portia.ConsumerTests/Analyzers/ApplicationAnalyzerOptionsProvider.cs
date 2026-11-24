using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cntryl.Portia.Consumer;

sealed class ApplicationAnalyzerOptionsProvider(
    ImmutableDictionary<string, string> values,
    ImmutableDictionary<string, string>? fileValues = null)
    : AnalyzerConfigOptionsProvider
{
    public override AnalyzerConfigOptions GlobalOptions { get; } = new ApplicationAnalyzerOptions(values);
    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) =>
        fileValues is null ? GlobalOptions : new ApplicationAnalyzerOptions(fileValues);
    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;
}
