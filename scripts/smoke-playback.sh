#!/usr/bin/env bash
# Starts a real Jellyfin 12 server in Docker with the plugin and a generated movie, plays
# it through the API the way a client does with a bitrate cap that forces a transcode, and
# checks what GET /MediaReports/Playbacks returns during playback and just after the stop.
# Also checks that the endpoint refuses anonymous calls and shows other users nothing,
# and that the retention window defaults to 24 hours and applies when changed.
#
# Usage: scripts/smoke-playback.sh [--without-plugin] [--direct-play]
#   --without-plugin  start the server with no plugin; the script must fail
#   --direct-play     play without the bitrate cap; the transcode checks must fail
#
# Environment:
#   JELLYFIN_IMAGE  image to run (default jellyfin/jellyfin:12)
#   SMOKE_TIMEOUT   seconds to wait for each step (default 180)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

image="${JELLYFIN_IMAGE:-jellyfin/jellyfin:12}"
timeout="${SMOKE_TIMEOUT:-180}"
with_plugin=1
direct_play=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --without-plugin) with_plugin=0; shift ;;
    --direct-play) direct_play=1; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

host_path() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else echo "$1"; fi
}

work="$repo_root/artifacts/smoke-playback"
meta="src/Jellyfin.Plugin.MediaReports/meta.json"
plugin_version="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$meta")"
plugin_id="$(sed -n 's/.*"guid": *"\([^"]*\)".*/\1/p' "$meta")"
container="media-reports-playback-$$"
device_id="media-reports-smoke"
auth="MediaBrowser Client=\"media-reports-smoke\", Device=\"smoke-script\", DeviceId=\"$device_id\", Version=\"1.0.0\""
token=""

trap 'docker rm -f "$container" >/dev/null 2>&1 || true' EXIT

echo "== dotnet publish"
rm -rf "$work/publish" "$work/stage"
dotnet publish src/Jellyfin.Plugin.MediaReports --nologo -c Release -o "$work/publish" >/dev/null
mkdir -p "$work/stage/plugins/MediaReports_${plugin_version}"
cp -R "$work/publish/." "$work/stage/plugins/MediaReports_${plugin_version}/"

docker pull --quiet "$image" >/dev/null
media="$work/media"
clip="$media/Test Movie (2026)/Test Movie (2026).mkv"
if [[ ! -s "$clip" ]]; then
  echo "== generate a 1-minute 1080p 8 Mbps H.264/AAC clip"
  mkdir -p "$(dirname "$clip")"
  MSYS_NO_PATHCONV=1 docker run --rm -v "$(host_path "$media"):/out" \
    --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg "$image" -hide_banner -loglevel error \
    -f lavfi -i testsrc2=size=1920x1080:rate=24 -f lavfi -i sine=frequency=440:sample_rate=48000 \
    -t 60 -c:v libx264 -preset veryfast -b:v 8M -maxrate 8M -bufsize 16M -pix_fmt yuv420p \
    -c:a aac -b:a 192k -ac 2 "/out/Test Movie (2026)/Test Movie (2026).mkv"
fi

echo "== start $image (plugin installed: $with_plugin)"
MSYS_NO_PATHCONV=1 docker create --name "$container" -p 127.0.0.1::8096 \
  -v "$(host_path "$media"):/media/movies:ro" "$image" >/dev/null
if [[ $with_plugin -eq 1 ]]; then
  docker cp "$work/stage/plugins" "$container:/config/"
fi
docker start "$container" >/dev/null
port="$(docker port "$container" 8096/tcp | head -n 1 | sed 's/.*://')"
api="http://127.0.0.1:$port"

deadline=$((SECONDS + timeout))
until docker logs "$container" 2>&1 | grep -q "Startup complete"; do
  (( SECONDS < deadline )) || { docker logs --tail 50 "$container" 2>&1; fail "the server did not start"; }
  sleep 2
done
server_version="$(docker logs "$container" 2>&1 | sed -n 's/.*Jellyfin version: \([0-9.]*\).*/\1/p' | head -n 1)"
echo "server: $image, Jellyfin ${server_version:-unknown}"

call() {
  local method="$1" path="$2" body="${3:-}"
  if ! curl -sS --fail-with-body -X "$method" "$api$path" \
    -H "Authorization: $auth${token:+, Token=\"$token\"}" \
    -H "Content-Type: application/json" \
    ${body:+--data "$body"}; then
    echo >&2
    fail "$method $path was refused"
  fi
}

# Reads a value from JSON on stdin with a Python expression over `d`.
json() {
  python3 -c "import json,sys; d=json.load(sys.stdin); v=$1; print('' if v is None else v)" 2>/dev/null \
    || python -c "import json,sys; d=json.load(sys.stdin); v=$1; print('' if v is None else v)"
}

echo "== complete the startup wizard, sign in and add a second user"
call POST /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}'
call GET /Startup/User >/dev/null
call POST /Startup/User '{"Name":"viewer","Password":"viewer-password"}'
call POST /Startup/Complete
signin="$(call POST /Users/AuthenticateByName '{"Username":"viewer","Pw":"viewer-password"}')"
token="$(json "d['AccessToken']" <<<"$signin")"
user_id="$(json "d['User']['Id']" <<<"$signin")"
[[ -n "$token" && -n "$user_id" ]] || fail "could not sign in"
call POST /Users/New '{"Name":"other","Password":"other-password"}' >/dev/null

echo "== add the movie library and wait for the item"
call POST "/Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=true" \
  '{"LibraryOptions":{"PathInfos":[{"Path":"/media/movies"}]}}'
item_id=""
deadline=$((SECONDS + timeout))
while [[ -z "$item_id" ]]; do
  (( SECONDS < deadline )) || fail "no movie appeared in the library"
  item_id="$(call GET "/Items?userId=$user_id&IncludeItemTypes=Movie&Recursive=true&Limit=1" |
    json "d['Items'][0]['Id'] if d['Items'] else None")"
  if [[ -z "$item_id" ]]; then
    # A library added during the startup scan can be skipped; ask for a refresh until it shows.
    call POST /Library/Refresh
    sleep 10
  fi
