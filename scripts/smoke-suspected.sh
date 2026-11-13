#!/usr/bin/env bash
# Starts a real Jellyfin 12 server in Docker with the plugin and plays three scripted sessions
# the way native TV apps report them, then checks the suspected problems the plugin noticed on
# its own:
#   - a direct play whose reported position jumps back three times after its first minute
#     (Freezing);
#   - a transcode capped at 1 Mbps from a 20 Mbps 1080p source (Low quality);
#   - an HLS transcode of a 4K HEVC clip with the server held to half a CPU (Transcoder too slow).
# Each is stored once with what the server saw in words, counted in the metrics, holds no file
# path, and no report or issue is made.
#
# Usage: scripts/smoke-suspected.sh
#
# Environment:
#   JELLYFIN_IMAGE  image to run (default jellyfin/jellyfin:12)
#   SMOKE_TIMEOUT   seconds to wait for each step (default 240)
#   SMOKE_KEEP      when set, leave the container running at the end
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

image="${JELLYFIN_IMAGE:-jellyfin/jellyfin:12}"
timeout="${SMOKE_TIMEOUT:-240}"
[[ $# -eq 0 ]] || { echo "unknown argument: $1" >&2; exit 2; }

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

host_path() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else echo "$1"; fi
}

py() {
  if command -v python3 >/dev/null 2>&1 && python3 -c "" >/dev/null 2>&1; then python3 "$@"; else python "$@"; fi
}

json() {
  py -c "import json,sys; d=json.load(sys.stdin); v=$1; print('' if v is None else v)"
}

work="$repo_root/artifacts/smoke-suspected"
meta="src/Jellyfin.Plugin.MediaReports/meta.json"
plugin_version="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$meta")"
container="media-reports-suspected-$$"
api=""
pids=()

cleanup() {
  local status=$?
  for pid in "${pids[@]}"; do kill "$pid" 2>/dev/null || true; done
  if (( status != 0 )) && [[ -n "$api" ]]; then
    echo "== server log (last 60 lines)" >&2
    docker logs --tail 60 "$container" >&2 2>&1 || true
  fi
  if [[ -n "${SMOKE_KEEP:-}" ]]; then echo "kept: $container at $api"; else docker rm -f "$container" >/dev/null 2>&1 || true; fi
}
trap cleanup EXIT

echo "== dotnet publish"
rm -rf "$work/publish" "$work/stage" "$work/results"
mkdir -p "$work/results" "$work/media"
dotnet publish src/Jellyfin.Plugin.MediaReports --nologo -c Release -o "$work/publish" >/dev/null
mkdir -p "$work/stage/plugins/MediaReports_${plugin_version}"
cp -R "$work/publish/." "$work/stage/plugins/MediaReports_${plugin_version}/"

docker pull --quiet "$image" >/dev/null
ffmpeg() {
  MSYS_NO_PATHCONV=1 docker run --rm -v "$(host_path "$work/media"):/out" --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg "$image" \
    -hide_banner -loglevel error "$@"
}
audio=(-f lavfi -i sine=frequency=440:sample_rate=48000)
if [[ ! -s "$work/media/Plain (2026)/Plain (2026).mkv" ]]; then
  echo "== generate a 1080p H.264 clip at 20 Mbps"
  mkdir -p "$work/media/Plain (2026)"
  ffmpeg -f lavfi -i testsrc2=size=1920x1080:rate=24 "${audio[@]}" -t 120 \
    -c:v libx264 -preset veryfast -b:v 20M -maxrate 20M -bufsize 40M -pix_fmt yuv420p -c:a aac -b:a 192k -ac 2 \
    "/out/Plain (2026)/Plain (2026).mkv"
fi
if [[ ! -s "$work/media/Heavy (2026)/Heavy (2026).mkv" ]]; then
  echo "== generate a 4K HEVC clip"
  mkdir -p "$work/media/Heavy (2026)"
  ffmpeg -f lavfi -i testsrc2=size=3840x2160:rate=24 "${audio[@]}" -t 60 \
    -c:v libx265 -preset ultrafast -pix_fmt yuv420p10le -b:v 60M -maxrate 60M -bufsize 120M -x265-params log-level=error \
    -c:a aac -b:a 192k -ac 2 "/out/Heavy (2026)/Heavy (2026).mkv"
