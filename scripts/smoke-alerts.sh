#!/usr/bin/env bash
# Starts a real Jellyfin 12 server in Docker with the plugin, a movie and a four-episode
# series, and a small receiver container on the same Docker network that records every
# webhook request and answers by path. Then checks the admin's report alerts: reports
# filed within a minute arrive as one Discord and one generic message with the expected
# fields and nothing private, each report is in Jellyfin's activity log, no webhook sends
# nothing, a failing webhook is retried once after 30 seconds, and the test alert says per
# address whether it was accepted.
#
# Usage: scripts/smoke-alerts.sh
#
# Environment:
#   JELLYFIN_IMAGE  image to run (default jellyfin/jellyfin:12)
#   SMOKE_TIMEOUT   seconds to wait for each step (default 180)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

image="${JELLYFIN_IMAGE:-jellyfin/jellyfin:12}"
timeout="${SMOKE_TIMEOUT:-180}"
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
  py -c "import json,sys; d=json.load(sys.stdin); v=$1; print('' if v is None else v)" | tr -d '\r'
}

work="$repo_root/artifacts/smoke-alerts"
meta="src/Jellyfin.Plugin.MediaReports/meta.json"
plugin_version="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$meta")"
container="media-reports-alerts-$$"
receiver="media-reports-receiver-$$"
network="media-reports-alerts-$$"

