#!/usr/bin/env bash
# Writes the Jellyfin plugin repository manifest.json.
#
# Usage: scripts/build-manifest.sh <manifest.json> --entries <dir>
#        scripts/build-manifest.sh <manifest.json> --github
#   --entries <dir>  read the entries scripts/package.sh wrote into <dir>
#   --github         read the entry attached to every GitHub Release of this repository
#                    (uses GITHUB_REPOSITORY, and GITHUB_TOKEN when it is set)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

if [[ $# -lt 2 ]]; then
  echo "usage: $0 <manifest.json> (--entries <dir> | --github)" >&2
  exit 2
fi

out="$1"
shift
dotnet run scripts/release-tool.cs -- manifest --meta src/Jellyfin.Plugin.MediaReports/meta.json --out "$out" "$@"
