#!/usr/bin/env bash
# Starts a real Jellyfin 12 server in Docker with the plugin and a small library whose
# folders carry provider ids, files reports, then replaces files the way Radarr and Sonarr
# do and checks that the reports follow: a movie renamed on disk moves its issues (open to
# Replaced - verify, Fixed kept); an episode overwritten at the same path becomes Replaced -
# verify, and again after the admin moves it on; a movie replaced by two copies sharing its
# TMDb id stays put; a movie renamed while the server is stopped moves after the start, a
# scan and "Re-link media reports". Also checks the task's daily trigger and My Reports.
#
# Usage: scripts/smoke-relink.sh
#
# Environment:
#   JELLYFIN_IMAGE  image to run (default jellyfin/jellyfin:12)
#   SMOKE_TIMEOUT   seconds to wait for each step (default 240)
#   SMOKE_KEEP      when set, leave the container running at the end, to look into a failure
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

work="$repo_root/artifacts/smoke-relink"
meta="src/Jellyfin.Plugin.MediaReports/meta.json"
plugin_version="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$meta")"
container="media-reports-relink-$$"

trap '[[ -n "${SMOKE_KEEP:-}" ]] && echo "kept: $container at $api" || docker rm -f "$container" >/dev/null 2>&1 || true' EXIT

echo "== dotnet publish"
rm -rf "$work/publish" "$work/stage" "$work/results" "$work/media"
mkdir -p "$work/results" "$work/clips"
dotnet publish src/Jellyfin.Plugin.MediaReports --nologo -c Release -o "$work/publish" >/dev/null
mkdir -p "$work/stage/plugins/MediaReports_${plugin_version}"
cp -R "$work/publish/." "$work/stage/plugins/MediaReports_${plugin_version}/"

docker pull --quiet "$image" >/dev/null
for seconds in 2 3; do
  if [[ ! -s "$work/clips/clip-$seconds.mkv" ]]; then
    echo "== generate a $seconds-second clip"
    MSYS_NO_PATHCONV=1 docker run --rm -v "$(host_path "$work/clips"):/out" \
      --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg "$image" -hide_banner -loglevel error \
      -f lavfi -i testsrc2=size=640x360:rate=24 -f lavfi -i sine=frequency=440:sample_rate=48000 \
      -t "$seconds" -c:v libx264 -preset veryfast -pix_fmt yuv420p -c:a aac -ac 2 "/out/clip-$seconds.mkv"
  fi
done

media="$work/media"
movie() { # movie <folder name>: a movie folder holding one file named after it
  mkdir -p "$media/movies/$1"
  cp "$work/clips/clip-2.mkv" "$media/movies/$1/$1.mkv"
}
movie "Alpha (2026) [tmdbid-900001]"
movie "Beta (2026) [tmdbid-900002]"
movie "Gamma (2026) [tmdbid-900003]"
show="$media/shows/Show (2026) [tvdbid-900010]/Season 01"
mkdir -p "$show"
cp "$work/clips/clip-2.mkv" "$show/Show (2026) S01E01.mkv"
cp "$work/clips/clip-2.mkv" "$show/Show (2026) S01E02.mkv"

api=""
start_server() {
  docker start "$container" >/dev/null
  local port
  port="$(docker port "$container" 8096/tcp | head -n 1 | sed 's/.*://')"
  api="http://127.0.0.1:$port"
  local deadline=$((SECONDS + timeout))
  until [[ "$(docker logs "$container" 2>&1 | grep -c "Startup complete")" -ge "$1" ]]; do
    (( SECONDS < deadline )) || { docker logs --tail 50 "$container" 2>&1; fail "the server did not start"; }
    sleep 2
  done
}

echo "== start $image"
MSYS_NO_PATHCONV=1 docker create --name "$container" -p 127.0.0.1::8096 \
  -v "$(host_path "$media"):/media:ro" "$image" >/dev/null
docker cp "$work/stage/plugins" "$container:/config/"
start_server 1

