"""Compile independent consumers of freshly packed compiler assets, using an isolated cache."""

import argparse
import os
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True)
    parser.add_argument("--packages", type=Path, required=True)
    args = parser.parse_args()
    repository = Path(__file__).resolve().parents[1]
    with tempfile.TemporaryDirectory(prefix="portia-packed-compiler-") as temporary:
        root = Path(temporary)
        environment = dict(os.environ, NUGET_PACKAGES=str(root / "cache"))
        config = ET.parse(repository / "smoke/Portia.AspNetCore.NativeAotConsumer/NuGet.Config")
        config.find("./packageSources/add[@key='portia-local']").set("value", str(args.packages.resolve()))
        config.write(root / "NuGet.Config", encoding="utf-8", xml_declaration=True)

        def run(command, expected=None):
            result = subprocess.run(command, cwd=root, env=environment, text=True,
                                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=False)
            if expected:
                # A named diagnostic from application source is required, not an incidental failure.
                if result.returncode == 0 or not any("Probe.cs(" in line and f"error {expected}:" in line
                                                     for line in result.stdout.splitlines()):
                    raise RuntimeError(f"Expected source diagnostic {expected}:\n{result.stdout}")
                print(f"Packed compiler consumer: {expected} verified")
            elif result.returncode:
                raise RuntimeError(result.stdout)

        for general in (True, False):
            analyzer = (f'<PackageReference Include="Cntryl.Portia.Analyzers" Version="{args.version}" '
                        'IncludeAssets="analyzers;build;buildTransitive" />') if general else ""
            (root / "Probe.csproj").write_text(f"""<Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType>
                <Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Cntryl.Portia.AspNetCore" Version="{args.version}" />
                <PackageReference Include="Cntryl.Portia.Testing" Version="{args.version}" />
                {analyzer}
              </ItemGroup>
            </Project>""")
            run(["dotnet", "restore", "Probe.csproj", "--verbosity", "quiet"])
            prefix = """using System;
using Cntryl.Portia;
using Cntryl.Portia.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using System.Text.Json.Serialization;
[Discriminator("probe.command")] public sealed record Command : IRequest, ICallable;
[PortiaJsonContext, JsonSerializable(typeof(Command))]
internal partial class ProbeJsonContext : JsonSerializerContext;
"""
            fixtures = [
                (prefix + "public static class App { public static void Map(IEndpointRouteBuilder routes) => routes.MapPortiaGet<Command>(\"/probe\"); }", None),
                (prefix + "public static class App { public static void Map() { Func<IEndpointRouteBuilder, string, IEndpointConventionBuilder> map = PortiaEndpointRouteBuilderExtensions.MapPortiaGet<Command>; } }", "PORTIA016"),
            ]
            if general:
                for request, declaration in (("Command", ""), ("Query", "public sealed record Query : IRequest<string>;"),
                                             ("Feed", "public sealed record Feed : IStreamRequest<string>;")):
                    for discard in ("_ = value.ExpectDenied();", "Action forgotten = () => value.ExpectDenied();"):
                        fixtures.append((prefix + declaration + f"public static class App {{ public static async System.Threading.Tasks.Task Check(IServiceProvider services) {{ var value = RequestScenario.For(services).When(new {request}()); {discard} await value; }} }}", "PORTIA107"))
                fixtures.append((prefix + "[PortiaJsonContext] public class InvalidContext { }", "PORTIA030"))
            for source, diagnostic in fixtures:
                (root / "Probe.cs").write_text(source)
                run(["dotnet", "build", "Probe.csproj", "--configuration", "Release", "--no-restore", "--verbosity", "quiet"], diagnostic)
        project = ET.parse(root / "Probe.csproj")
        project.find("./PropertyGroup/OutputType").text = "Exe"
        project.find("./ItemGroup/PackageReference[@Include='Cntryl.Portia.AspNetCore']").set("ExcludeAssets", "analyzers;build;buildTransitive")
        # Remove the compiler item explicitly as well: package exclusion alone can leave SDK-resolved items.
        target = ET.SubElement(project.getroot(), "Target", Name="RemoveHttpTooling", BeforeTargets="CoreCompile")
        group = ET.SubElement(target, "ItemGroup")
        ET.SubElement(group, "Analyzer", Remove="@(Analyzer)", Condition="'%(Analyzer.Filename)' == 'Portia.AspNetCore.Generators'")
        fallback = root / "Fallback"
        fallback.mkdir()
        project.write(fallback / "Probe.csproj", encoding="utf-8", xml_declaration=True)
        (fallback / "Probe.cs").write_text("""using System;
using Cntryl.Portia;
using Microsoft.AspNetCore.Builder;
var builder = WebApplication.CreateSlimBuilder();
await using var app = builder.Build();
var rejected = false;
try { app.MapPortiaGet<Command>("/probe"); }
catch (InvalidOperationException error) when (error.Message.Contains("Cntryl.Portia.AspNetCore", StringComparison.Ordinal)
    && error.Message.Contains("analyzer assets", StringComparison.Ordinal)
    && !error.Message.Contains("Portia.DependencyInjection", StringComparison.Ordinal)) { rejected = true; }
if (!rejected) throw new InvalidOperationException("Packed fallback did not identify the HTTP generator owner.");
public sealed record Command : IRequest, ICallable;
""")
        run(["dotnet", "restore", "Fallback/Probe.csproj", "--verbosity", "quiet"])
        run(["dotnet", "run", "--project", "Fallback/Probe.csproj", "--configuration", "Release", "--no-restore", "--verbosity", "quiet"])
        print("Packed runtime fallback verified with HTTP analyzer assets excluded.")
        print("Packed compiler qualification passed with and without the independent general analyzer package.")


if __name__ == "__main__":
    main()
