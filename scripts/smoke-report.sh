#!/usr/bin/env bash
# Starts a real Jellyfin 12 server in Docker with the plugin and a generated movie, plays it
# as one user on a "TV", then files reports through the report API from that user's
# "phone" and from a second user. Checks the context the API describes, the playback it
# attaches, grouping into one issue, every refusal, retired reasons and the reporting switch.
# Then lists, opens and withdraws the users' own reports, closes an issue as the admin, and
# reads the notice that arrives on the reporter's websocket.
#
# Usage: scripts/smoke-report.sh [--without-plugin]
#   --without-plugin  start the server with no plugin; the script must fail
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
while [[ $# -gt 0 ]]; do
  case "$1" in
    --without-plugin) with_plugin=0; shift ;;
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

py() {
  if command -v python3 >/dev/null 2>&1 && python3 -c "" >/dev/null 2>&1; then python3 "$@"; else python "$@"; fi
}

json() {
  py -c "import json,sys; d=json.load(sys.stdin); v=$1; print('' if v is None else v)"
}

work="$repo_root/artifacts/smoke-report"
meta="src/Jellyfin.Plugin.MediaReports/meta.json"
plugin_version="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$meta")"
plugin_id="$(sed -n 's/.*"guid": *"\([^"]*\)".*/\1/p' "$meta")"
container="media-reports-report-$$"

trap 'docker rm -f "$container" >/dev/null 2>&1 || true' EXIT

echo "== dotnet publish"
rm -rf "$work/publish" "$work/stage" "$work/results"
mkdir -p "$work/results"
dotnet publish src/Jellyfin.Plugin.MediaReports --nologo -c Release -o "$work/publish" >/dev/null
mkdir -p "$work/stage/plugins/MediaReports_${plugin_version}"
cp -R "$work/publish/." "$work/stage/plugins/MediaReports_${plugin_version}/"

docker pull --quiet "$image" >/dev/null
media="$work/media"
clip="$media/Test Movie (2026)/Test Movie (2026).mkv"
if [[ ! -s "$clip" ]]; then
  echo "== generate a 1-minute H.264/AAC clip"
  mkdir -p "$(dirname "$clip")"
  MSYS_NO_PATHCONV=1 docker run --rm -v "$(host_path "$media"):/out" \
    --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg "$image" -hide_banner -loglevel error \
    -f lavfi -i testsrc2=size=1280x720:rate=24 -f lavfi -i sine=frequency=440:sample_rate=48000 \
    -t 60 -c:v libx264 -preset veryfast -b:v 2M -pix_fmt yuv420p \
    -c:a aac -b:a 128k -ac 2 "/out/Test Movie (2026)/Test Movie (2026).mkv"
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
until docker logs "$container" 2>&1 | grep "Startup complete" >/dev/null; do
  (( SECONDS < deadline )) || { docker logs --tail 50 "$container" 2>&1; fail "the server did not start"; }
  sleep 2
done

# auth <device> [token]: the MediaBrowser authorization header for a device.
auth() {
  echo "MediaBrowser Client=\"media-reports-smoke\", Device=\"$1\", DeviceId=\"smoke-$(tr ' ' '-' <<<"$1")\", Version=\"1.0.0\"${2:+, Token=\"$2\"}"
}

# req <device> <token> <method> <path> [body]: prints the HTTP status; the body goes to $work/results/last.json.
req() {
  local device="$1" tok="$2" method="$3" path="$4" body="${5:-}"
  local headers=(-H "Content-Type: application/json")
  [[ -n "$device" ]] && headers+=(-H "Authorization: $(auth "$device" "$tok")")
  curl -sS -o "$work/results/last.json" -w '%{http_code}' -X "$method" "$api$path" "${headers[@]}" ${body:+--data "$body"}
}

# ok <device> <token> <method> <path> [body]: prints the body; fails unless the status is 2xx.
ok() {
  local code
  code="$(req "$@")"
  [[ "$code" == 2* ]] || { cat "$work/results/last.json" >&2; echo >&2; fail "$3 $4 answered $code"; }
  cat "$work/results/last.json"
}

# expect <status> <error code> <device> <token> <method> <path> [body]
expect() {
  local want="$1" code_want="$2"; shift 2
  local got
  got="$(req "$@")"
  [[ "$got" == "$want" ]] || { cat "$work/results/last.json" >&2; echo >&2; fail "$3 $4 answered $got, not $want"; }
  if [[ -n "$code_want" ]]; then
    [[ "$(json "d.get('code') or d.get('Code')" <"$work/results/last.json")" == "$code_want" ]] || fail "$3 $4 did not say $code_want: $(cat "$work/results/last.json")"
  fi
  echo "$3 $4 -> $got ${code_want}"
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
alice_tv="$(sign_in "Living Room TV" alice)"
alice_phone="$(sign_in "Alice Phone" alice)"
bob="$(sign_in "Bob Laptop" bob)"
alice_id="$(ok "Alice Phone" "$alice_phone" GET /Users/Me | json "d['Id']")"

echo "== add the movie library and wait for the item"
ok "admin box" "$admin" POST "/Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=true" \
  '{"LibraryOptions":{"PathInfos":[{"Path":"/media/movies"}]}}' >/dev/null
item_id=""
deadline=$((SECONDS + timeout))
while [[ -z "$item_id" ]]; do
  (( SECONDS < deadline )) || fail "no movie appeared in the library"
  item_id="$(ok "admin box" "$admin" GET "/Items?userId=$alice_id&IncludeItemTypes=Movie&Recursive=true&Limit=1" |
    json "d['Items'][0]['Id'] if d['Items'] else None")"
  [[ -n "$item_id" ]] || { ok "admin box" "$admin" POST /Library/Refresh >/dev/null; sleep 10; }
done

echo "== alice plays the movie on the TV and stops"
state() { echo "{\"ItemId\":\"$item_id\",\"PlaySessionId\":\"smoke-play\",\"PlayMethod\":\"DirectPlay\",\"PositionTicks\":$1,\"CanSeek\":true}"; }
ok "Living Room TV" "$alice_tv" POST /Sessions/Playing "$(state 0)" >/dev/null
sleep 2
ok "Living Room TV" "$alice_tv" POST /Sessions/Playing/Progress "$(state 100000000)" >/dev/null
ok "Living Room TV" "$alice_tv" POST /Sessions/Playing/Stopped "$(state 200000000)" >/dev/null

echo "== access"
expect 401 "" "" "" GET "/MediaReports/Report/Context?itemId=$item_id"
expect 401 "" "" "" GET /MediaReports/Report/Mine
expect 401 "" "" "" GET /MediaReports/Report/Mine/1
expect 401 "" "" "" DELETE /MediaReports/Report/Mine/1
expect 404 item-not-found "Alice Phone" "$alice_phone" GET "/MediaReports/Report/Context?itemId=$(py -c 'import uuid;print(uuid.uuid4())')"

echo "== context from alice's phone names the TV playback"
ok "Alice Phone" "$alice_phone" GET "/MediaReports/Report/Context?itemId=$item_id" >"$work/results/context.json"
py - "$work/results/context.json" <<'PY' || fail "the context is wrong"
import json, sys
d = json.load(open(sys.argv[1]))
codes = [r["Code"] for r in d["Reasons"]]
assert codes == ["wont-play", "playback-error", "poor-quality", "stutter", "audio", "subtitles", "metadata", "other"], codes
assert d["Type"] == "Movie" and d["Name"], d
p = d["Playback"]
assert p and p["DeviceName"] == "Living Room TV" and p["State"] == "Stopped", p
assert d["OpenReports"] == []
print("context: 8 reasons, playback on Living Room TV, Stopped")
PY

echo "== file reports"
ok "Alice Phone" "$alice_phone" POST /MediaReports/Report "{\"itemId\":\"$item_id\",\"reasonCode\":\"stutter\",\"note\":\"keeps jumping back\"}" >"$work/results/alice.json"
[[ "$(req "Alice Phone" "$alice_phone" GET "/MediaReports/Report/Context?itemId=$item_id")" == 200 ]] || fail "context after filing"
cp "$work/results/last.json" "$work/results/context-after.json"
expect 409 already-reported "Alice Phone" "$alice_phone" POST /MediaReports/Report "{\"itemId\":\"$item_id\",\"reasonCode\":\"stutter\"}"
expect 400 reason-not-allowed "Alice Phone" "$alice_phone" POST /MediaReports/Report "{\"itemId\":\"$item_id\",\"reasonCode\":\"nonsense\"}"
long="$(py -c 'print("x" * 1001)')"
expect 400 note-too-long "Alice Phone" "$alice_phone" POST /MediaReports/Report "{\"itemId\":\"$item_id\",\"reasonCode\":\"audio\",\"note\":\"$long\"}"
expect 404 item-not-found "Alice Phone" "$alice_phone" POST /MediaReports/Report "{\"itemId\":\"$(py -c 'import uuid;print(uuid.uuid4())')\",\"reasonCode\":\"audio\"}"
ok "Bob Laptop" "$bob" POST /MediaReports/Report "{\"itemId\":\"$item_id\",\"reasonCode\":\"stutter\"}" >"$work/results/bob.json"

echo "== retire a reason and switch reporting off and on, without a restart"
ok "admin box" "$admin" GET "/Plugins/$plugin_id/Configuration" >"$work/results/config.json"
set_config() {
  local cfg
  cfg="$(py -c "
import json, sys
d = json.load(open(sys.argv[1]))
d['ReportingEnabled'] = sys.argv[2] == 'on'
d['Reasons'] = [dict(r, Retired=(r['Code'] == 'metadata')) for r in d['Reasons']]
print(json.dumps(d))" "$work/results/config.json" "$1")"
  ok "admin box" "$admin" POST "/Plugins/$plugin_id/Configuration" "$cfg" >/dev/null
}
set_config on
ok "Alice Phone" "$alice_phone" GET "/MediaReports/Report/Context?itemId=$item_id" | json "'metadata' in [r['Code'] for r in d['Reasons']]" | grep -x False >/dev/null \
  || fail "a retired reason is still offered"
expect 400 reason-not-allowed "Alice Phone" "$alice_phone" POST /MediaReports/Report "{\"itemId\":\"$item_id\",\"reasonCode\":\"metadata\"}"
set_config off
expect 403 reporting-off "Alice Phone" "$alice_phone" POST /MediaReports/Report "{\"itemId\":\"$item_id\",\"reasonCode\":\"audio\"}"
expect 403 reporting-off "Alice Phone" "$alice_phone" GET "/MediaReports/Report/Context?itemId=$item_id"
alice_report="$(json "d['Id']" <"$work/results/alice.json")"
ok "Alice Phone" "$alice_phone" GET /MediaReports/Report/Mine | json "len(d['Reports'])" | grep -x 1 >/dev/null \
  || fail "with reporting off, Mine did not list alice's report"
expect 403 reporting-off "Alice Phone" "$alice_phone" DELETE "/MediaReports/Report/Mine/$alice_report"
set_config on
ok "Alice Phone" "$alice_phone" POST /MediaReports/Report "{\"itemId\":\"$item_id\",\"reasonCode\":\"audio\"}" >/dev/null
echo "reporting off refused, on again accepted"

echo "== what was stored"
ok "admin box" "$admin" GET /MediaReports/Admin/Export >"$work/results/export.json"
py - "$work/results" <<'PY' || fail "the stored reports are wrong"
import json, os, sys
r = sys.argv[1]
load = lambda n: json.load(open(os.path.join(r, n)))
alice, bob, after, export = load("alice.json"), load("bob.json"), load("context-after.json"), load("export.json")
assert alice["IssueId"] == bob["IssueId"], (alice, bob)
assert alice["Status"] == "New" and alice["Playback"]["DeviceName"] == "Living Room TV"
assert [o["ReasonCode"] for o in after["OpenReports"]] == ["stutter"], after["OpenReports"]
reports = export["reports"]
assert len(reports) == 3, len(reports)
mine = next(x for x in reports if x["id"] == alice["Id"])
assert mine["note"] == "keeps jumping back" and mine["reporterName"] == "alice"
assert mine["snapshot"]["state"] == "Stopped" and mine["snapshot"]["deviceName"] == "Living Room TV", mine["snapshot"]
assert mine["filePath"].endswith(".mkv") and mine["fileSize"] > 0
for name in ("context.json", "context-after.json", "alice.json"):
    text = open(os.path.join(r, name)).read().lower()
    assert "/media/" not in text and ".mkv" not in text and "bob" not in text, name
print("stored: 3 reports, alice and bob in one issue, snapshot from the TV; no path or other user in alice's answers")
PY

echo "== alice's phone connects its websocket the way an app does"
ok "Alice Phone" "$alice_phone" POST /Sessions/Capabilities/Full \
  '{"PlayableMediaTypes":["Video"],"SupportedCommands":["DisplayMessage"],"SupportsMediaControl":true}' >/dev/null
rm -f "$work/results/ready" "$work/results/notices.json"
# A minimal RFC 6455 client, since the standard library has none. It records every DisplayMessage.
py - "$port" "$(auth "Alice Phone" "$alice_phone")" "$work/results" <<'PY' &
import base64, json, os, socket, struct, sys, time
port, authorization, out = sys.argv[1:4]
s = socket.create_connection(("127.0.0.1", int(port)), timeout=20)
key = base64.b64encode(os.urandom(16)).decode()
s.sendall((f"GET /socket HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
           f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\nAuthorization: {authorization}\r\n\r\n").encode())
buf = b""
while b"\r\n\r\n" not in buf:
    chunk = s.recv(4096)
    if not chunk:
        sys.exit("the server closed the websocket handshake")
    buf += chunk
head, buf = buf.split(b"\r\n\r\n", 1)
if not head.startswith(b"HTTP/1.1 101"):
    sys.exit("websocket refused: " + head.decode(errors="replace").splitlines()[0])
open(os.path.join(out, "ready"), "w").close()
s.settimeout(1)


def take(n):
    global buf
    while len(buf) < n:
        chunk = s.recv(4096)
        if not chunk:
            raise ConnectionError("closed")
        buf += chunk
    data, buf = buf[:n], buf[n:]
    return data


def send(opcode, payload):
    mask = os.urandom(4)
    s.sendall(bytes([0x80 | opcode, 0x80 | len(payload)]) + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(payload)))


notices = []
deadline = time.time() + 240
while time.time() < deadline:
    try:
        b0, b1 = take(2)
        size = b1 & 0x7F
        if size == 126:
            size = struct.unpack(">H", take(2))[0]
        elif size == 127:
            size = struct.unpack(">Q", take(8))[0]
        payload = take(size)
    except socket.timeout:
        continue
    except ConnectionError:
        break
    opcode = b0 & 0x0F
    if opcode == 8:
        break
    if opcode == 9:
        send(10, payload)
        continue
    message = json.loads(payload)
    if message.get("MessageType") == "ForceKeepAlive":
        send(1, b'{"MessageType":"KeepAlive"}')
    if message.get("MessageType") == "GeneralCommand" and (message.get("Data") or {}).get("Name") == "DisplayMessage":
        notices.append(message["Data"]["Arguments"])
        with open(os.path.join(out, "notices.json.part"), "w") as f:
            json.dump(notices, f)
        os.replace(os.path.join(out, "notices.json.part"), os.path.join(out, "notices.json"))
PY
listener=$!
trap 'kill "$listener" 2>/dev/null || true; docker rm -f "$container" >/dev/null 2>&1 || true' EXIT
deadline=$((SECONDS + 30))
until [[ -f "$work/results/ready" ]]; do
  (( SECONDS < deadline )) || fail "the websocket did not open"
  kill -0 "$listener" 2>/dev/null || fail "the websocket listener exited"
  sleep 1
done
sleep 2
ok "admin box" "$admin" GET /Sessions >"$work/results/sessions.json"
py - "$work/results/sessions.json" <<'PY' || fail "the sessions are not what the notice check needs"
import json, sys
by_device = {s["DeviceName"]: bool(s.get("SupportsRemoteControl")) for s in json.load(open(sys.argv[1])) if s.get("UserName") == "alice"}
assert by_device.get("Alice Phone") is True and by_device.get("Living Room TV") is False, by_device
print("alice's phone takes remote control, her TV session doesn't:", by_device)
PY

echo "== my reports, and what another user can't do with them"
issue="$(json "d['IssueId']" <"$work/results/alice.json")"
ok "Alice Phone" "$alice_phone" GET /MediaReports/Report/Mine >"$work/results/mine-before.json"
audio_report="$(json "d['Reports'][0]['Id']" <"$work/results/mine-before.json")"
expect 404 report-not-found "Bob Laptop" "$bob" GET "/MediaReports/Report/Mine/$alice_report"
expect 404 report-not-found "Bob Laptop" "$bob" DELETE "/MediaReports/Report/Mine/$alice_report"
expect 404 report-not-found "Bob Laptop" "$bob" DELETE "/MediaReports/Report/Mine/$audio_report"

echo "== withdraw while New, refused once it is being looked at"
ok "admin box" "$admin" POST /MediaReports/Admin/Issues/Status "{\"issueIds\":[$issue],\"status\":\"Investigating\"}" >/dev/null
expect 409 not-new "Alice Phone" "$alice_phone" DELETE "/MediaReports/Report/Mine/$alice_report"
expect 204 "" "Alice Phone" "$alice_phone" DELETE "/MediaReports/Report/Mine/$audio_report"

echo "== the admin closes the issue; the notice reaches alice's phone"
ok "admin box" "$admin" PATCH "/MediaReports/Admin/Issues/$issue" '{"status":"Fixed","notes":"Secret admin note: re-encoded."}' >/dev/null
deadline=$((SECONDS + 30))
until [[ -f "$work/results/notices.json" ]]; do
  (( SECONDS < deadline )) || fail "no DisplayMessage arrived on alice's websocket"
  sleep 1
done
ok "admin box" "$admin" PATCH "/MediaReports/Admin/Issues/$issue" '{"status":"Duplicate"}' >/dev/null
sleep 5
ok "Alice Phone" "$alice_phone" GET /MediaReports/Report/Mine >"$work/results/mine-after.json"
ok "Alice Phone" "$alice_phone" GET "/MediaReports/Report/Mine/$alice_report" >"$work/results/mine-detail.json"
ok "Alice Phone" "$alice_phone" GET /MediaReports/Report/Mine >"$work/results/mine-read.json"
ok "Bob Laptop" "$bob" GET /MediaReports/Report/Mine >"$work/results/bob-mine.json"
py - "$work/results" <<'PY' || fail "My Reports or the notice is wrong"
import json, os, sys
r = sys.argv[1]
load = lambda n: json.load(open(os.path.join(r, n)))
title = load("context.json")["Name"]
before, after, detail, read, bob = (load(n) for n in ("mine-before.json", "mine-after.json", "mine-detail.json", "mine-read.json", "bob-mine.json"))
assert [x["ReasonLabel"] for x in before["Reports"]] == ["Audio problem", "Stutter or jitter"] and before["Unread"] == 0, before
assert len(after["Reports"]) == 1 and after["Unread"] == 1 and after["Reports"][0]["Unread"], after
assert after["Reports"][0]["StatusLabel"] == "Duplicate", after
assert [h["Label"] for h in detail["History"]] == ["Reported", "Being looked at", "Fixed", "Duplicate"], detail["History"]
assert detail["Note"] == "keeps jumping back" and detail.get("Device") == "Living Room TV" and detail["CanWithdraw"] is False, detail
assert read["Unread"] == 0 and not read["Reports"][0]["Unread"], read
assert bob["Unread"] == 1 and bob["Reports"][0]["StatusLabel"] == "Duplicate", bob
notices = load("notices.json")
assert len(notices) == 1, notices
n = notices[0]
assert n["Header"] == "Media Reports" and n["Text"] == f"Your report on {title} is Fixed." and str(n.get("TimeoutMs")) == "8000", n
for name in ("mine-before.json", "mine-after.json", "mine-detail.json", "mine-read.json", "notices.json"):
    text = open(os.path.join(r, name)).read().lower()
    for bad in ("secret", "re-encoded", "bob", "/media/", ".mkv", "remoteendpoint", "127.0.0.1", "172."):
        assert bad not in text, (name, bad)
print(f"my reports: listed, opened, withdrawn; one toast '{n['Text']}' (8000 ms); nothing private in any answer")
PY

echo "PASS: filing reports on $image"