auth() {
  echo "MediaBrowser Client=\"media-reports-smoke\", Device=\"$1\", DeviceId=\"smoke-$(tr ' ' '-' <<<"$1")\", Version=\"1.0.0\"${2:+, Token=\"$2\"}"
}

req() {
  local device="$1" tok="$2" method="$3" path="$4" body="${5:-}"
  local headers=(-H "Content-Type: application/json")
  [[ -n "$device" ]] && headers+=(-H "Authorization: $(auth "$device" "$tok")")
  curl -sS -o "$work/results/last.json" -w '%{http_code}' -X "$method" "$api$path" "${headers[@]}" ${body:+--data "$body"}
}

ok() {
  local code
  code="$(req "$@")"
  [[ "$code" == 2* ]] || { cat "$work/results/last.json" >&2; echo >&2; fail "$3 $4 answered $code"; }
  cat "$work/results/last.json"
}

echo "== complete the startup wizard and add alice"
ok "admin box" "" POST /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null
ok "admin box" "" GET /Startup/User >/dev/null
ok "admin box" "" POST /Startup/User '{"Name":"admin","Password":"admin-password"}' >/dev/null
ok "admin box" "" POST /Startup/Complete >/dev/null
sign_in() { ok "$1" "" POST /Users/AuthenticateByName "{\"Username\":\"$2\",\"Pw\":\"$2-password\"}" | json "d['AccessToken']"; }
admin="$(sign_in "admin box" admin)"
ok "admin box" "$admin" POST /Users/New '{"Name":"alice","Password":"alice-password"}' >/dev/null
alice="$(sign_in "Alice Phone" alice)"
admin_id="$(ok "admin box" "$admin" GET /Users/Me | json "d['Id']")"

as_admin() { ok "admin box" "$admin" "$@"; }

echo "== add the movie and show libraries"
as_admin POST "/Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=false" \
  '{"LibraryOptions":{"PathInfos":[{"Path":"/media/movies"}]}}' >/dev/null
as_admin POST "/Library/VirtualFolders?name=Shows&collectionType=tvshows&refreshLibrary=true" \
  '{"LibraryOptions":{"PathInfos":[{"Path":"/media/shows"}]}}' >/dev/null

# items <type>: the library's items of a type as JSON lines: id, name, path, provider ids, numbers.
items() {
  as_admin GET "/Items?userId=$admin_id&IncludeItemTypes=$1&Recursive=true&Fields=Path,ProviderIds" | py -c '
import json, sys
for i in json.load(sys.stdin)["Items"]:
    print(json.dumps({"id": i["Id"], "path": i.get("Path") or "", "ids": i.get("ProviderIds") or {}, "season": i.get("ParentIndexNumber"), "episode": i.get("IndexNumber")}))'
}

# item_by_path <type> <path fragment>: the id of the one item whose path contains the fragment.
item_by_path() {
  items "$1" | py -c '
import json, sys
hits = [json.loads(l)["id"] for l in sys.stdin if sys.argv[1] in json.loads(l)["path"]]
print(hits[0] if len(hits) == 1 else "")' "$2"
}

# wait_for <what> <command...>: refreshes the library every 15 s until the command succeeds.
wait_for() {
  local what="$1"; shift
  local deadline=$((SECONDS + timeout)) next_scan=$((SECONDS + 15))
  until "$@" >/dev/null 2>&1; do
    (( SECONDS < deadline )) || { docker logs --tail 40 "$container" 2>&1 | grep -i "media report\|relink" >&2 || true; fail "$what"; }
    if (( SECONDS >= next_scan )); then as_admin POST /Library/Refresh >/dev/null; next_scan=$((SECONDS + 15)); fi
    sleep 2
  done
}

scan() { as_admin POST /Library/Refresh >/dev/null; }

library_ready() {
  [[ "$(items Movie | wc -l)" -eq 3 && "$(items Episode | wc -l)" -eq 2 ]] &&
    items Movie | grep -q '"Tmdb": "900001"'
}
wait_for "the library did not show three movies with their TMDb ids and two episodes" library_ready
alpha="$(item_by_path Movie "Alpha (2026)")"
beta="$(item_by_path Movie "Beta (2026)")"
gamma="$(item_by_path Movie "Gamma (2026)")"
e01="$(item_by_path Episode "S01E01")"
[[ -n "$alpha" && -n "$beta" && -n "$gamma" && -n "$e01" ]] || fail "the test titles were not found"
items Episode | grep -q '"season": 1, "episode": 1' || fail "the episodes have no season and episode numbers"

