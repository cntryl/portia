#!/usr/bin/env bash
set -euo pipefail
portia_root=$(cd "$(dirname "$0")/.." && pwd)
portia_output=${1:?Usage: verify-namespace-fix.sh EVIDENCE_DIRECTORY [FILES=64]}
portia_count=${2:-64}
[[ "$portia_count" =~ ^[1-9][0-9]*$ ]]
mkdir -p "$portia_output"
portia_output=$(cd "$portia_output" && pwd)
test ! -e "$portia_output/NamespaceSmoke.Common.csproj"
mkdir -p "$portia_output/Features/Orders/Models"
cat > "$portia_output/NamespaceSmoke.Common.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>NamespaceSmoke.Common</RootNamespace>
    <PortiaProjectStructure>true</PortiaProjectStructure>
  </PropertyGroup>
  <Import Project="$portia_root/src/Portia.Analyzers/buildTransitive/Cntryl.Portia.Analyzers.props"/>
  <ItemGroup>
    <Analyzer Include="$portia_root/src/Portia.Generators/bin/Release/netstandard2.0/Portia.Generators.dll"/>
    <Analyzer Include="$portia_root/src/Portia.CodeFixes/bin/Release/netstandard2.0/Portia.CodeFixes.dll"/>
    <Reference Include="Portia.Abstractions" HintPath="$portia_root/src/Portia.Abstractions/bin/Release/net10.0/Portia.Abstractions.dll"/>
  </ItemGroup>
</Project>
EOF
cat > "$portia_output/ContractsJsonContext.cs" <<'EOF'
using System.Text.Json.Serialization;
using Cntryl.Portia;
namespace NamespaceSmoke.Common;
[PortiaJsonContext]
EOF
for ((portia_index=0; portia_index<portia_count; portia_index++)); do
  if ((portia_index % 2 == 0)); then
    printf 'namespace Old { public sealed record Model%s(string Value); }\n' "$portia_index"
  else
    printf 'namespace Old; public sealed record Model%s(string Value);\n' "$portia_index"
  fi > "$portia_output/Features/Orders/Models/Model$portia_index.cs"
  printf '[JsonSerializable(typeof(Old.Model%s))]\n' "$portia_index" >> "$portia_output/ContractsJsonContext.cs"
done
printf 'public partial class ContractsJsonContext : JsonSerializerContext;\n' >> "$portia_output/ContractsJsonContext.cs"
dotnet restore "$portia_output/NamespaceSmoke.Common.csproj" > "$portia_output/restore.log" 2>&1
dotnet format analyzers "$portia_output/NamespaceSmoke.Common.csproj" --diagnostics PORTIA202 --no-restore \
  --verbosity diagnostic > "$portia_output/format.log" 2>&1
if rg -q 'Failed to apply code fix|Unable to fix' "$portia_output/format.log"; then
  cat "$portia_output/format.log"
  exit 1
fi
if rg -q '^namespace Old' "$portia_output/Features"; then
  echo 'The namespace code fix left an original declaration unchanged.' >&2
  exit 1
fi
dotnet build "$portia_output/NamespaceSmoke.Common.csproj" --no-restore > "$portia_output/build.log" 2>&1
dotnet format analyzers "$portia_output/NamespaceSmoke.Common.csproj" --diagnostics PORTIA202 --no-restore \
  --verify-no-changes --verbosity diagnostic > "$portia_output/idempotence.log" 2>&1
if rg -q 'Failed to apply code fix|Unable to fix' "$portia_output/idempotence.log"; then
  cat "$portia_output/idempotence.log"
  exit 1
fi
echo "Qualified PORTIA202 Fix All for $portia_count files with generated JSON constructor references; build and idempotence passed."
