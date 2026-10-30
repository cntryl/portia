using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cntryl.Portia.Consumer;

sealed class ApplicationAnalyzerOptions(ImmutableDictionary<string, string> values) : AnalyzerConfigOptions
{
    public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
}
