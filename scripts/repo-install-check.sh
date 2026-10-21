#!/usr/bin/env bash
# Proves a Jellyfin server accepts the release zip and manifest.json: packages the
# plugin, serves the zip and manifest from a local web container, adds that URL as
# a plugin repository on a local Jellyfin, installs the plugin through the API,
# restarts the server and waits for it to report the plugin loaded.
#
# Usage: scripts/repo-install-check.sh [--corrupt-zip]
#   --corrupt-zip  change the zip after its checksum is computed; the install must fail
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
case "${1:-}" in
  "") ;;
  --corrupt-zip) corrupt=1 ;;
  *) echo "unknown argument: $1" >&2; exit 2 ;;
esac

meta="src/Jellyfin.Plugin.MediaReports/meta.json"
plugin_name="$(sed -n 's/.*"name": *"\([^"]*\)".*/\1/p' "$meta")"
plugin_guid="$(sed -n 's/.*"guid": *"\([^"]*\)".*/\1/p' "$meta")"
version="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$meta")"

suffix="$$"
network="media-reports-check-$suffix"
repo_container="media-reports-repo-$suffix"
server_container="media-reports-server-$suffix"
repo_host="media-reports-repo"
repo_url="http://$repo_host/manifest.json"
out_dir="artifacts/repo-check"

cleanup() {
  docker rm -f "$server_container" "$repo_container" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
}
trap cleanup EXIT

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

echo "== package $version"
rm -rf "$out_dir"
bash scripts/package.sh "$version" "$out_dir" "http://$repo_host" "Local repository check"
zip_path="$out_dir/media-reports_${version}.zip"
if [[ $corrupt -eq 1 ]]; then
  echo "== corrupt the zip after its checksum was computed"
  printf 'corrupted' >>"$zip_path"
fi
bash scripts/build-manifest.sh "$out_dir/manifest.json" --entries "$out_dir"

echo "== serve the repository"
docker network create "$network" >/dev/null
docker create --name "$repo_container" --network "$network" --network-alias "$repo_host" nginx:alpine >/dev/null
docker cp "$out_dir/manifest.json" "$repo_container:/usr/share/nginx/html/manifest.json"
docker cp "$zip_path" "$repo_container:/usr/share/nginx/html/"
docker start "$repo_container" >/dev/null

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
grep -q "\"name\":\"$plugin_name\"" <<<"$packages" || fail "the server does not list $plugin_name from $repo_url"
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
echo "PASS: $image (Jellyfin ${server_version:-unknown}) installed $plugin_name $version from the repository and loaded it"
