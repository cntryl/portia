#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

failed=false
for api_file in src/*/PublicAPI.Unshipped.txt; do
  entries="$(awk 'NF && $0 != "#nullable enable" { print NR ":" $0 }' "$api_file")"
  if [[ -n "$entries" ]]; then
    echo "::error file=$api_file,title=Unshipped public API::Promote reviewed API to PublicAPI.Shipped.txt before publishing."
    printf '%s\n' "$entries"
    failed=true
  fi
done

if [[ "$failed" == true ]]; then
  exit 1
fi
