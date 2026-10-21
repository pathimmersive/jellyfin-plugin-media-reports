#!/usr/bin/env bash
# Writes the Jellyfin plugin repository manifest.json.
#
# Usage: scripts/build-manifest.sh <manifest.json> --entries <dir>
#        scripts/build-manifest.sh <manifest.json> --gitlab
#   --entries <dir>  read the entries scripts/package.sh wrote into <dir>
#   --gitlab         read the entry attached to every GitLab Release of this project
#                    (uses the CI_API_V4_URL and CI_PROJECT_ID CI variables)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

if [[ $# -lt 2 ]]; then
  echo "usage: $0 <manifest.json> (--entries <dir> | --gitlab)" >&2
  exit 2
fi

out="$1"
shift
dotnet run scripts/release-tool.cs -- manifest --meta src/Jellyfin.Plugin.MediaReports/meta.json --out "$out" "$@"