fi

echo "== start $image"
MSYS_NO_PATHCONV=1 docker create --name "$container" -p 127.0.0.1::8096 \
  -v "$(host_path "$work/media"):/media/movies:ro" "$image" >/dev/null
docker cp "$work/stage/plugins" "$container:/config/"
docker start "$container" >/dev/null
api="http://127.0.0.1:$(docker port "$container" 8096/tcp | head -n 1 | sed 's/.*://')"
deadline=$((SECONDS + timeout))
until docker logs "$container" 2>&1 | grep "Startup complete" >/dev/null; do
  (( SECONDS < deadline )) || fail "the server did not start"
  sleep 2
done

# auth <client> <device> [token]: the authorization header a TV app sends.
auth() {
  echo "MediaBrowser Client=\"$1\", Device=\"$2\", DeviceId=\"smoke-$(tr ' ' '-' <<<"$2")\", Version=\"1.0.0\"${3:+, Token=\"$3\"}"
}

# call <client> <device> <token> <method> <path> [body]: prints the body; fails on an error status.
call() {
  local client="$1" device="$2" tok="$3" method="$4" path="$5" body="${6:-}"
  curl -sS --fail-with-body -X "$method" "$api$path" -H "Authorization: $(auth "$client" "$device" "$tok")" \
    -H "Content-Type: application/json" ${body:+--data "$body"} || { echo >&2; fail "$method $path was refused"; }
}

echo "== complete the startup wizard, add alice and the library"
call smoke box "" POST /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null
call smoke box "" GET /Startup/User >/dev/null
call smoke box "" POST /Startup/User '{"Name":"admin","Password":"admin-password"}' >/dev/null
call smoke box "" POST /Startup/Complete >/dev/null
admin="$(call smoke box "" POST /Users/AuthenticateByName '{"Username":"admin","Pw":"admin-password"}' | json "d['AccessToken']")"
call smoke box "$admin" POST /Users/New '{"Name":"alice","Password":"alice-password"}' >/dev/null
call smoke box "$admin" POST "/Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=true" \
  '{"LibraryOptions":{"PathInfos":[{"Path":"/media/movies"}]}}' >/dev/null
admin_id="$(call smoke box "$admin" GET /Users/Me | json "d['Id']")"
item() {
  call smoke box "$admin" GET "/Items?userId=$admin_id&IncludeItemTypes=Movie&Recursive=true&searchTerm=$1" | json "d['Items'][0]['Id'] if d['Items'] else None"
}
deadline=$((SECONDS + timeout))
until [[ -n "$(item Plain)" && -n "$(item Heavy)" ]]; do
  (( SECONDS < deadline )) || fail "the clips did not appear in the library"
  sleep 3
done
plain="$(item Plain)"
heavy="$(item Heavy)"

