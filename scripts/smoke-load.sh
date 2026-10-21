#!/usr/bin/env bash
# Builds and tests the plugin, then starts real Jellyfin 12.x servers in Docker
# with the plugin installed and waits for each server log to report it loaded.
#
# Usage: scripts/smoke-load.sh [--without-plugin] [--target-abi <version>]
#   --without-plugin        start the servers with no plugin; the script must fail
#   --target-abi <version>  install the plugin with this targetAbi instead of the
#                           one in meta.json; a version above the server's must fail
#
# Environment:
#   JELLYFIN_IMAGE  run only this image (default: jellyfin/jellyfin:12.0, :12.1, :12)
#   SMOKE_TIMEOUT   seconds to wait for each server to start (default 180)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

if [[ -n "${JELLYFIN_IMAGE:-}" ]]; then
  images=("$JELLYFIN_IMAGE")
else
  images=(jellyfin/jellyfin:12.0 jellyfin/jellyfin:12.1 jellyfin/jellyfin:12)
fi
timeout="${SMOKE_TIMEOUT:-180}"
with_plugin=1
target_abi=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --without-plugin) with_plugin=0; shift ;;
    --target-abi) target_abi="${2:?--target-abi needs a version}"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

plugin_name="Media Reports"
plugin_version="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' src/Jellyfin.Plugin.MediaReports/meta.json)"
publish_dir="artifacts/smoke/publish"
stage_dir="artifacts/smoke/stage"
container=""

cleanup() {
  if [[ -n "$container" ]]; then
    docker rm -f "$container" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

echo "== dotnet test"
dotnet test --nologo

echo "== dotnet publish"
rm -rf "$publish_dir"
dotnet publish src/Jellyfin.Plugin.MediaReports --nologo -c Release -o "$publish_dir"

rm -rf "$stage_dir"
mkdir -p "$stage_dir/plugins/MediaReports_${plugin_version}"
cp -R "$publish_dir/." "$stage_dir/plugins/MediaReports_${plugin_version}/"
if [[ -n "$target_abi" ]]; then
  sed -i "s/\"targetAbi\": *\"[^\"]*\"/\"targetAbi\": \"${target_abi}\"/" \
    "$stage_dir/plugins/MediaReports_${plugin_version}/meta.json"
fi

loaded_pattern="Loaded plugin: \"?${plugin_name}\"? \"?${plugin_version}\"?"

# Starts one server and waits for the load line. Returns non-zero on any failure.
check_image() {
  local image="$1" logs server_version deadline
  container="media-reports-smoke-$$"
  docker rm -f "$container" >/dev/null 2>&1 || true

  echo "== start $image (plugin installed: $with_plugin${target_abi:+, targetAbi $target_abi})"
  docker pull --quiet "$image" >/dev/null
  docker create --name "$container" "$image" >/dev/null
  if [[ $with_plugin -eq 1 ]]; then
    # docker cp avoids host path translation, so the same script works on Linux and Git Bash.
    # It does not create missing parents, so the whole plugins folder is staged and copied.
    docker cp "$stage_dir/plugins" "$container:/config/"
  fi
  docker start "$container" >/dev/null

  deadline=$((SECONDS + timeout))
  while (( SECONDS < deadline )); do
    logs="$(docker logs "$container" 2>&1 || true)"
    server_version="$(sed -n 's/.*Jellyfin version: \([0-9.]*\).*/\1/p' <<<"$logs" | head -n 1)"
    if grep -Eq "$loaded_pattern" <<<"$logs"; then
      grep -E "$loaded_pattern" <<<"$logs"
      echo "PASS: $image (Jellyfin ${server_version:-unknown}) loaded the $plugin_name plugin"
      return 0
    fi
    if grep -q "Startup complete" <<<"$logs"; then
      grep -iE "plugin|MediaReports" <<<"$logs" || true
      echo "FAIL: $image (Jellyfin ${server_version:-unknown}) started but did not report loading the $plugin_name plugin" >&2
      return 1
    fi
    if [[ "$(docker inspect -f '{{.State.Running}}' "$container")" != "true" ]]; then
      tail -n 50 <<<"$logs"
      echo "FAIL: $image exited before startup completed" >&2
      return 1
    fi
    sleep 2
  done

  docker logs --tail 50 "$container" 2>&1 || true
  echo "FAIL: $image did not report a plugin load line within ${timeout}s" >&2
  return 1
}

for image in "${images[@]}"; do
  check_image "$image" || exit 1
  docker rm -f "$container" >/dev/null 2>&1 || true
done
echo "PASS: the $plugin_name plugin loaded on ${#images[@]} image(s): ${images[*]}"
