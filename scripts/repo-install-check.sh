#!/usr/bin/env bash
# Proves a Jellyfin server accepts the release zip and manifest.json: packages the
# plugin, serves the zip and manifest from a local web container, adds that URL as
# a plugin repository on a local Jellyfin, installs the plugin through the API,
# restarts the server and waits for it to report the plugin loaded. Checks the catalog
# image along the way: the manifest names it, the repository serves it, the plugin
# folder holds it and the server serves it for the installed plugin. Checks the changelog:
# a version with no CHANGELOG.md entry is not packaged, and the version's entry is the
# changelog in the manifest, the catalog and the installed plugin's meta.json. Checks
# SHA256SUMS: it matches the zip and the manifest entry, and a changed zip fails it.
#
# Usage: scripts/repo-install-check.sh [--corrupt-zip]
#        scripts/repo-install-check.sh --repository-url <url> [--version <x.y.z[.w]>]
#   --corrupt-zip     change the zip after its checksum is computed; the install must fail
#   --repository-url  skip the packaging and the local repository, and install from a
#                     published manifest instead (the GitHub Pages URL after a release)
#   --version         the version to install from that manifest (default: meta.json's)
#
# Environment:
#   JELLYFIN_IMAGE  image to run (default jellyfin/jellyfin:12)
#   CHECK_TIMEOUT   seconds to wait for each step (default 180)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

image="${JELLYFIN_IMAGE:-jellyfin/jellyfin:12}"
timeout="${CHECK_TIMEOUT:-180}"
corrupt=0
published_url=""
wanted_version=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --corrupt-zip) corrupt=1; shift ;;
    --repository-url) published_url="${2:?--repository-url needs a URL}"; shift 2 ;;
    --version) wanted_version="${2:?--version needs a version}"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
if [[ -n "$wanted_version" && -z "$published_url" ]]; then
  echo "--version only goes with --repository-url" >&2
  exit 2
fi
if [[ -n "$published_url" && $corrupt -eq 1 ]]; then
  echo "--corrupt-zip does not go with --repository-url" >&2
  exit 2
fi