# play <client> <device> <item> <profile json> <seconds> <bitrate>: signs in as alice from a TV app,
# asks for a transcode with the profile, fetches its segments as a player would, and reports
# progress every second for the given time.
play_transcode() {
  local client="$1" device="$2" item_id="$3" profile="$4" seconds="$5" bitrate="$6"
  local tok info session source url master base playlist
  tok="$(call "$client" "$device" "" POST /Users/AuthenticateByName '{"Username":"alice","Pw":"alice-password"}' | json "d['AccessToken']")"
  local user_id
  user_id="$(call "$client" "$device" "$tok" GET /Users/Me | json "d['Id']")"
  info="$(call "$client" "$device" "$tok" POST "/Items/$item_id/PlaybackInfo?userId=$user_id&maxStreamingBitrate=$bitrate&autoOpenLiveStream=false" "$profile")"
  session="$(json "d['PlaySessionId']" <<<"$info")"
  source="$(json "d['MediaSources'][0]['Id']" <<<"$info")"
  url="$(json "d['MediaSources'][0].get('TranscodingUrl')" <<<"$info")"
  [[ -n "$url" ]] || fail "$device: the server did not offer a transcode"
  master="$(call "$client" "$device" "$tok" GET "$url")"
  base="$(dirname "${url%%\?*}")"
  playlist="$(call "$client" "$device" "$tok" GET "$base/$(grep -v '^#' <<<"$master" | head -n 1)")"
  mapfile -t segments < <(grep -v '^#' <<<"$playlist")
  state() { echo "{\"ItemId\":\"$item_id\",\"MediaSourceId\":\"$source\",\"PlaySessionId\":\"$session\",\"PlayMethod\":\"Transcode\",\"PositionTicks\":$1,\"CanSeek\":true}"; }
  call "$client" "$device" "$tok" POST /Sessions/Playing "$(state 0)" >/dev/null
  (
    for s in "${segments[@]}"; do
      for _ in $(seq 1 60); do
        code="$(curl -sS -o /dev/null -w '%{http_code}' "$api$base/$s" -H "Authorization: $(auth "$client" "$device" "$tok")")" || code=0
        [[ "$code" == 200 ]] && break
        sleep 1
      done
    done
  ) &
  pids+=($!)
  for p in $(seq 1 "$seconds"); do
    sleep 1
    if (( p % 5 == 1 )); then
      call smoke box "$admin" GET /Sessions | py -c "
import json, sys
for s in json.load(sys.stdin):
    if s.get('DeviceName') == sys.argv[1] and s.get('NowPlayingItem'):
        t = s.get('TranscodingInfo') or {}
        print('  %ss: %s, reasons %s, %s fps' % (sys.argv[2], (s.get('PlayState') or {}).get('PlayMethod'), t.get('TranscodeReasons'), t.get('Framerate')))" "$device" "$p"
    fi
    curl -sS -o /dev/null -X POST "$api/Sessions/Playing/Progress" -H "Content-Type: application/json" \
      -H "Authorization: $(auth "$client" "$device" "$tok")" --data "$(state $((p * 10000000)))" || true
  done
  curl -sS -o /dev/null -X POST "$api/Sessions/Playing/Stopped" -H "Content-Type: application/json" \
    -H "Authorization: $(auth "$client" "$device" "$tok")" --data "$(state $((seconds * 10000000)))" || true
}

echo "== Freezing: a direct play whose position jumps back three times after its first minute"
roku="$(call "Jellyfin Roku" "Living Room TV" "" POST /Users/AuthenticateByName '{"Username":"alice","Pw":"alice-password"}' | json "d['AccessToken']")"
direct() { echo "{\"ItemId\":\"$plain\",\"MediaSourceId\":\"$plain\",\"PlaySessionId\":\"smoke-freeze\",\"PlayMethod\":\"DirectPlay\",\"PositionTicks\":$(( $1 * 10000000 )),\"CanSeek\":true}"; }
call "Jellyfin Roku" "Living Room TV" "$roku" POST /Sessions/Playing "$(direct 0)" >/dev/null
position=0
for t in $(seq 5 5 95); do
  sleep 5
  position=$((position + 5))
  # After the first minute, every other report goes back 8 s.
  if (( t >= 70 && t % 10 == 0 )); then position=$((position - 13)); fi
  call "Jellyfin Roku" "Living Room TV" "$roku" POST /Sessions/Playing/Progress "$(direct "$position")" >/dev/null
done
call "Jellyfin Roku" "Living Room TV" "$roku" POST /Sessions/Playing/Stopped "$(direct "$position")" >/dev/null

echo "== Low quality: a transcode capped at 1 Mbps from the 20 Mbps 1080p clip"
capped='{"DeviceProfile":{"MaxStreamingBitrate":1000000,"DirectPlayProfiles":[{"Container":"mkv","Type":"Video","VideoCodec":"h264","AudioCodec":"aac"}],"TranscodingProfiles":[{"Container":"ts","Type":"Video","VideoCodec":"h264","AudioCodec":"aac","Protocol":"hls","Context":"Streaming"}],"CodecProfiles":[],"SubtitleProfiles":[]}}'
play_transcode "Jellyfin Android TV" "Bedroom TV" "$plain" "$capped" 20 1000000

