#!/usr/bin/env bash
set -euo pipefail

portia_root=$(cd "$(dirname "$0")/.." && pwd)
portia_output=${1:?Usage: verify-structure-file-fixes.sh EVIDENCE_DIRECTORY}
mkdir -p "$portia_output"
portia_output=$(cd "$portia_output" && pwd)
test ! -e "$portia_output/globbed"
test ! -e "$portia_output/explicit"

write_fixture() {
  local portia_fixture=$1
  local portia_explicit=$2
  local portia_extra_properties=${3:-}
  mkdir -p "$portia_fixture/Features/Orders/Values"
  cat > "$portia_fixture/PortiaStructureSmoke.slnx" <<'EOF'
<Solution>
  <Project Path="Shop.Core.csproj" />
</Solution>
EOF
  cat > "$portia_fixture/Shop.Core.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <RootNamespace>Shop.Core</RootNamespace>
    <PortiaProjectStructure>true</PortiaProjectStructure>
    $portia_explicit
    $portia_extra_properties
  </PropertyGroup>
  <Import Project="$portia_root/src/Portia.Analyzers/buildTransitive/Cntryl.Portia.Analyzers.props" />
  <ItemGroup>
    <Analyzer Include="$portia_root/src/Portia.Generators/bin/Release/netstandard2.0/Portia.Generators.dll" />
    <Analyzer Include="$portia_root/src/Portia.CodeFixes/bin/Release/netstandard2.0/Portia.CodeFixes.dll" />
  </ItemGroup>
</Project>
EOF
  if [[ "$portia_explicit" == "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>" ]]; then
    sed -i.bak 's#</Project>#  <ItemGroup><Compile Include="Features/Orders/Values/Wrong.cs" /></ItemGroup>\n</Project>#' "$portia_fixture/Shop.Core.csproj"
    rm "$portia_fixture/Shop.Core.csproj.bak"
  fi
  cat > "$portia_fixture/Features/Orders/Values/Wrong.cs" <<'EOF'
namespace Shop.Core.Features.Orders.Values;
public sealed record Price(string Value);
public sealed record Currency(string Code);
EOF
}

portia_globbed="$portia_output/globbed"
portia_consumer="$portia_output/consumer"
write_fixture "$portia_globbed" ""
mkdir "$portia_consumer"
cat > "$portia_consumer/Consumer.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../globbed/Shop.Core.csproj" />
  </ItemGroup>
</Project>
EOF
cat > "$portia_consumer/CoreTypesConsumer.cs" <<'EOF'
using Shop.Core.Features.Orders.Values;

namespace Shop.Consumer;

public static class CoreTypesConsumer
{
    public static (Price Price, Currency Currency) Create() =>
        (new Price("12.50"), new Currency("USD"));
}
EOF
cat > "$portia_globbed/PortiaStructureSmoke.slnx" <<'EOF'
<Solution>
  <Project Path="Shop.Core.csproj" />
  <Project Path="../consumer/Consumer.csproj" />
</Solution>
EOF
dotnet restore "$portia_globbed/PortiaStructureSmoke.slnx" > "$portia_globbed/restore.log" 2>&1
dotnet run --project "$portia_root/eng/Portia.RepositoryTools/Portia.RepositoryTools.csproj" \
  --configuration Release --no-build -- fix-application-structure --solution "$portia_globbed/PortiaStructureSmoke.slnx" \
  > "$portia_globbed/fix.log" 2>&1
test ! -e "$portia_globbed/Features/Orders/Values/Wrong.cs"
test -f "$portia_globbed/Features/Orders/Values/Price.cs"
test -f "$portia_globbed/Features/Orders/Values/Currency.cs"
! grep -E -q '<Compile[[:space:]]+Include=' "$portia_globbed/Shop.Core.csproj"
dotnet run --project "$portia_root/eng/Portia.RepositoryTools/Portia.RepositoryTools.csproj" \
  --configuration Release --no-build -- fix-application-structure --solution "$portia_globbed/PortiaStructureSmoke.slnx" \
  > "$portia_globbed/idempotence.log" 2>&1
grep -F -q 'No PORTIA200 or PORTIA201 fixes are needed.' "$portia_globbed/idempotence.log"

portia_workspace_failure="$portia_output/workspace-failure"
write_fixture "$portia_workspace_failure" ""
dotnet restore "$portia_workspace_failure/PortiaStructureSmoke.slnx" > "$portia_workspace_failure/restore.log" 2>&1
mkdir "$portia_workspace_failure/Broken"
printf '<Project>\n' > "$portia_workspace_failure/Broken/Bad.csproj"
cat > "$portia_workspace_failure/PortiaStructureSmoke.slnx" <<'EOF'
<Solution>
  <Project Path="Shop.Core.csproj" />
  <Project Path="Broken/Bad.csproj" />
</Solution>
EOF
if dotnet run --project "$portia_root/eng/Portia.RepositoryTools/Portia.RepositoryTools.csproj" \
  --configuration Release --no-build -- fix-application-structure --solution "$portia_workspace_failure/PortiaStructureSmoke.slnx" \
  > "$portia_workspace_failure/fix.log" 2>&1; then
  echo 'The CLI accepted a partially loaded solution after a project load failure.' >&2
  exit 1
