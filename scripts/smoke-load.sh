#!/usr/bin/env bash
# Builds and tests the plugin, then starts a real Jellyfin 12 server in Docker
# with the plugin installed and waits for the server log to report it loaded.
#
# Usage: scripts/smoke-load.sh [--without-plugin]
#   --without-plugin  start the same server with no plugin; the script must fail
#
# Environment:
#   JELLYFIN_IMAGE  image to run (default jellyfin/jellyfin:12)
#   SMOKE_TIMEOUT   seconds to wait for startup (default 180)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

image="${JELLYFIN_IMAGE:-jellyfin/jellyfin:12}"
timeout="${SMOKE_TIMEOUT:-180}"
with_plugin=1
if [[ "${1:-}" == "--without-plugin" ]]; then
  with_plugin=0
elif [[ -n "${1:-}" ]]; then
  echo "unknown argument: $1" >&2
  exit 2
fi

plugin_name="Media Reports"
plugin_version="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' src/Jellyfin.Plugin.MediaReports/meta.json)"
publish_dir="artifacts/smoke/publish"
stage_dir="artifacts/smoke/stage"
container="media-reports-smoke-$$"

cleanup() {
  docker rm -f "$container" >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "== dotnet test"
dotnet test --nologo

echo "== dotnet publish"
rm -rf "$publish_dir"
dotnet publish src/Jellyfin.Plugin.MediaReports --nologo -c Release -o "$publish_dir"

echo "== start $image (plugin installed: $with_plugin)"
docker create --name "$container" "$image" >/dev/null
if [[ $with_plugin -eq 1 ]]; then
  # docker cp avoids host path translation, so the same script works on Linux and Git Bash.
  # It does not create missing parents, so the whole plugins folder is staged and copied.
  rm -rf "$stage_dir"
  mkdir -p "$stage_dir/plugins/MediaReports_${plugin_version}"
  cp -R "$publish_dir/." "$stage_dir/plugins/MediaReports_${plugin_version}/"
  docker cp "$stage_dir/plugins" "$container:/config/"
fi
docker start "$container" >/dev/null

loaded_pattern="Loaded plugin: \"?${plugin_name}\"? \"?${plugin_version}\"?"
deadline=$((SECONDS + timeout))
while (( SECONDS < deadline )); do
  logs="$(docker logs "$container" 2>&1 || true)"
  if grep -Eq "$loaded_pattern" <<<"$logs"; then
    grep -E "$loaded_pattern" <<<"$logs"
    echo "PASS: Jellyfin loaded the $plugin_name plugin"
    exit 0
  fi
  if grep -q "Startup complete" <<<"$logs"; then
    grep -iE "plugin|MediaReports" <<<"$logs" || true
    echo "FAIL: server started but did not report loading the $plugin_name plugin" >&2
    exit 1
  fi
  if [[ "$(docker inspect -f '{{.State.Running}}' "$container")" != "true" ]]; then
    tail -n 50 <<<"$logs"
    echo "FAIL: container exited before startup completed" >&2
    exit 1
  fi
  sleep 2
done

docker logs --tail 50 "$container" 2>&1 || true
echo "FAIL: no plugin load line within ${timeout}s" >&2
exit 1