cleanup() {
  docker rm -f "$container" "$receiver" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "== dotnet publish"
rm -rf "$work/publish" "$work/stage" "$work/results"
mkdir -p "$work/results"
dotnet publish src/Jellyfin.Plugin.MediaReports --nologo -c Release -o "$work/publish" >/dev/null
mkdir -p "$work/stage/plugins/MediaReports_${plugin_version}"
cp -R "$work/publish/." "$work/stage/plugins/MediaReports_${plugin_version}/"

docker pull --quiet "$image" >/dev/null
cache="$work/media"
clip() {  # clip <path under the cache> <seconds>
  local out="$cache/$1"
  [[ -s "$out" ]] && return 0
  mkdir -p "$(dirname "$out")"
  MSYS_NO_PATHCONV=1 docker run --rm -v "$(host_path "$cache"):/out" \
    --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg "$image" -hide_banner -loglevel error \
    -f lavfi -i testsrc2=size=1280x720:rate=24 -f lavfi -i sine=frequency=440:sample_rate=48000 \
    -t "$2" -c:v libx264 -preset veryfast -b:v 2M -pix_fmt yuv420p -c:a aac -b:a 128k -ac 2 "/out/$1"
}
echo "== generate a movie and a four-episode series"
clip "movies/Test Movie (2026)/Test Movie (2026).mkv" 20
for e in 1 2 3 4; do clip "shows/Test Series (2026)/Season 01/Test Series (2026) S01E0$e.mkv" 5; done

echo "== start the webhook receiver"
docker network create "$network" >/dev/null
# Records each POST as one JSON line with its path, body and arrival time; answers by path.
receiver_script='
import json, time
from http.server import BaseHTTPRequestHandler, HTTPServer
STATUS = {"/discord": 204, "/generic": 200, "/broken": 500, "/missing": 404}
class H(BaseHTTPRequestHandler):
    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length") or 0)).decode("utf-8")
        with open("/tmp/hits.jsonl", "a", encoding="utf-8") as f:
            f.write(json.dumps({"path": self.path, "type": self.headers.get("Content-Type"), "body": body, "t": time.time()}) + "\n")
        self.send_response(STATUS.get(self.path, 404))
        self.end_headers()
    def log_message(self, *a):
        pass
open("/tmp/hits.jsonl", "a").close()
HTTPServer(("", 8000), H).serve_forever()
'
MSYS_NO_PATHCONV=1 docker run -d --name "$receiver" --network "$network" --network-alias receiver python:3.12-slim python -c "$receiver_script" >/dev/null
hook="http://receiver:8000"

# hits <path>: the requests the receiver recorded for a path, as a JSON list.
hits() {
  MSYS_NO_PATHCONV=1 docker exec "$receiver" cat /tmp/hits.jsonl | MSYS_NO_PATHCONV=1 py -c "
import json, sys
print(json.dumps([h for h in map(json.loads, filter(None, sys.stdin.read().splitlines())) if h['path'] == sys.argv[1]]))" "$1"
}
count() { hits "$1" | json "len(d)"; }
# wait_hits <path> <count> <seconds>: waits until the receiver has that many requests for a path.
wait_hits() {
  local deadline=$((SECONDS + $3))
  until (( $(count "$1") >= $2 )); do
    if (( SECONDS >= deadline )); then
      docker logs "$container" 2>&1 | grep -E -A6 'ReportAlerts|webhook|alert|\[ERR\]|\[WRN\]' | tail -40 >&2 || true
      docker logs --tail 20 "$receiver" >&2 2>&1 || true
      fail "the receiver got $(count "$1") request(s) at $1 within $3 s, not $2"
    fi
    sleep 2
  done
}

echo "== start $image with the plugin"
MSYS_NO_PATHCONV=1 docker create --name "$container" --network "$network" -p 127.0.0.1::8096 \
  -v "$(host_path "$cache/movies"):/media/movies:ro" -v "$(host_path "$cache/shows"):/media/shows:ro" "$image" >/dev/null
docker cp "$work/stage/plugins" "$container:/config/"
docker start "$container" >/dev/null
port="$(docker port "$container" 8096/tcp | head -n 1 | sed 's/.*://')"
api="http://127.0.0.1:$port"
deadline=$((SECONDS + timeout))
until docker logs "$container" 2>&1 | grep "Startup complete" >/dev/null; do
  (( SECONDS < deadline )) || { docker logs --tail 50 "$container" 2>&1; fail "the server did not start"; }
  sleep 2
done

auth() {
  echo "MediaBrowser Client=\"media-reports-smoke\", Device=\"$1\", DeviceId=\"smoke-$(tr ' ' '-' <<<"$1")\", Version=\"1.0.0\"${2:+, Token=\"$2\"}"
}

# ok <device> <token> <method> <path> [body]: prints the body; fails unless the status is 2xx.
ok() {
  local device="$1" tok="$2" method="$3" path="$4" body="${5:-}" code
  local headers=(-H "Content-Type: application/json")
  [[ -n "$device" ]] && headers+=(-H "Authorization: $(auth "$device" "$tok")")
  code="$(curl -sS -o "$work/results/last.json" -w '%{http_code}' -X "$method" "$api$path" "${headers[@]}" ${body:+--data "$body"})"
  [[ "$code" == 2* ]] || { cat "$work/results/last.json" >&2; echo >&2; fail "$method $path answered $code"; }
  cat "$work/results/last.json"
}

# check <file> <python assertions using d>: fails with the message the assertions raise.
check() {
  local file="$1"; shift
  py - "$file" "$1" <<'PY' || fail "$2"
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
exec(sys.argv[2])
PY
}

echo "== complete the startup wizard and add two users"
ok "admin box" "" POST /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null
ok "admin box" "" GET /Startup/User >/dev/null
ok "admin box" "" POST /Startup/User '{"Name":"admin","Password":"admin-password"}' >/dev/null
ok "admin box" "" POST /Startup/Complete >/dev/null
admin="$(ok "admin box" "" POST /Users/AuthenticateByName '{"Username":"admin","Pw":"admin-password"}' | json "d['AccessToken']")"
ok "admin box" "$admin" POST /Users/New '{"Name":"alice","Password":"alice-password"}' >/dev/null
ok "admin box" "$admin" POST /Users/New '{"Name":"bob","Password":"bob-password"}' >/dev/null
sign_in() { ok "$1" "" POST /Users/AuthenticateByName "{\"Username\":\"$2\",\"Pw\":\"$2-password\"}" | json "d['AccessToken']"; }
alice="$(sign_in "Living Room TV" alice)"
bob="$(sign_in "Bob Laptop" bob)"
alice_id="$(ok "Living Room TV" "$alice" GET /Users/Me | json "d['Id']")"

echo "== add the libraries and wait for the items"
ok "admin box" "$admin" POST "/Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=false" \
  '{"LibraryOptions":{"PathInfos":[{"Path":"/media/movies"}]}}' >/dev/null
ok "admin box" "$admin" POST "/Library/VirtualFolders?name=Shows&collectionType=tvshows&refreshLibrary=true" \
  '{"LibraryOptions":{"PathInfos":[{"Path":"/media/shows"}]}}' >/dev/null
items() { ok "admin box" "$admin" GET "/Items?userId=$alice_id&IncludeItemTypes=$1&Recursive=true&SortBy=SortName"; }
deadline=$((SECONDS + timeout))
until [[ "$(items Movie | json "len(d['Items'])")" == 1 && "$(items Episode | json "len(d['Items'])")" == 4 ]]; do
  (( SECONDS < deadline )) || fail "the library did not show the movie and four episodes"
  ok "admin box" "$admin" POST /Library/Refresh >/dev/null
  sleep 10
done
movie="$(items Movie | json "d['Items'][0]['Id']")"
movie_name="$(items Movie | json "d['Items'][0]['Name']")"
mapfile -t episodes < <(items Episode | json "'\n'.join(i['Id'] for i in d['Items'])")

settings() { ok "admin box" "$admin" PUT /MediaReports/Admin/Settings "$1" >/dev/null; }
file() { ok "$1" "$2" POST /MediaReports/Report "{\"itemId\":\"$3\",\"reasonCode\":\"$4\"${5:+,\"note\":\"$5\"}}" >/dev/null; }
activity() { ok "admin box" "$admin" GET "/System/ActivityLog/Entries?limit=100" >"$work/results/activity.json"; }

echo "== three reports within a minute: one message per webhook"
settings "{\"DiscordWebhookUrl\":\"$hook/discord\",\"WebhookUrl\":\"$hook/generic\"}"
state() { echo "{\"ItemId\":\"$movie\",\"PlaySessionId\":\"alerts-play\",\"PlayMethod\":\"DirectPlay\",\"PositionTicks\":$1,\"CanSeek\":true}"; }
ok "Living Room TV" "$alice" POST /Sessions/Playing "$(state 0)" >/dev/null
ok "Living Room TV" "$alice" POST /Sessions/Playing/Stopped "$(state 100000000)" >/dev/null
long_note="$(printf 'The picture keeps jumping back a few frames every second. %.0s' {1..8})"
first=$(date +%s)
file "Living Room TV" "$alice" "$movie" stutter "$long_note"
sleep 5
file "Bob Laptop" "$bob" "${episodes[0]}" audio
sleep 5
file "Bob Laptop" "$bob" "${episodes[1]}" subtitles
wait_hits /discord 1 90
wait_hits /generic 1 10
sleep 5
[[ "$(count /discord)" == 1 && "$(count /generic)" == 1 ]] || fail "the three reports did not arrive as one message per webhook"
hits /discord >"$work/results/discord.json"
hits /generic >"$work/results/generic.json"
check "$work/results/discord.json" "
h = d[0]; body = json.loads(h['body'])
waited = h['t'] - $first
assert 45 <= waited <= 80, waited
assert h['type'].startswith('application/json'), h['type']
assert body['username'] == 'Media Reports' and body['content'] == '3 new reports', body['content']
e = body['embeds']
assert [x['title'] for x in e] == ['$movie_name', 'Test Series S01E01', 'Test Series S01E02'], [x['title'] for x in e]
f = {x['name']: x['value'] for x in e[0]['fields']}
assert f['Reason'] == 'Stutter or jitter' and f['Reported by'] == 'alice' and 'Living Room TV' in f['Device'], f
assert f['How it played'] in ('Direct play', 'Direct stream') or f['How it played'].startswith('Transcode'), f
assert len(e[0]['description']) == 300 and e[0]['description'][-1] == chr(0x2026), len(e[0]['description'])
assert 'configurationpage?name=mediareports&issue=' in e[0]['url'], e[0]['url']
assert '/media/' not in h['body'] and '.mkv' not in h['body'], 'a file path left the server'
" "the Discord message does not hold the three reports as specified"
check "$work/results/generic.json" "
body = json.loads(d[0]['body'])
assert body['event'] == 'reports.filed' and body['sentAt'], body
r = body['reports']
assert [x['reason'] for x in r] == ['Stutter or jitter', 'Audio problem', 'Subtitle problem'], [x['reason'] for x in r]
assert r[0]['itemId'] and r[0]['issueId'] and r[0]['user'] == 'alice' and r[0]['playMethod'] in ('DirectPlay', 'DirectStream', 'Transcode'), r[0]
assert len(r[0]['note']) == 300 and 'issue=' in r[0]['link'], r[0]
assert '/media/' not in d[0]['body'] and '.mkv' not in d[0]['body'], 'a file path left the server'
" "the generic message does not hold the three reports as specified"
echo "three reports arrived as one Discord and one generic message, $(( $(hits /discord | json "int(d[0]['t'])") - first )) s after the first"

activity
check "$work/results/activity.json" "
mine = [e for e in d['Items'] if e['Type'] == 'MediaReportsReportFiled']
assert len(mine) == 3, [e['Name'] for e in mine]
assert any(e['Name'] == 'alice reported $movie_name: Stutter or jitter' and e['ItemId'] for e in mine), [e['Name'] for e in mine]
" "the activity log does not list the three reports"
echo "the activity log lists the three reports"

echo "== no webhook set: nothing sent, still in the activity log"
settings '{"DiscordWebhookUrl":"","WebhookUrl":""}'
before=$(( $(count /discord) + $(count /generic) ))
file "Bob Laptop" "$bob" "${episodes[2]}" other
sleep 75
(( $(count /discord) + $(count /generic) == before )) || fail "a report was sent with no webhook set"
activity
check "$work/results/activity.json" "assert len([e for e in d['Items'] if e['Type'] == 'MediaReportsReportFiled']) == 4" "the report with no webhook set is not in the activity log"
echo "no webhook set: nothing sent, the activity log has the report"

echo "== a failing webhook: retried once after 30 seconds"
settings "{\"WebhookUrl\":\"$hook/broken\"}"
file "Bob Laptop" "$bob" "${episodes[3]}" metadata
wait_hits /broken 1 90
wait_hits /broken 2 45
sleep 40
hits /broken >"$work/results/broken.json"
check "$work/results/broken.json" "
assert len(d) == 2, len(d)
gap = d[1]['t'] - d[0]['t']
assert 28 <= gap <= 40, gap
" "a failing webhook was not retried exactly once after 30 seconds"
docker logs "$container" 2>&1 | grep -q "webhook didn't take the report alert again" || fail "the dropped alert was not logged"
ok "admin box" "$admin" GET /MediaReports/Admin/Settings | json "d['WebhookUrl']" | grep -x "$hook/broken" >/dev/null || fail "the settings changed"
echo "a webhook answering 500: one retry about 30 s later, then dropped with a warning"

echo "== the test alert"
ok "admin box" "$admin" POST /MediaReports/Admin/Settings/TestAlert "{\"DiscordWebhookUrl\":\"$hook/discord\",\"WebhookUrl\":\"$hook/missing\"}" >"$work/results/test.json"
check "$work/results/test.json" "
got = [(r['Field'], r['Accepted'], r['Status']) for r in d]
assert got == [('discordWebhookUrl', True, 204), ('webhookUrl', False, 404)], got
" "the test alert does not say which address accepted it"
hits /discord | json "d[-1]['body']" | grep -q "Sample Movie (2026)" || fail "the test alert did not reach the Discord address"
ok "admin box" "$admin" GET /MediaReports/Admin/Settings | json "(d['DiscordWebhookUrl'], d['WebhookUrl'])" | grep -x "('', '$hook/broken')" >/dev/null \
  || fail "the test alert changed the settings"
echo "test alert: Discord accepted, the other address failed with 404, settings unchanged"

echo "PASS: report alerts on $image"
