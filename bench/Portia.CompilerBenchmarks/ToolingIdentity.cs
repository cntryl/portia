namespace Cntryl.Portia.CompilerBenchmarks;

sealed record ToolingIdentity(string HarnessHash, string Runtime, string Roslyn, string OS, string Architecture,
    Dictionary<string, string> Assemblies);
