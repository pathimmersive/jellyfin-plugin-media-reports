#!/usr/bin/env bash
# Checks a published release end to end: the GitHub Release for the tag carries exactly
# the zip, its manifest entry and SHA256SUMS; the manifest entry's checksum is the zip's
# MD5; the release notes and the manifest changelog are the tag's dated CHANGELOG.md
# entry; SHA256SUMS and the build provenance attestation verify both files; immutable
# releases are on; and the GitHub Pages manifest lists the version with the same
# checksum, download URL and changelog. Then a local Jellyfin 12 installs the version
# from the Pages URL and loads it (scripts/repo-install-check.sh --repository-url).
# Reads GitHub with the GitHub CLI; needs `gh auth login`, Docker and the .NET SDK.
#
# Usage: scripts/check-release.sh <tag>   (for example v0.1.0)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

if [[ $# -ne 1 ]]; then
  echo "usage: $0 <tag>" >&2
  exit 2
fi
tag="$1"
repo="pathimmersive/jellyfin-plugin-media-reports"
pages_url="https://pathimmersive.github.io/jellyfin-plugin-media-reports/manifest.json"

version="${tag#v}"
if [[ ! "$version" =~ ^[0-9]+(\.[0-9]+){2,3}$ ]]; then
  echo "not a release tag: $tag" >&2
  exit 2
fi
while [[ "$(tr -cd . <<<"$version" | wc -c)" -lt 3 ]]; do
  version="$version.0"
done
zip="media-reports_${version}.zip"
entry="media-reports_${version}.json"
plugin_guid="$(sed -n 's/.*"guid": *"\([^"]*\)".*/\1/p' src/Jellyfin.Plugin.MediaReports/meta.json)"

work="artifacts/release-check/$tag"
rm -rf "$work"
mkdir -p "$work/download"

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

py() {
  if command -v python3 >/dev/null 2>&1 && python3 -c "" >/dev/null 2>&1; then python3 "$@"; else python "$@"; fi
}

echo "== GitHub Release $tag"
gh release view "$tag" --repo "$repo" --json isDraft,isPrerelease,body,assets >"$work/release.json" ||
  fail "there is no GitHub Release $tag"
py - "$work/release.json" "$zip" "$entry" <<'PY' || fail "Release $tag does not carry exactly $zip, $entry and SHA256SUMS"
import json, sys
r = json.load(open(sys.argv[1], encoding="utf-8"))
names = sorted(a["name"] for a in r["assets"])
want = sorted([sys.argv[2], sys.argv[3], "SHA256SUMS"])
if r["isDraft"] or r["isPrerelease"]:
    sys.exit("the release is a draft or a prerelease")
sys.exit(0 if names == want else "assets are %s" % names)
PY
gh release download "$tag" --repo "$repo" --dir "$work/download"
echo "Release $tag carries $zip, $entry and SHA256SUMS"

echo "== immutable releases"
[[ "$(gh api "repos/$repo/immutable-releases" --jq .enabled)" == true ]] ||
  fail "immutable releases are off for $repo, so a release's files could be replaced"
echo "immutable releases are on"

echo "== checksum, release notes and changelog"
dotnet run scripts/release-tool.cs -- changelog --release --file CHANGELOG.md --version "$version" >"$work/notes.md" ||
  fail "CHANGELOG.md has no dated entry for $version"
grep -q "SHA256SUMS" "$work/notes.md" || fail "the $version changelog entry does not mention SHA256SUMS"
py - "$work/download/$entry" "$work/download/$zip" "$work/notes.md" "$work/release.json" <<'PY' || fail "the release files or notes do not match"
import hashlib, json, sys
entry = json.load(open(sys.argv[1], encoding="utf-8"))
md5 = hashlib.md5(open(sys.argv[2], "rb").read()).hexdigest()
want = open(sys.argv[3], encoding="utf-8").read().replace("\r\n", "\n").strip()
body = json.load(open(sys.argv[4], encoding="utf-8"))["body"].replace("\r\n", "\n").strip()
problems = []
if entry["checksum"].lower() != md5:
    problems.append("the manifest entry's checksum %s is not the zip's MD5 %s" % (entry["checksum"], md5))
if entry["changelog"].replace("\r\n", "\n").strip() != want:
    problems.append("the manifest entry's changelog is not the CHANGELOG.md entry")
if body != want:
    problems.append("the release notes are not the CHANGELOG.md entry")
for p in problems:
    print("  " + p, file=sys.stderr)
sys.exit(1 if problems else 0)
PY
echo "the checksum is the zip's MD5, and the notes and the changelog are the dated $version entry"

echo "== SHA256SUMS and build provenance"
(cd "$work/download" && sha256sum -c SHA256SUMS) || fail "SHA256SUMS does not match the release files"
for f in "$zip" "$entry"; do
  gh attestation verify "$work/download/$f" --repo "$repo" >/dev/null ||
    fail "the build provenance of $f does not verify against $repo"
  echo "$f: build provenance verified"
done

echo "== the GitHub Pages manifest"
curl -sS --fail -L -o "$work/manifest.json" "$pages_url" || fail "$pages_url serves no manifest"
py - "$work/manifest.json" "$plugin_guid" "$work/download/$entry" <<'PY' || fail "$pages_url does not list $version as released"
import json, sys
manifest = json.load(open(sys.argv[1], encoding="utf-8"))
entry = json.load(open(sys.argv[3], encoding="utf-8"))
listed = [v for p in manifest if p.get("guid") == sys.argv[2] for v in p.get("versions", []) if v.get("version") == entry["version"]]
if not listed:
    sys.exit("  version %s is not listed" % entry["version"])
for field in ("checksum", "sourceUrl", "changelog", "targetAbi"):
    if listed[0].get(field) != entry.get(field):
        sys.exit("  its %s differs from the release's manifest entry" % field)
PY
echo "$pages_url lists $version with the release's checksum, download URL and changelog"

echo "== install from $pages_url"
bash scripts/repo-install-check.sh --repository-url "$pages_url" --version "$version"
echo "PASS: release $tag is published, checks out, and installs from $pages_url"