file() { # file <item> <reason>: alice files a report and prints its issue id
  ok "Alice Phone" "$alice" POST /MediaReports/Report "{\"itemId\":\"$1\",\"reasonCode\":\"$2\"}" | json "d['IssueId']"
}
issue() { as_admin GET "/MediaReports/Admin/Issues/$1"; }
set_status() { as_admin PATCH "/MediaReports/Admin/Issues/$1" "{\"Status\":\"$2\"}" >/dev/null; }

echo "== alice reports the titles; the admin fixes one"
alpha_open="$(file "$alpha" stutter)"
alpha_fixed="$(file "$alpha" audio)"
set_status "$alpha_fixed" Fixed
beta_issue="$(file "$beta" stutter)"
gamma_issue="$(file "$gamma" stutter)"
e01_issue="$(file "$e01" audio)"

# issue_is <issue> <python condition on d>: the issue's detail meets the condition.
issue_is() {
  issue "$1" | py -c "import json,sys; d=json.load(sys.stdin); sys.exit(0 if ($2) else 1)"
}

echo "== a movie renamed on disk, as an upgrade under a new name"
mv "$media/movies/Alpha (2026) [tmdbid-900001]" "$media/movies/Alpha Remux (2026) [tmdbid-900001]"
mv "$media/movies/Alpha Remux (2026) [tmdbid-900001]/Alpha (2026) [tmdbid-900001].mkv" \
  "$media/movies/Alpha Remux (2026) [tmdbid-900001]/Alpha Remux (2026) [tmdbid-900001].mkv"
scan
wait_for "the open issue on the renamed movie did not move" issue_is "$alpha_open" "d['ItemId'] != '$alpha' and d['ItemId'].replace('-', '') != '$alpha'"
issue "$alpha_open" >"$work/results/alpha-open.json"
issue "$alpha_fixed" >"$work/results/alpha-fixed.json"
alpha_new="$(item_by_path Movie "Alpha Remux")"
py - "$work/results" "$alpha_new" <<'PY' || fail "the moved issues are wrong"
import json, os, sys
r, new = sys.argv[1], sys.argv[2].replace("-", "")
o = json.load(open(os.path.join(r, "alpha-open.json")))
f = json.load(open(os.path.join(r, "alpha-fixed.json")))
for d in (o, f):
    assert d["ItemId"].replace("-", "") == new, (d["ItemId"], new)
    assert any(h["Kind"] == "relinked" and (h.get("Detail") or "").startswith("Moved to the replaced file") for h in d["History"]), d["History"]
assert o["Status"] == "ReplacedVerify", o["Status"]
assert f["Status"] == "Fixed", f["Status"]
print("renamed movie: open issue Replaced - verify on the new item, Fixed issue moved and still Fixed")
PY

echo "== an episode overwritten at the same path"
cp "$work/clips/clip-3.mkv" "$show/Show (2026) S01E01.mkv"
scan
wait_for "the episode's issue did not become Replaced - verify after its file changed" \
  issue_is "$e01_issue" "d['Status'] == 'ReplacedVerify' and any(h.get('Detail') == 'The file changed' for h in d['History'])"
echo "episode: Replaced - verify, The file changed"
set_status "$e01_issue" Investigating
sleep 2
cp "$work/clips/clip-2.mkv" "$show/Show (2026) S01E01.mkv"
touch "$show/Show (2026) S01E01.mkv"
scan
wait_for "the episode's issue did not become Replaced - verify again after a second change" \
  issue_is "$e01_issue" "d['Status'] == 'ReplacedVerify' and sum(h.get('Detail') == 'The file changed' for h in d['History']) == 2"
echo "episode: Replaced - verify again after the admin moved it on and the file changed again"

