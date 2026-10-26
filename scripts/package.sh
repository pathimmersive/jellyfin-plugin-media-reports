#!/usr/bin/env bash
# Publishes the plugin in Release and packages it as a Jellyfin plugin repository zip.
#
# Usage: scripts/package.sh <version> <out-dir> <source-url-base> [changelog]
#   version          x.y.z or x.y.z.w, with or without a leading v (a tag works)
#   out-dir          where the zip and its manifest entry are written
#   source-url-base  the URL the zip will be downloaded from, without the file name
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

if [[ $# -lt 3 ]]; then
  echo "usage: $0 <version> <out-dir> <source-url-base> [changelog]" >&2
  exit 2
fi

version="${1#v}"
if [[ ! "$version" =~ ^[0-9]+(\.[0-9]+){2,3}$ ]]; then
  echo "not a release version: $1" >&2
  exit 2
fi
while [[ "$(tr -cd . <<<"$version" | wc -c)" -lt 3 ]]; do
  version="$version.0"
done

out_dir="$2"
publish_dir="$out_dir/publish"
rm -rf "$publish_dir"
dotnet publish src/Jellyfin.Plugin.MediaReports --nologo -c Release \
  -p:AssemblyVersion="$version" -p:FileVersion="$version" -o "$publish_dir"
dotnet run scripts/release-tool.cs -- package --publish-dir "$publish_dir" --version "$version" \
  --out "$out_dir" --source-url-base "$3" --changelog "${4:-}"