echo "== Transcoder too slow: the 4K HEVC clip with the server held to half a CPU"
docker update --cpus 0.5 "$container" >/dev/null
transcode_all='{"DeviceProfile":{"MaxStreamingBitrate":120000000,"DirectPlayProfiles":[],"TranscodingProfiles":[{"Container":"ts","Type":"Video","VideoCodec":"h264","AudioCodec":"aac","Protocol":"hls","Context":"Streaming"}],"CodecProfiles":[],"SubtitleProfiles":[]}}'
play_transcode "Jellyfin Roku" "Den TV" "$heavy" "$transcode_all" 45 120000000
docker update --cpus 0 "$container" >/dev/null 2>&1 || true

echo "== the metrics count the findings"
sleep 3
metrics="$(curl -sS --fail-with-body "$api/MediaReports/Metrics" -H "Authorization: $(auth smoke box "$admin")")"
grep '^media_reports_suspected' <<<"$metrics" || true
for kind in freezing low-quality slow-transcode; do
  grep -Eq "^media_reports_suspected_problems_recent\{kind=\"$kind\"\} [1-9][0-9]*$" <<<"$metrics" || fail "the metrics don't count a recent $kind finding"
done
grep -q '^media_reports_suspected_problems_total{kind="freezing",client="Jellyfin Roku"} 1$' <<<"$metrics" || fail "the Freezing finding is not counted for Jellyfin Roku"

echo "== what was stored"
docker stop -t 30 "$container" >/dev/null
db="$work/results/media-reports.db"
rm -f "$db" "$db-wal"
MSYS_NO_PATHCONV=1 docker cp "$container:/config/data/media-reports/media-reports.db" "$(host_path "$db")"
MSYS_NO_PATHCONV=1 docker cp "$container:/config/data/media-reports/media-reports.db-wal" "$(host_path "$db-wal")" 2>/dev/null || true
py - "$db" <<'PY' || fail "the stored findings are wrong"
import re, sqlite3, sys
db = sqlite3.connect(sys.argv[1])
db.row_factory = sqlite3.Row
rows = {r["kind"]: dict(r) for r in db.execute("SELECT * FROM finding")}
for kind, r in rows.items():
    print("%-15s %-20s %-14s %s" % (kind, r["client"], r["device"], r["detail"]))
assert re.match(r"^Position jumped back \d+ s, \d+ times in \d+ minutes?$", rows["freezing"]["detail"]), rows.get("freezing")
assert rows["freezing"]["client"] == "Jellyfin Roku" and rows["freezing"]["device"] == "Living Room TV" and rows["freezing"]["title"]
assert re.match(r"^Sent at [\d.]+ Mbps( \d+p)? from \d+ Mbps 1080p \(bitrate limit\)$", rows["low-quality"]["detail"]), rows.get("low-quality")
assert re.match(r"^Transcoding at [\d.]+ fps against 24 fps for \d+ s$", rows["slow-transcode"]["detail"]), rows.get("slow-transcode")
assert db.execute("SELECT COUNT(*) FROM finding WHERE kind = 'freezing'").fetchone()[0] == 1
# Jellyfin reports a transcode as Direct play until its job starts; that start is no playback error.
assert "playback-error" not in rows, rows.get("playback-error")
for r in rows.values():
    for value in r.values():
        assert not (isinstance(value, str) and ("/media/" in value or ".mkv" in value)), r
assert db.execute("SELECT COUNT(*) FROM report").fetchone()[0] == 0
assert db.execute("SELECT COUNT(*) FROM issue").fetchone()[0] == 0
print("stored: Freezing, Low quality and Transcoder too slow, with their words; no path; no report or issue")
PY

echo "PASS: the plugin notices suspected problems on $image"
