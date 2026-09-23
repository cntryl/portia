using System.Text;

namespace Cntryl.Portia;

// The BCL doesn't ship InterceptsLocationAttribute on every target yet; the compiler recognizes it
// structurally by name, so a self-declared, file-local copy works exactly like the real one.
static class InterceptsLocationPolyfill
{
    static readonly string[] IndentedLines =
    {
        "namespace System.Runtime.CompilerServices",
        "{",
        "    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]",
        "    file sealed class InterceptsLocationAttribute : global::System.Attribute",
        "    {",
        "        public InterceptsLocationAttribute(int version, string data)",
        "        {",
        "            _ = version;",
        "            _ = data;",
        "        }",
        "    }",
        "}"
    };

    /// <summary>Appends the attribute declaration, one source line per line or collapsed onto one.</summary>
    public static StringBuilder AppendTo(StringBuilder source, bool indented)
    {
        if (!indented)
        {
            return source.AppendLine(
                "namespace System.Runtime.CompilerServices { [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)] file sealed class InterceptsLocationAttribute : global::System.Attribute { public InterceptsLocationAttribute(int version, string data) { _ = version; _ = data; } } }");
        }

        foreach (var line in IndentedLines)
            _ = source.AppendLine(line);
        return source;
    }
}
