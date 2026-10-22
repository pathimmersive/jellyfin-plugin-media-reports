#!/usr/bin/env bash
# Checks the repository documents a visitor, contributor or admin relies on, and that
# THIRD_PARTY_NOTICES.md lists every package the published plugin ships.
#
# Usage: scripts/check-repo-docs.sh [--deps <deps.json>]
#   --deps <file>  check this deps.json instead of publishing the plugin
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

deps=""
case "${1:-}" in
  "") ;;
  --deps) deps="${2:?--deps needs a file}" ;;
  *) echo "unknown argument: $1" >&2; exit 2 ;;
esac

# Licenses accepted for components shipped with a GPL-3.0-or-later plugin.
allowed_licenses="MIT Apache-2.0 BSD-2-Clause BSD-3-Clause ISC Zlib MPL-2.0 LGPL-2.1-or-later LGPL-3.0-only LGPL-3.0-or-later GPL-3.0-only GPL-3.0-or-later"

failures=0
fail() {
  echo "FAIL: $*" >&2
  failures=$((failures + 1))
}

contains() {
  local file="$1" pattern="$2" what="$3"
  grep -Eiq -- "$pattern" "$file" || fail "$file does not $what"
}

echo "== required files"
for file in README.md LICENSE CONTRIBUTING.md CODE_OF_CONDUCT.md SECURITY.md THIRD_PARTY_NOTICES.md \
  .github/ISSUE_TEMPLATE/bug.md .github/ISSUE_TEMPLATE/feature.md .github/pull_request_template.md; do
  [[ -s "$file" ]] || fail "$file is missing or empty"
done

echo "== document contents"
if [[ -s README.md ]]; then
  contains README.md "unofficial" "say the plugin is unofficial"
  contains README.md "not affiliated with or endorsed by the Jellyfin project" "say the plugin is not endorsed by Jellyfin"
  contains README.md "Jellyfin 12\.0" "name the supported Jellyfin versions"
  contains README.md "https://pathimmersive\.github\.io/jellyfin-plugin-media-reports/manifest\.json" "give the plugin repository URL"
fi
if [[ -s CONTRIBUTING.md ]]; then
  contains CONTRIBUTING.md "dotnet test" "name the test command"
  contains CONTRIBUTING.md "scripts/smoke-load\.sh" "name the load check"
  contains CONTRIBUTING.md "Conventional Commits" "name the commit style"
  contains CONTRIBUTING.md "GPL-3\.0-or-later" "state the contribution license"
fi
if [[ -s CODE_OF_CONDUCT.md ]]; then
  contains CODE_OF_CONDUCT.md "Contributor Covenant" "use the Contributor Covenant"
  contains CODE_OF_CONDUCT.md "version 2\.1" "use version 2.1"
fi
for file in SECURITY.md CODE_OF_CONDUCT.md; do
  [[ -s "$file" ]] || continue
  contains "$file" "github\.com/pathimmersive/jellyfin-plugin-media-reports/security/advisories/new" "send reports to private vulnerability reporting"
  if grep -Eq '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}' "$file"; then
    fail "$file contains an email address"
  fi
done

echo "== no Jellyfin brand assets"
while IFS= read -r image; do
  if [[ "$(basename "$image" | tr '[:upper:]' '[:lower:]')" == *jellyfin* ]] ||
    { [[ "$image" == *.svg ]] && grep -qi jellyfin "$image"; }; then
    fail "$image looks like a Jellyfin brand asset"
  fi
done < <(find . \( -path ./.git -o -path ./artifacts -o -name bin -o -name obj \) -prune -o \
  -type f \( -iname '*.png' -o -iname '*.jpg' -o -iname '*.jpeg' -o -iname '*.gif' -o -iname '*.svg' \
  -o -iname '*.ico' -o -iname '*.webp' \) -print)

echo "== third-party notices cover the published plugin"
if [[ -z "$deps" ]]; then
  publish_dir="artifacts/docs-check/publish"
  rm -rf "$publish_dir"
  dotnet publish src/Jellyfin.Plugin.MediaReports --nologo -c Release -o "$publish_dir" >/dev/null
  deps="$publish_dir/Jellyfin.Plugin.MediaReports.deps.json"
fi
[[ -s "$deps" ]] || fail "$deps is missing"

# Packages from the "libraries" section of deps.json, except the server's own libraries.
shipped="$(awk '
  /"libraries": *\{/ { in_libs = 1; next }
  in_libs && match($0, /^ *"[^"]+\/[^"]+": *\{/) { key = $0; sub(/^ *"/, "", key); sub(/\/.*/, "", key); next }
  in_libs && key != "" && /"type": *"package"/ { print key; key = "" }
' "$deps" | grep -v '^Jellyfin\.' | sort -u || true)"

# Rows of the "Distributed with the plugin" table: | Package | License |
notices="$(awk '
  /^## / { in_section = ($0 ~ /^## Distributed with the plugin/); next }
  in_section && /^\|/ && !/^\| *Package *\|/ && !/^\|[- |]+\|$/ { print }
' THIRD_PARTY_NOTICES.md 2>/dev/null || true)"

for package in $shipped; do
  row="$(grep -E "^\| *${package//./\\.} *\|" <<<"$notices" || true)"
  if [[ -z "$row" ]]; then
    fail "THIRD_PARTY_NOTICES.md does not list shipped package $package"
    continue
  fi
  license="$(awk -F'|' '{ gsub(/ /, "", $3); print $3 }' <<<"$row")"
  if [[ " $allowed_licenses " != *" $license "* ]]; then
    fail "shipped package $package has license '$license', which is not on the GPL-3.0 compatible list"
  fi
done
echo "shipped packages: ${shipped:-none}"

if (( failures > 0 )); then
  echo "FAIL: $failures problem(s)" >&2
  exit 1
fi
echo "PASS: repository documents and third-party notices are complete"