fi
test -f "$portia_workspace_failure/Features/Orders/Values/Wrong.cs"
test ! -e "$portia_workspace_failure/Features/Orders/Values/Price.cs"
test ! -e "$portia_workspace_failure/Features/Orders/Values/Currency.cs"
grep -F -q 'MSBuildWorkspace reported project load failures' "$portia_workspace_failure/fix.log"
grep -F -q 'Broken/Bad.csproj' "$portia_workspace_failure/fix.log"
grep -F -q 'project file could not be loaded' "$portia_workspace_failure/fix.log"
! find "$portia_workspace_failure" \( -name '.portia-stage-*' -o -name '.portia-backup-*' \) -print | grep . > /dev/null

portia_explicit="$portia_output/explicit"
write_fixture "$portia_explicit" "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>"
dotnet restore "$portia_explicit/PortiaStructureSmoke.slnx" > "$portia_explicit/restore.log" 2>&1
if dotnet run --project "$portia_root/eng/Portia.RepositoryTools/Portia.RepositoryTools.csproj" \
  --configuration Release --no-build -- fix-application-structure --solution "$portia_explicit/PortiaStructureSmoke.slnx" \
  > "$portia_explicit/fix.log" 2>&1; then
  echo 'The CLI accepted a split whose new file is not included by explicit Compile items.' >&2
  exit 1
fi
test -f "$portia_explicit/Features/Orders/Values/Wrong.cs"
test ! -e "$portia_explicit/Features/Orders/Values/Currency.cs"
grep -F -q 'public sealed record Currency' "$portia_explicit/Features/Orders/Values/Wrong.cs"
grep -F -q '<Compile Include="Features/Orders/Values/Wrong.cs"' "$portia_explicit/Shop.Core.csproj"
! find "$portia_explicit" \( -name '.portia-stage-*' -o -name '.portia-backup-*' \) -print | grep . > /dev/null

portia_explicit_glob="$portia_output/explicit-glob"
write_fixture "$portia_explicit_glob" ""
sed -i.bak 's#</Project>#  <ItemGroup><Compile Include="Features/Orders/Values/Currency.cs" /></ItemGroup>\n</Project>#' \
  "$portia_explicit_glob/Shop.Core.csproj"
rm "$portia_explicit_glob/Shop.Core.csproj.bak"
dotnet restore "$portia_explicit_glob/PortiaStructureSmoke.slnx" > "$portia_explicit_glob/restore.log" 2>&1
if dotnet run --project "$portia_root/eng/Portia.RepositoryTools/Portia.RepositoryTools.csproj" \
  --configuration Release --no-build -- fix-application-structure --solution "$portia_explicit_glob/PortiaStructureSmoke.slnx" \
  > "$portia_explicit_glob/fix.log" 2>&1; then
  echo 'The CLI accepted a destination already declared as an explicit Compile item under SDK default globs.' >&2
  exit 1
fi
test -f "$portia_explicit_glob/Features/Orders/Values/Wrong.cs"
test ! -e "$portia_explicit_glob/Features/Orders/Values/Currency.cs"

portia_collision="$portia_output/collision"
write_fixture "$portia_collision" ""
mkdir "$portia_collision/Features/Orders/Values/Currency.cs"
printf 'occupied\n' > "$portia_collision/Features/Orders/Values/Currency.cs/sentinel.txt"
dotnet restore "$portia_collision/PortiaStructureSmoke.slnx" > "$portia_collision/restore.log" 2>&1
if dotnet run --project "$portia_root/eng/Portia.RepositoryTools/Portia.RepositoryTools.csproj" \
  --configuration Release --no-build -- fix-application-structure --solution "$portia_collision/PortiaStructureSmoke.slnx" \
  > "$portia_collision/fix.log" 2>&1; then
  echo 'The CLI accepted a split whose destination is an existing directory.' >&2
  exit 1
fi
test -f "$portia_collision/Features/Orders/Values/Wrong.cs"
test ! -e "$portia_collision/Features/Orders/Values/Price.cs"
test -f "$portia_collision/Features/Orders/Values/Currency.cs/sentinel.txt"

portia_build_failure="$portia_output/build-failure"
write_fixture "$portia_build_failure" "" \
  '<GenerateDocumentationFile>true</GenerateDocumentationFile><TreatWarningsAsErrors>true</TreatWarningsAsErrors>'
dotnet restore "$portia_build_failure/PortiaStructureSmoke.slnx" > "$portia_build_failure/restore.log" 2>&1
if dotnet run --project "$portia_root/eng/Portia.RepositoryTools/Portia.RepositoryTools.csproj" \
  --configuration Release --no-build -- fix-application-structure --solution "$portia_build_failure/PortiaStructureSmoke.slnx" \
  > "$portia_build_failure/fix.log" 2>&1; then
  echo 'The CLI accepted a split whose post-change solution does not build.' >&2
  exit 1
fi
test -f "$portia_build_failure/Features/Orders/Values/Wrong.cs"
test ! -e "$portia_build_failure/Features/Orders/Values/Price.cs"
test ! -e "$portia_build_failure/Features/Orders/Values/Currency.cs"
grep -F -q 'public sealed record Currency' "$portia_build_failure/Features/Orders/Values/Wrong.cs"

echo 'Qualified PORTIA200/201 file operations through the Portia CLI; project-load failures, default globs, explicit Compile rollback/collision, destination collision refusal, build-failure rollback, and second-pass idempotence passed.'
