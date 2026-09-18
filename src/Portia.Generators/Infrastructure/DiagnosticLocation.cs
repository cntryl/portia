using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Cntryl.Portia;

readonly record struct DiagnosticLocation(
    string Path,
    int SpanStart,
    int SpanLength,
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter)
{
    public static DiagnosticLocation From(Location? location)
    {
        if (location is null || !location.IsInSource)
            return default;
        var span = location.SourceSpan;
        var lines = location.GetLineSpan().Span;
        return new DiagnosticLocation(location.SourceTree?.FilePath ?? string.Empty, span.Start, span.Length,
            lines.Start.Line, lines.Start.Character, lines.End.Line, lines.End.Character);
    }

    public Location ToLocation() => SpanLength == 0 && SpanStart == 0 && string.IsNullOrEmpty(Path)
        ? Location.None
        : Location.Create(Path, new TextSpan(SpanStart, SpanLength),
            new LinePositionSpan(new LinePosition(StartLine, StartCharacter), new LinePosition(EndLine, EndCharacter)));
}