done

if [[ $direct_play -eq 1 ]]; then max_bitrate=200000000; else max_bitrate=1000000; fi
profile='{"DeviceProfile":{"MaxStreamingBitrate":'$max_bitrate',"DirectPlayProfiles":[{"Container":"mkv,mp4","Type":"Video","VideoCodec":"h264","AudioCodec":"aac"}],"TranscodingProfiles":[{"Container":"ts","Type":"Video","VideoCodec":"h264","AudioCodec":"aac","Protocol":"hls","Context":"Streaming"}],"CodecProfiles":[],"SubtitleProfiles":[]}}'

echo "== start playback (MaxStreamingBitrate $max_bitrate)"
info="$(call POST "/Items/$item_id/PlaybackInfo?userId=$user_id&maxStreamingBitrate=$max_bitrate&autoOpenLiveStream=false" "$profile")"
play_session="$(json "d['PlaySessionId']" <<<"$info")"
source_id="$(json "d['MediaSources'][0]['Id']" <<<"$info")"
transcode_url="$(json "d['MediaSources'][0].get('TranscodingUrl')" <<<"$info")"
play_method="DirectPlay"
if [[ -n "$transcode_url" ]]; then
  play_method="Transcode"
  master="$(call GET "$transcode_url")"
  variant="$(grep -v '^#' <<<"$master" | head -n 1)"
  playlist="$(call GET "$(dirname "${transcode_url%%\?*}")/$variant")"
  segment="$(grep -v '^#' <<<"$playlist" | head -n 1)"
  # The first segment can be refused while the transcode job is still starting; retry briefly.
  segment_url="$api$(dirname "${transcode_url%%\?*}")/$segment"
  code=""
  for _ in 1 2 3 4 5 6 7 8 9 10; do
    code="$(curl -sS -o /dev/null -w '%{http_code}' "$segment_url" -H "Authorization: $auth, Token=\"$token\"")"
    [[ "$code" == 200 ]] && break
    sleep 2
  done
  [[ "$code" == 200 ]] || fail "the first transcoded segment was refused ($code)"
fi
echo "server chose: $play_method"

state() {
  echo "{\"ItemId\":\"$item_id\",\"MediaSourceId\":\"$source_id\",\"PlaySessionId\":\"$play_session\",\"PlayMethod\":\"$play_method\",\"PositionTicks\":$1,\"CanSeek\":true}"
}

call POST /Sessions/Playing "$(state 0)"
for position in 50000000 100000000 150000000; do
  sleep 3
  call POST /Sessions/Playing/Progress "$(state $position)"
done
mkdir -p "$work/results"
call GET /MediaReports/Playbacks >"$work/results/during.json"

echo "== stop playback"
call POST /Sessions/Playing/Stopped "$(state 600000000)"
curl -sS -o /dev/null -X DELETE "$api/Videos/ActiveEncodings?deviceId=$device_id&playSessionId=$play_session" \
  -H "Authorization: $auth, Token=\"$token\"" || true
stopped_at=$SECONDS
call GET /MediaReports/Playbacks >"$work/results/after.json"
after_seconds=$((SECONDS - stopped_at))