meta="src/Jellyfin.Plugin.MediaReports/meta.json"
plugin_name="$(sed -n 's/.*"name": *"\([^"]*\)".*/\1/p' "$meta")"
plugin_guid="$(sed -n 's/.*"guid": *"\([^"]*\)".*/\1/p' "$meta")"
version="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$meta")"
image_name="$(sed -n 's/.*"imagePath": *"\([^"]*\)".*/\1/p' "$meta")"
image_file="src/Jellyfin.Plugin.MediaReports/$image_name"

suffix="$$"
network="media-reports-check-$suffix"
repo_container="media-reports-repo-$suffix"
server_container="media-reports-server-$suffix"
repo_host="media-reports-repo"
repo_url="http://$repo_host/manifest.json"
repo_base="http://$repo_host"
out_dir="artifacts/repo-check"
if [[ -n "$published_url" ]]; then
  repo_url="$published_url"
  repo_base="${published_url%/*}"
  version="${wanted_version:-$version}"
  version="${version#v}"
  while [[ "$(tr -cd . <<<"$version" | wc -c)" -lt 3 ]]; do
    version="$version.0"
  done
fi

cleanup() {
  docker rm -f "$server_container" "$repo_container" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
}
trap cleanup EXIT

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

# Python reads the JSON files below; every runner has it.
py() {
  if command -v python3 >/dev/null 2>&1 && python3 -c "" >/dev/null 2>&1; then python3 "$@"; else python "$@"; fi
}

rm -rf "$out_dir"
mkdir -p "$out_dir"
# changelog_is <file> <python expression for the changelog>: fails unless it equals the CHANGELOG.md entry.
changelog_is() {
  py - "$1" "$out_dir/notes.md" "$2" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
want = open(sys.argv[2], encoding="utf-8").read().strip()
got = eval(sys.argv[3])
sys.exit(0 if got == want else "changelog in %s is %r, not the CHANGELOG.md entry" % (sys.argv[1], got[:80]))
PY
}

docker network create "$network" >/dev/null
if [[ -n "$published_url" ]]; then
  echo "== the published manifest at $repo_url"
  curl -sS --fail -L -o "$out_dir/manifest.json" "$repo_url" || fail "$repo_url serves no manifest"
  dotnet run scripts/release-tool.cs -- changelog --release --file CHANGELOG.md --version "$version" >"$out_dir/notes.md"
  py - "$out_dir/manifest.json" "$plugin_guid" "$version" <<'PY' || fail "$repo_url does not list $plugin_name $version"
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
found = [v for p in d if p.get("guid") == sys.argv[2] for v in p.get("versions", []) if v.get("version") == sys.argv[3]]
sys.exit(0 if found else 1)
PY
  curl -sS --fail -L -o "$out_dir/served-repo-$image_name" "$repo_base/$image_name" ||
    fail "the repository does not serve $image_name"
  echo "$repo_url lists $plugin_name $version"
else
  echo "== a version with no changelog entry is not packaged"
  cat >"$out_dir/no-entry.md" <<'EOF'
# Changelog

## [9.9.9] - 2099-01-01

- Something else.
EOF
  if CHANGELOG_FILE="$out_dir/no-entry.md" bash scripts/package.sh "$version" "$out_dir/no-entry" "http://$repo_host" 2>"$out_dir/no-entry.log"; then
    fail "package.sh packaged $version from a changelog with no entry for it"
  fi
  grep -q "has no entry for" "$out_dir/no-entry.log" || { cat "$out_dir/no-entry.log" >&2; fail "package.sh failed for another reason"; }
  [[ ! -e "$out_dir/no-entry" ]] || fail "package.sh wrote files before refusing"
  echo "refused: $(grep -o 'has no entry for.*' "$out_dir/no-entry.log")"

  echo "== package $version"
  bash scripts/package.sh "$version" "$out_dir" "http://$repo_host"
  dotnet run scripts/release-tool.cs -- changelog --file CHANGELOG.md --version "$version" >"$out_dir/notes.md"
  changelog_is "$out_dir/media-reports_${version}.json" 'd["changelog"]' || fail "the manifest entry's changelog differs"
  py - "$out_dir/media-reports_${version}.zip" "$out_dir/zip-meta.json" <<'PY'
import sys, zipfile
open(sys.argv[2], "wb").write(zipfile.ZipFile(sys.argv[1]).read("meta.json"))
PY
  changelog_is "$out_dir/zip-meta.json" 'd["changelog"]' || fail "the packaged meta.json's changelog differs"
  [[ "$(py -c 'import json,sys; print(json.load(open(sys.argv[1]))["owner"])' "$out_dir/zip-meta.json")" == pathimmersive ]] ||
    fail "the packaged meta.json does not name pathimmersive as owner"
  echo "the manifest entry and the packaged meta.json carry the $version changelog entry"
  zip_path="$out_dir/media-reports_${version}.zip"

  echo "== SHA256SUMS matches the release files"
  [[ "$(wc -l <"$out_dir/SHA256SUMS")" -eq 2 ]] || fail "SHA256SUMS does not list exactly the zip and the manifest entry"
  (cd "$out_dir" && sha256sum -c SHA256SUMS) || fail "SHA256SUMS does not match the release files"
  mkdir -p "$out_dir/tampered"
  cp "$out_dir/SHA256SUMS" "$out_dir/media-reports_${version}.json" "$zip_path" "$out_dir/tampered/"
  printf 'x' >>"$out_dir/tampered/media-reports_${version}.zip"
  if (cd "$out_dir/tampered" && sha256sum -c --quiet SHA256SUMS) >/dev/null 2>&1; then
    fail "SHA256SUMS still passes for a changed zip"
  fi
  echo "SHA256SUMS lists both files, matches them, and fails for a changed zip"
  # Jellyfin also downloads the image from the manifest, so check the package itself ships it.
  cmp -s "$out_dir/publish/$image_name" "$image_file" || fail "the packaged plugin does not ship $image_name"
  if [[ $corrupt -eq 1 ]]; then
    echo "== corrupt the zip after its checksum was computed"
    printf 'corrupted' >>"$zip_path"
  fi
  bash scripts/build-manifest.sh "$out_dir/manifest.json" "http://$repo_host" --entries "$out_dir"
  grep -q "\"imageUrl\": \"$repo_base/$image_name\"" "$out_dir/manifest.json" ||
    fail "the manifest does not name the catalog image $image_name"

  echo "== serve the repository"
  docker create --name "$repo_container" --network "$network" --network-alias "$repo_host" nginx:alpine >/dev/null
  docker cp "$out_dir/manifest.json" "$repo_container:/usr/share/nginx/html/manifest.json"
  docker cp "$zip_path" "$repo_container:/usr/share/nginx/html/"
  docker cp "$out_dir/$image_name" "$repo_container:/usr/share/nginx/html/"
  docker start "$repo_container" >/dev/null
  docker exec "$repo_container" wget -q -O /dev/null "http://localhost/$image_name" ||
    fail "the repository does not serve $image_name"
fi

echo "== start $image"
docker pull --quiet "$image" >/dev/null
docker create --name "$server_container" --network "$network" -p 127.0.0.1::8096 "$image" >/dev/null
docker start "$server_container" >/dev/null
port="$(docker port "$server_container" 8096/tcp | head -n 1 | sed 's/.*://')"
api="http://127.0.0.1:$port"

# Waits until the server log matches a pattern; fails on timeout or container exit.
wait_for_log() {
  local pattern="$1" deadline=$((SECONDS + timeout))
  while (( SECONDS < deadline )); do
    if docker logs "$server_container" 2>&1 | grep -Eq "$pattern"; then
      return 0
    fi
    if [[ "$(docker inspect -f '{{.State.Running}}' "$server_container")" != "true" ]]; then
      docker logs --tail 50 "$server_container" 2>&1 || true
      fail "server exited while waiting for: $pattern"
    fi
    sleep 2
  done
  docker logs --tail 50 "$server_container" 2>&1 || true
  fail "timed out waiting for: $pattern"
}

auth='MediaBrowser Client="media-reports-check", Device="script", DeviceId="media-reports-check", Version="1.0.0"'
call() {
  local method="$1" path="$2" body="${3:-}"
  if ! curl -sS --fail-with-body -X "$method" "$api$path" \
    -H "Authorization: $auth${token:+, Token=\"$token\"}" \
    -H "Content-Type: application/json" \
    ${body:+--data "$body"}; then
    echo >&2
    docker logs --tail 40 "$server_container" 2>&1 | grep -A4 -E '\[(ERR|WRN)\]' >&2 || true
    fail "$method $path was refused"
  fi
}

wait_for_log "Startup complete"
server_version="$(docker logs "$server_container" 2>&1 | sed -n 's/.*Jellyfin version: \([0-9.]*\).*/\1/p' | head -n 1)"

echo "== complete the startup wizard"
token=""
call POST /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}'
call GET /Startup/User >/dev/null
call POST /Startup/User '{"Name":"check","Password":"check-password"}'
call POST /Startup/Complete
token="$(call POST /Users/AuthenticateByName '{"Username":"check","Pw":"check-password"}' |
  sed -n 's/.*"AccessToken":"\([^"]*\)".*/\1/p')"
[[ -n "$token" ]] || fail "could not sign in to the server"

echo "== add the repository and install $plugin_name $version"
call POST /Repositories "[{\"Name\":\"Media Reports check\",\"Url\":\"$repo_url\",\"Enabled\":true}]"
packages="$(call GET /Packages)"
grep -q "\"name\":\"$plugin_name\"" <<<"$packages" || {
  docker logs --tail 60 "$server_container" 2>&1 | grep -iE -A3 "repositor|package|manifest" >&2 || true
  fail "the server does not list $plugin_name from $repo_url"
}
grep -q "\"imageUrl\":\"$repo_base/$image_name\"" <<<"$packages" ||
  fail "the catalog does not give the image URL for $plugin_name"
call GET /Packages >"$out_dir/packages.json"
changelog_is "$out_dir/packages.json" '[v["changelog"] for p in d if p["name"] == "Media Reports" for v in p["versions"]][0]' ||
  fail "the catalog does not show the $version changelog entry"
encoded_name="${plugin_name// /%20}"
call POST "/Packages/Installed/$encoded_name?assemblyGuid=$plugin_guid&version=$version&repositoryUrl=$repo_url"

deadline=$((SECONDS + timeout))
while true; do
  if docker logs "$server_container" 2>&1 | grep -iE "checksum|installation.*fail|failed.*install" >&2; then
    fail "the server refused to install $plugin_name $version"
  fi
  if docker exec "$server_container" sh -c "ls /config/plugins/*/Jellyfin.Plugin.MediaReports.dll" >/dev/null 2>&1; then
    break
  fi
  (( SECONDS < deadline )) || fail "timed out waiting for the install of $plugin_name $version"
  sleep 2
done

echo "== restart and wait for the plugin to load"
docker restart "$server_container" >/dev/null
loaded_pattern="Loaded plugin: \"?${plugin_name}\"? \"?${version}\"?"
wait_for_log "$loaded_pattern"
docker logs "$server_container" 2>&1 | grep -E "$loaded_pattern"

echo "== the installed plugin's image"
# The restart gives the container a new host port.
port="$(docker port "$server_container" 8096/tcp | head -n 1 | sed 's/.*://')"
api="http://127.0.0.1:$port"
deadline=$((SECONDS + timeout))
until curl -sf -o /dev/null "$api/System/Info/Public"; do
  (( SECONDS < deadline )) || fail "the server did not answer after the restart"
  sleep 2
done
docker exec "$server_container" sh -c "ls /config/plugins/*/$image_name" >/dev/null 2>&1 ||
  fail "the plugin folder does not hold $image_name"
plugins="$(call GET /Plugins)"
grep -Eq "\"Name\":\"$plugin_name\"[^}]*\"HasImage\":true|\"HasImage\":true[^}]*\"Name\":\"$plugin_name\"" <<<"$plugins" ||
  fail "the installed plugin list shows no image for $plugin_name"
served="$out_dir/served-$image_name"
curl -sS --fail -o "$served" "$api/Plugins/$plugin_guid/$version/Image" ||
  fail "the server does not serve the image of $plugin_name"
cmp -s "$served" "$image_file" || fail "the server serves a different image than $image_file"
echo "the server serves $image_name for $plugin_name $version"
docker exec "$server_container" sh -c "cat /config/plugins/*/meta.json" >"$out_dir/installed-meta.json"
changelog_is "$out_dir/installed-meta.json" 'd["changelog"]' || fail "the installed plugin's meta.json lacks the changelog entry"
echo "the catalog and the installed plugin carry the $version changelog entry"
echo "PASS: $image (Jellyfin ${server_version:-unknown}) installed $plugin_name $version from $repo_url and loaded it"
