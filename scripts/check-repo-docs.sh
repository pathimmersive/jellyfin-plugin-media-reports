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
for file in README.md LICENSE CONTRIBUTING.md CODE_OF_CONDUCT.md SECURITY.md THIRD_PARTY_NOTICES.md CHANGELOG.md \
  .github/ISSUE_TEMPLATE/bug.md .github/ISSUE_TEMPLATE/feature.md .github/pull_request_template.md; do
  [[ -s "$file" ]] || fail "$file is missing or empty"
done

echo "== document contents"
if [[ -s README.md ]]; then
  contains README.md "unofficial" "say the plugin is unofficial"
  contains README.md "not affiliated with or endorsed by the Jellyfin project" "say the plugin is not endorsed by Jellyfin"
  contains README.md "Jellyfin 12\.0" "name the supported Jellyfin versions"
  # GitHub drops style attributes but keeps align, so the logo is centred with <p align="center">.
  # awk reads the file itself: grep | head would fail under pipefail when head closes the pipe.
  opening="$(awk 'NF { print; if (++n == 2) exit }' README.md | tr -d '\r' | tr '\n' ' ')"
  [[ "$opening" =~ ^\<p\ align=\"center\"\>\ +\<picture\> ]] || fail "README.md does not open with the logo centred"
  contains README.md "https://pathimmersive\.github\.io/jellyfin-plugin-media-reports/manifest\.json" "give the plugin repository URL"
  # What a new admin needs, one section each.
  for heading in "Features" "Which apps get the Report button" "How the Report button reaches Jellyfin Web" \
    "Install" "Settings" "Update" "Uninstall" "Troubleshooting" "Privacy and stored data" "Versioning"; do
    grep -qx "## $heading" README.md || fail "README.md has no \"## $heading\" section"
  done
  if grep -Eiq "no features|not (yet )?(have|has) (any )?features" README.md; then
    fail "README.md says the plugin has no features"
  fi
  contains README.md "Android TV" "say which apps get only suspected problems"
  contains README.md "index\.html" "say the plugin changes jellyfin-web's index.html"
  contains README.md "File Transformation" "say how the plugin sits with File Transformation"
  contains README.md "suspected problems? (watching )?(is|are) on by default" "say suspected-problem watching is on by default"
  contains README.md "docs/data\.md" "link what the plugin stores"
  contains README.md "CHANGELOG\.md" "link the changelog"
  contains README.md "sha256sum -c .*SHA256SUMS" "say how to check a download against SHA256SUMS"
  contains README.md "gh attestation verify .*--repo pathimmersive/jellyfin-plugin-media-reports" "say how to verify a download's build provenance"
  # Every screenshot the README shows exists.
  while IFS= read -r shot; do
    [[ -s "$shot" ]] || fail "README.md shows $shot, which is missing"
  done < <(grep -oE 'assets/screenshots/[A-Za-z0-9._-]+\.png' README.md | sort -u)
  grep -q 'assets/screenshots/' README.md || fail "README.md shows no screenshots"
fi
if [[ -s docs/data.md ]]; then
  contains docs/data.md "^# What the plugin stores" "open with its title"
else
  fail "docs/data.md is missing or empty"
fi
echo "== the changelog has an entry for this version"
plugin_version="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' src/Jellyfin.Plugin.MediaReports/meta.json)"
if [[ -s CHANGELOG.md ]]; then
  dotnet run scripts/release-tool.cs -- changelog --file CHANGELOG.md --version "$plugin_version" >/dev/null ||
    fail "CHANGELOG.md has no entry for $plugin_version, the version in meta.json"
else
  fail "CHANGELOG.md is missing or empty"
fi
# Release notes for a tag come only from a dated entry, never from one still marked Unreleased.
notes_dir="$(mktemp -d)"
printf '# Changelog\n\n## [9.8.7] - Unreleased\n\n- Not out yet.\n\n## [9.8.6] - 2026-01-02\n\n- Out.\n' >"$notes_dir/CHANGELOG.md"
if dotnet run scripts/release-tool.cs -- changelog --release --file "$notes_dir/CHANGELOG.md" --version v9.8.7 >/dev/null 2>&1; then
  fail "release-tool gives release notes for an entry marked Unreleased"
fi
dotnet run scripts/release-tool.cs -- changelog --release --file "$notes_dir/CHANGELOG.md" --version v9.8.6 >/dev/null ||
  fail "release-tool refuses release notes for a dated entry"
rm -rf "$notes_dir"
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
done < <(find . \( -path ./.git -o -path ./artifacts -o -name bin -o -name obj -o -name node_modules \) -prune -o \
  -type f \( -iname '*.png' -o -iname '*.jpg' -o -iname '*.jpeg' -o -iname '*.gif' -o -iname '*.svg' \
  -o -iname '*.ico' -o -iname '*.webp' \) -print)

echo "== logo images match their SVG source"
dotnet run scripts/render-logo.cs -- --check || fail "a logo PNG differs from its SVG source"

echo "== the API reference covers every route"
py() {
  if command -v python3 >/dev/null 2>&1 && python3 -c "" >/dev/null 2>&1; then python3 "$@"; else python "$@"; fi
}
if [[ -s docs/api.md ]]; then
  contains README.md "docs/api\.md" "link the API reference"
  contains docs/api.md "^\*\*Values you don't know\.\*\*" "tell clients to accept values they don't know"
  # Each controller's [Route] joined with each action's [HttpX] template, constants resolved,
  # against the `### `METHOD /path`` headings of docs/api.md; a route on one side only fails.
  py - docs/api.md $(find src -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*') <<'PY' || fail "docs/api.md and the controllers' routes differ"
import re, sys
doc, files = sys.argv[1], sys.argv[2:]
texts = {f: open(f, encoding="utf-8").read() for f in files}
consts = {}
for t in texts.values():
    for cls, body in re.findall(r"class (\w+)\b(.*?)(?=\nclass |\npublic |\Z)", t, re.S):
        for name, value in re.findall(r'const string (\w+) = "([^"]*)";', body):
            consts[cls + "." + name] = value
routes = set()
for t in texts.values():
    for prefix, body in re.findall(r'\[Route\("([^"]+)"\)\]\s*(?:\[[^\]]*\]\s*)*public sealed class \w+(.*?)(?=\n\[|\Z)', t, re.S):
        for verb, arg in re.findall(r"\[Http(Get|Post|Put|Patch|Delete)(?:\(([^)]*)\))?\]", body):
            arg = arg.strip()
            template = arg.strip('"') if arg.startswith('"') else consts.get(arg, arg) if arg else ""
            routes.add(verb.upper() + " /" + "/".join(x for x in (prefix, template) if x))
documented = set(re.findall(r"^### `([A-Z]+ /[^`?]+)", open(doc, encoding="utf-8").read(), re.M))
for r in sorted(routes - documented):
    print("FAIL: docs/api.md does not document " + r, file=sys.stderr)
for r in sorted(documented - routes):
    print("FAIL: docs/api.md documents " + r + ", which no controller declares", file=sys.stderr)
print("routes: %d declared, %d documented" % (len(routes), len(documented)))
sys.exit(1 if routes != documented or not routes else 0)
PY
else
  fail "docs/api.md is missing or empty"
fi

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