echo "== access checks"
anonymous="$(curl -sS -o /dev/null -w '%{http_code}' "$api/MediaReports/Playbacks")"
other_signin="$(call POST /Users/AuthenticateByName '{"Username":"other","Pw":"other-password"}')"
other_token="$(json "d['AccessToken']" <<<"$other_signin")"
curl -sS --fail-with-body "$api/MediaReports/Playbacks" \
  -H "Authorization: MediaBrowser Client=\"media-reports-smoke\", Device=\"other\", DeviceId=\"media-reports-other\", Version=\"1.0.0\", Token=\"$other_token\"" \
  >"$work/results/other.json" || fail "GET /MediaReports/Playbacks was refused for the second user"

echo "== retention window"
call GET "/Plugins/$plugin_id/Configuration" >"$work/results/config.json"
config="$(python3 -c "import json,sys; d=json.load(open(sys.argv[1])); d['PlaybackRetentionHours']=0; print(json.dumps(d))" "$work/results/config.json" 2>/dev/null ||
  python -c "import json,sys; d=json.load(open(sys.argv[1])); d['PlaybackRetentionHours']=0; print(json.dumps(d))" "$work/results/config.json")"
call POST "/Plugins/$plugin_id/Configuration" "$config"
call GET /MediaReports/Playbacks >"$work/results/after-retention.json"

echo "== results (Jellyfin ${server_version:-unknown}, read ${after_seconds}s after the stop)"
py="$(command -v python3 || command -v python)"
"$py" - "$work/results" "$after_seconds" "$anonymous" <<'PY'
import json, os, sys

results, after_seconds, anonymous = sys.argv[1], int(sys.argv[2]), sys.argv[3]
load = lambda name: json.load(open(os.path.join(results, name)))
problems = []

def get(d, *path):
    for key in path:
        if not isinstance(d, dict):
            return None
        match = next((k for k in d if k.lower() == key.lower()), None)
        d = d.get(match) if match else None
    return d

def check(label, entries, state):
    if not isinstance(entries, list) or len(entries) != 1:
        problems.append(f"{label}: expected one playback, got {entries!r}"[:300])
        return
    e = entries[0]
    print(f"{label}: state={get(e,'State')} client={get(e,'Client')} {get(e,'ClientVersion')} "
          f"device={get(e,'DeviceName')} method={get(e,'PlayMethod')} position={get(e,'PositionTicks')} "
          f"reasons={get(e,'Transcoding','TranscodeReasons')} hw={get(e,'Transcoding','HardwareAccelerationType')} "
          f"streams={[get(s,'Type') for s in get(e,'SourceStreams') or []]}")
    if str(get(e, "State")) not in (state, {"Playing": "0", "Stopped": "1"}[state]):
        problems.append(f"{label}: state is {get(e, 'State')}, expected {state}")
    for path in [("Client",), ("ClientVersion",), ("DeviceName",), ("PositionTicks",),
                 ("Transcoding", "TranscodeReasons"), ("Transcoding", "VideoCodec"),
                 ("Transcoding", "AudioCodec"), ("Transcoding", "HardwareAccelerationType")]:
        if get(e, *path) in (None, "", [], 0):
            problems.append(f"{label}: {'/'.join(path)} is missing")
    if get(e, "PlayMethod") != "Transcode":
        problems.append(f"{label}: play method is {get(e, 'PlayMethod')}, expected Transcode")
    types = {get(s, "Type") for s in get(e, "SourceStreams") or []}
    if not {"Video", "Audio"} <= types:
        problems.append(f"{label}: source streams are {sorted(map(str, types))}")

check("during", load("during.json"), "Playing")
check("after stop", load("after.json"), "Stopped")
if after_seconds > 10:
    problems.append(f"after stop: read {after_seconds}s after the stop, more than 10s")
if anonymous != "401":
    problems.append(f"anonymous call returned {anonymous}, expected 401")
if load("other.json") != []:
    problems.append("the second user was shown playbacks that are not theirs")
if get(load("config.json"), "PlaybackRetentionHours") != 24:
    problems.append(f"default retention is {get(load('config.json'), 'PlaybackRetentionHours')}, expected 24")
if load("after-retention.json") != []:
    problems.append("the stopped playback is still returned after the retention window was set to 0")

for p in problems:
    print(f"FAIL: {p}", file=sys.stderr)
sys.exit(1 if problems else 0)
PY
echo "PASS: playback diagnostics served during a forced transcode and ${after_seconds}s after the stop on Jellyfin ${server_version:-unknown}"