echo "== the task is listed with a daily trigger"
task_id="$(as_admin GET /ScheduledTasks | py -c '
import json, sys
t = [t for t in json.load(sys.stdin) if t["Name"] == "Re-link media reports"]
assert len(t) == 1, "no Re-link media reports task"
assert [x["Type"] for x in t[0]["Triggers"]] == ["DailyTrigger"], t[0]["Triggers"]
print(t[0]["Id"])')" || fail "Re-link media reports is not listed with a daily trigger"
run_task() {
  local before
  before="$(as_admin GET "/ScheduledTasks/$task_id" | json "(d.get('LastExecutionResult') or {}).get('EndTimeUtc')")"
  as_admin POST "/ScheduledTasks/Running/$task_id" >/dev/null
  local deadline=$((SECONDS + timeout))
  until as_admin GET "/ScheduledTasks/$task_id" | py -c "
import json, sys
d = json.load(sys.stdin); r = d.get('LastExecutionResult') or {}
sys.exit(0 if d['State'] == 'Idle' and r.get('EndTimeUtc') and r['EndTimeUtc'] != sys.argv[1] else 1)" "$before"; do
    (( SECONDS < deadline )) || fail "Re-link media reports did not finish"
    sleep 2
  done
  as_admin GET "/ScheduledTasks/$task_id" | json "d['LastExecutionResult']['Status']" | grep -x Completed >/dev/null || fail "Re-link media reports did not complete"
}

echo "== a movie replaced by two copies sharing its TMDb id"
cp -R "$media/movies/Gamma (2026) [tmdbid-900003]" "$media/movies/Gamma Copy A (2026) [tmdbid-900003]"
cp -R "$media/movies/Gamma (2026) [tmdbid-900003]" "$media/movies/Gamma Copy B (2026) [tmdbid-900003]"
rm -rf "$media/movies/Gamma (2026) [tmdbid-900003]"
scan
gamma_replaced() { [[ -z "$(item_by_path Movie "Gamma (2026)")" && -n "$(item_by_path Movie "Gamma Copy A")" && -n "$(item_by_path Movie "Gamma Copy B")" ]]; }
wait_for "the two Gamma copies did not replace the original" gamma_replaced
run_task
issue_is "$gamma_issue" "d['ItemId'].replace('-', '') == '$gamma'.replace('-', '') and d['Status'] == 'New' and d['File']['InLibrary'] is False" \
  || fail "the issue on a movie with two matches moved or does not read as no longer in the library: $(issue "$gamma_issue")"
echo "two matches: the issue stayed on the removed movie, no longer in the library"

echo "== a movie renamed while the server is stopped"
docker stop "$container" >/dev/null
mv "$media/movies/Beta (2026) [tmdbid-900002]" "$media/movies/Beta Final (2026) [tmdbid-900002]"
start_server 2
scan
beta_new_present() { [[ -n "$(item_by_path Movie "Beta Final")" ]]; }
wait_for "the renamed Beta did not appear after the restart" beta_new_present
run_task
wait_for "the issue on the movie renamed while the server was stopped did not move" \
  issue_is "$beta_issue" "d['ItemId'].replace('-', '') != '$beta'.replace('-', '') and d['Status'] == 'ReplacedVerify'"
echo "renamed while stopped: moved after the start, a scan and Re-link media reports"

echo "== alice's My Reports"
ok "Alice Phone" "$alice" GET /MediaReports/Report/Mine | py -c '
import json, sys
d = json.load(sys.stdin)
moved = [r for r in d["Reports"] if r["Status"] == "ReplacedVerify"]
assert moved, d["Reports"]
assert all(r["StatusLabel"] == "Being looked at" for r in moved), moved
print("My Reports: %d moved or changed reports read Being looked at" % len(moved))' || fail "My Reports is wrong"

if docker logs "$container" 2>&1 | grep -q "could not follow a library change"; then
  docker logs "$container" 2>&1 | grep -A5 "could not follow a library change" >&2
  fail "the re-linking logged an error"
fi

echo "PASS: reports follow replaced files on $image"
