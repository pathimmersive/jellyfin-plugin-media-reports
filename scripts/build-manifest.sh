#!/usr/bin/env bash
# Writes the Jellyfin plugin repository manifest.json, and the catalog image beside it.
#
# Usage: scripts/build-manifest.sh <manifest.json> <image-url-base> --entries <dir>
#        scripts/build-manifest.sh <manifest.json> <image-url-base> --github
#   image-url-base   the URL the catalog image is served from, without the file name
#   --entries <dir>  read the entries scripts/package.sh wrote into <dir>
#   --github         read the entry attached to every GitHub Release of this repository
#                    (uses GITHUB_REPOSITORY, and GITHUB_TOKEN when it is set)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

if [[ $# -lt 3 ]]; then
  echo "usage: $0 <manifest.json> <image-url-base> (--entries <dir> | --github)" >&2
  exit 2
fi

out="$1"
image_url_base="$2"
shift 2
dotnet run scripts/release-tool.cs -- manifest --meta src/Jellyfin.Plugin.MediaReports/meta.json \
  --out "$out" --image-url-base "$image_url_base" "$@"
