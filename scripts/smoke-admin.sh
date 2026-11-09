#!/usr/bin/env bash
# Starts a real Jellyfin 12 server in Docker with the plugin, two movies and a four-episode
# series, files reports as two users, then works them through the admin issues API: access,
# the summary and its new count, the issue list with grouping and the season rollup, one
# issue with the file as Jellyfin has it now, status rules, notes, Set status for all,
# Refresh and Scan, a movie removed from the library, and the settings API with Export.
#
# Usage: scripts/smoke-admin.sh [--without-plugin]
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
  py -c "import json,sys; d=json.load(sys.stdin); v=$1; print('' if v is None else v)" | tr -d '\r'
}

work="$repo_root/artifacts/smoke-admin"
meta="src/Jellyfin.Plugin.MediaReports/meta.json"
plugin_version="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$meta")"
container="media-reports-admin-$$"

trap 'docker rm -f "$container" >/dev/null 2>&1 || true' EXIT

echo "== dotnet publish"
rm -rf "$work/publish" "$work/stage" "$work/results" "$work/library"
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
echo "== generate two movies and a four-episode series"
clip "movies/Test Movie (2026)/Test Movie (2026).mkv" 20
clip "movies/Gone Movie (2026)/Gone Movie (2026).mkv" 5
for e in 1 2 3 4; do clip "shows/Test Series (2026)/Season 01/Test Series (2026) S01E0$e.mkv" 5; done
# The library is a copy, so one movie can be removed during the run.
cp -R "$cache" "$work/library"
library="$work/library"

echo "== start $image (plugin installed: $with_plugin)"
MSYS_NO_PATHCONV=1 docker create --name "$container" -p 127.0.0.1::8096 \
  -v "$(host_path "$library/movies"):/media/movies:ro" -v "$(host_path "$library/shows"):/media/shows:ro" "$image" >/dev/null
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

# check <file> <python assertions using d>: fails with the message the assertions raise.
check() {
  local file="$1"; shift
  py - "$file" "$1" <<'PY' || fail "$2"
import json, sys
d = json.load(open(sys.argv[1]))
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
items() { ok "admin box" "$admin" GET "/Items?userId=$alice_id&IncludeItemTypes=$1&Recursive=true&SortBy=SortName&Fields=Path"; }
deadline=$((SECONDS + timeout))
until [[ "$(items Movie | json "len(d['Items'])")" == 2 && "$(items Episode | json "len(d['Items'])")" == 4 ]]; do
  (( SECONDS < deadline )) || fail "the library did not show two movies and four episodes"
  ok "admin box" "$admin" POST /Library/Refresh >/dev/null
  sleep 10
done
movie="$(items Movie | json "[i['Id'] for i in d['Items'] if i['Name'].startswith('Test')][0]")"
gone="$(items Movie | json "[i['Id'] for i in d['Items'] if i['Name'].startswith('Gone')][0]")"
mapfile -t episodes < <(items Episode | json "'\n'.join(i['Id'] for i in d['Items'])")

echo "== alice plays the movie on the TV; both users file reports"
state() { echo "{\"ItemId\":\"$movie\",\"PlaySessionId\":\"smoke-play\",\"PlayMethod\":\"DirectPlay\",\"PositionTicks\":$1,\"CanSeek\":true}"; }
ok "Living Room TV" "$alice" POST /Sessions/Playing "$(state 0)" >/dev/null
ok "Living Room TV" "$alice" POST /Sessions/Playing/Stopped "$(state 100000000)" >/dev/null
file() { ok "$1" "$2" POST /MediaReports/Report "{\"itemId\":\"$3\",\"reasonCode\":\"$4\"${5:+,\"note\":\"$5\"}}" | json "d['IssueId']"; }
movie_issue="$(file "Living Room TV" "$alice" "$movie" stutter "keeps jumping back")"
[[ "$(file "Bob Laptop" "$bob" "$movie" stutter)" == "$movie_issue" ]] || fail "bob's report did not join alice's issue"
gone_issue="$(file "Bob Laptop" "$bob" "$gone" audio)"
episode_issues=()
for e in "${episodes[@]:0:3}"; do episode_issues+=("$(file "Living Room TV" "$alice" "$e" subtitles)"); done
lone_issue="$(file "Bob Laptop" "$bob" "${episodes[3]}" metadata)"

echo "== access"
for call in "GET /MediaReports/Admin/Summary" "GET /MediaReports/Admin/Issues" "GET /MediaReports/Admin/Issues/$movie_issue" \
  "PATCH /MediaReports/Admin/Issues/$movie_issue" "POST /MediaReports/Admin/Issues/Status" \
  "POST /MediaReports/Admin/Issues/$movie_issue/Refresh" "POST /MediaReports/Admin/Issues/$movie_issue/Scan" \
  "GET /MediaReports/Admin/Settings" "PUT /MediaReports/Admin/Settings" "GET /MediaReports/Admin/Export"; do
  set -- $call
  expect 401 "" "" "" "$1" "$2" '{}'
  got="$(req "Bob Laptop" "$bob" "$1" "$2" '{"status":"Fixed","issueIds":[1]}')"
  [[ "$got" == 403 ]] || fail "a non-admin got $got from $1 $2"
done
echo "non-admin refused on every endpoint"
expect 404 issue-not-found "admin box" "$admin" GET /MediaReports/Admin/Issues/999999

echo "== summary and the new count"
ok "admin box" "$admin" GET /MediaReports/Admin/Summary >"$work/results/summary.json"
check "$work/results/summary.json" "assert (d['OpenIssues'], d['Reports'], d['New']) == (6, 7, 6), d" "the summary is wrong"
ok "admin box" "$admin" GET "/MediaReports/Admin/Summary?markSeen=true" >/dev/null
ok "admin box" "$admin" GET /MediaReports/Admin/Summary >"$work/results/summary-seen.json"
check "$work/results/summary-seen.json" "assert d['New'] == 0, d" "markSeen did not clear the new count"
echo "summary: 6 open issues from 7 reports, 6 new, 0 after markSeen"

echo "== the issue list"
ok "admin box" "$admin" GET /MediaReports/Admin/Issues >"$work/results/issues.json"
check "$work/results/issues.json" "
rows = d['Rows']
assert d['Total'] == 4, d['Total']
season = [r for r in rows if r['Kind'] == 'season']
assert len(season) == 1 and season[0]['EpisodeCount'] == 3 and len(season[0]['Children']) == 3 and season[0]['Status'] == 'New', season
movie = next(r for r in rows if r.get('IssueId') == $movie_issue)
assert (movie['Reports'], movie['People'], movie['ReasonLabel']) == (2, 2, 'Stutter or jitter'), movie
assert any(r.get('IssueId') == $lone_issue for r in rows), 'the lone episode is not its own row'
assert 'subtitles' in [x['Code'] for x in d['Reasons']] and d['Clients'], (d['Reasons'], d['Clients'])
" "the issue list is wrong"
ok "admin box" "$admin" GET "/MediaReports/Admin/Issues?groupBy=titleClient&q=test%20movie" >"$work/results/by-client.json"
check "$work/results/by-client.json" "
rows = d['Rows']
assert len(rows) == 2 and all(r.get('IssueId') == $movie_issue for r in rows), rows
assert sorted(r['Reports'] for r in rows) == [1, 1] and {r.get('Client') for r in rows} == {'media-reports-smoke', None}, rows
" "grouping by title and client is wrong"
echo "list: one season row of 3, the movie with 2 reports from 2 people, by client 2 rows"

echo "== one issue, with the file as Jellyfin has it now"
ok "admin box" "$admin" GET "/MediaReports/Admin/Issues/$movie_issue" >"$work/results/issue.json"
check "$work/results/issue.json" "
f = d['File']
assert f['InLibrary'] and f['Path'].endswith('Test Movie (2026).mkv') and f['Size'] > 0 and f['Container'], f
kinds = sorted(s['Type'] for s in f['Streams'])
assert 'Video' in kinds and 'Audio' in kinds, kinds
reports = d['Reports']
assert [r['ReporterName'] for r in reports] == ['bob', 'alice'], reports
assert reports[1]['Snapshot']['deviceName'] == 'Living Room TV' and reports[1]['Note'] == 'keeps jumping back', reports[1]
assert [(e['Kind'], e.get('ActorName')) for e in d['History']] == [('filed', 'alice'), ('filed', 'bob')], d['History']
assert d['ServerId'], d
" "the issue's details are wrong"
echo "issue: live file with streams, two reports newest first, history with names"

echo "== status rules and notes"
expect 400 status-not-allowed "admin box" "$admin" PATCH "/MediaReports/Admin/Issues/$movie_issue" '{"status":"ReplacedVerify"}'
expect 400 status-not-allowed "admin box" "$admin" PATCH "/MediaReports/Admin/Issues/$movie_issue" '{"status":"Bogus"}'
long="$(py -c 'print("x" * 10001)')"
expect 400 notes-too-long "admin box" "$admin" PATCH "/MediaReports/Admin/Issues/$movie_issue" "{\"notes\":\"$long\"}"
ok "admin box" "$admin" PATCH "/MediaReports/Admin/Issues/$movie_issue" '{"status":"Fixed","notes":"Re-encoded the file."}' >"$work/results/fixed.json"
check "$work/results/fixed.json" "
assert d['Status'] == 'Fixed' and d.get('ClosedAt') and d.get('Notes') == 'Re-encoded the file.', d
kinds = [(e['Kind'], e.get('ActorName')) for e in d['History']]
assert ('status changed', 'admin') in kinds and ('note added', 'admin') in kinds, kinds
" "closing the issue is wrong"
newer="$(file "Living Room TV" "$alice" "$movie" stutter)"
[[ "$newer" != "$movie_issue" ]] || fail "a report after Fixed joined the closed issue"
expect 409 newer-open-issue "admin box" "$admin" PATCH "/MediaReports/Admin/Issues/$movie_issue" '{"status":"New"}'
check "$work/results/last.json" "assert str(d.get('IssueId')) == '$newer', d" "the refusal does not name the newer issue"
echo "Replaced – verify and unknown refused, long notes refused, Fixed with notes, reopen refused naming issue $newer"

echo "== Set status for all"
ids="$(IFS=,; echo "${episode_issues[*]}")"
expect 409 newer-open-issue "admin box" "$admin" POST /MediaReports/Admin/Issues/Status "{\"issueIds\":[$ids,$movie_issue],\"status\":\"New\"}"
ok "admin box" "$admin" GET "/MediaReports/Admin/Issues/${episode_issues[0]}" | json "d['Status']" | grep -x New >/dev/null || fail "a refused Set status for all changed an issue"
ok "admin box" "$admin" POST /MediaReports/Admin/Issues/Status "{\"issueIds\":[$ids],\"status\":\"Investigating\"}" >/dev/null
for i in "${episode_issues[@]}"; do
  ok "admin box" "$admin" GET "/MediaReports/Admin/Issues/$i" >"$work/results/episode.json"
  check "$work/results/episode.json" "assert d['Status'] == 'Investigating' and d['History'][-1]['Kind'] == 'status changed', d['History']" "Set status for all missed issue $i"
done
echo "all or nothing: refused with one bad id, then all three Investigating with their own history line"

echo "== Refresh and Scan"
expect 202 "" "admin box" "$admin" POST "/MediaReports/Admin/Issues/$movie_issue/Refresh"
expect 202 "" "admin box" "$admin" POST "/MediaReports/Admin/Issues/${episode_issues[0]}/Scan"
ok "admin box" "$admin" GET "/MediaReports/Admin/Issues/${episode_issues[0]}" | json "d['History'][-1]['Kind']" | grep -x "action run" >/dev/null \
  || fail "Scan wrote no history line"
echo "Refresh and Scan answered 202 with a history line"

echo "== a movie removed from the library"
rm -rf "$library/movies/Gone Movie (2026)"
ok "admin box" "$admin" POST /Library/Refresh >/dev/null
deadline=$((SECONDS + timeout))
until [[ "$(req "admin box" "$admin" GET "/Items/$gone?userId=$alice_id")" == 404 ]]; do
  (( SECONDS < deadline )) || fail "the removed movie stayed in the library"
  sleep 5
done
ok "admin box" "$admin" GET "/MediaReports/Admin/Issues/$gone_issue" >"$work/results/gone.json"
check "$work/results/gone.json" "f = d['File']; assert f['InLibrary'] is False and f['Path'].endswith('Gone Movie (2026).mkv'), f" "a removed movie is not shown as gone"
expect 404 not-in-library "admin box" "$admin" POST "/MediaReports/Admin/Issues/$gone_issue/Scan"
expect 404 not-in-library "admin box" "$admin" POST "/MediaReports/Admin/Issues/$gone_issue/Refresh"
echo "removed movie: inLibrary false with the stored path, Refresh and Scan 404"

echo "== settings"
settings() { ok "admin box" "$admin" GET /MediaReports/Admin/Settings >"$work/results/$1.json"; }
settings settings
check "$work/results/settings.json" "
assert [r['Code'] for r in d['Reasons']] == ['wont-play', 'playback-error', 'poor-quality', 'stutter', 'audio', 'subtitles', 'metadata', 'other'], d['Reasons']
counts = {r['Code']: r['Reports'] for r in d['Reasons']}
assert (counts['stutter'], counts['subtitles'], counts['metadata'], counts['other']) == (3, 3, 1, 0), counts
assert d['ReportingEnabled'] is True and d['ReportRetentionDays'] == 365, d
" "Settings GET does not list the eight defaults with their report counts"
# reasons <python expression over r, the saved reasons>: a Reasons array for a PUT.
reasons() {
  py - "$work/results/settings.json" "$1" <<'PY' | tr -d '\r'
import json, sys
r = [{'Code': x['Code'], 'Label': x['Label'], 'Retired': x['Retired']} for x in json.load(open(sys.argv[1]))['Reasons']]
print(json.dumps(eval(sys.argv[2])))
PY
}
dup="$(reasons "[dict(x, Label='Audio problem') if x['Code'] == 'stutter' else x for x in r]")"
expect 400 label-duplicate "admin box" "$admin" PUT /MediaReports/Admin/Settings "{\"Reasons\":$dup,\"ReportingEnabled\":false}"
check "$work/results/last.json" "assert d['Field'] == 'reasons' and d['ReasonIndex'] == 3, d" "the duplicate label is not pinned on the renamed reason"
expect 400 label-empty "admin box" "$admin" PUT /MediaReports/Admin/Settings "{\"Reasons\":$(reasons "r + [{'Code': None, 'Label': ' ', 'Retired': False}]")}"
expect 400 no-active-reason "admin box" "$admin" PUT /MediaReports/Admin/Settings "{\"Reasons\":$(reasons "[dict(x, Retired=True) for x in r]")}"
expect 400 retention-invalid "admin box" "$admin" PUT /MediaReports/Admin/Settings '{"ReportRetentionDays":"ten"}'
expect 400 retention-invalid "admin box" "$admin" PUT /MediaReports/Admin/Settings '{"ReportRetentionDays":-1}'
expect 400 webhook-invalid "admin box" "$admin" PUT /MediaReports/Admin/Settings '{"WebhookUrl":"ftp://example.com/hook"}'
settings unchanged
cmp -s "$work/results/settings.json" "$work/results/unchanged.json" || fail "a refused save changed the settings"
echo "refused saves name the field and change nothing"

saved="$(reasons "[dict(x, Label='Choppy video') if x['Code'] == 'stutter' else dict(x, Retired=True) if x['Code'] == 'metadata' else x for x in r] + [{'Code': None, 'Label': 'Wrong language', 'Retired': False}]")"
ok "admin box" "$admin" PUT /MediaReports/Admin/Settings "{\"Reasons\":$saved,\"ReportRetentionDays\":\"30\"}" >"$work/results/saved.json"
check "$work/results/saved.json" "
codes = [r['Code'] for r in d['Reasons']]
assert codes[-1] == 'wrong-language' and len(set(codes)) == 9, codes
assert d['ReportRetentionDays'] == 30, d
" "the save did not keep the codes and add the new reason"
ok "Living Room TV" "$alice" GET "/MediaReports/Report/Context?itemId=$movie" >"$work/results/context.json"
check "$work/results/context.json" "
labels = [r['Label'] for r in d['Reasons']]
assert 'Choppy video' in labels and 'Wrong metadata' not in labels and labels[-1] == 'Wrong language', labels
" "the report dialog does not offer the saved reasons"
ok "admin box" "$admin" GET "/MediaReports/Admin/Issues/$movie_issue" | json "d['ReasonLabel']" | grep -x "Choppy video" >/dev/null \
  || fail "the older issue does not show the renamed reason"
settings after
check "$work/results/after.json" "
m = [r for r in d['Reasons'] if r['Code'] == 'metadata'][0]
assert m['Retired'] is True and m['Reports'] == 1, m
" "the retired reason lost its report count"
echo "renamed, retired and added: the report dialog and an older issue use them without a restart"

ok "admin box" "$admin" PUT /MediaReports/Admin/Settings '{"ReportingEnabled":false}' >/dev/null
expect 403 reporting-off "Bob Laptop" "$bob" POST /MediaReports/Report "{\"itemId\":\"${episodes[1]}\",\"reasonCode\":\"audio\"}"
ok "admin box" "$admin" PUT /MediaReports/Admin/Settings '{"ReportingEnabled":true}' >/dev/null
ok "admin box" "$admin" GET /MediaReports/Admin/Settings | json "(d['ReportingEnabled'], d['ReportRetentionDays'], d['Reasons'][3]['Label'])" | grep -x "(True, 30, 'Choppy video')" >/dev/null \
  || fail "a save of one field changed the others"
echo "reporting off refuses the next report without a restart; a save of one field keeps the rest"

echo "== Export"
curl -sS -D "$work/results/export.headers" -o "$work/results/export.json" -H "Authorization: $(auth "admin box" "$admin")" "$api/MediaReports/Admin/Export"
grep -qi 'filename=.*media-reports-export-.*\.json' "$work/results/export.headers" || fail "Export is not a JSON download"
check "$work/results/export.json" "assert len(d['issues']) >= 6 and len(d['reports']) >= 7 and len(d['events']) >= 7, {k: len(v) for k, v in d.items() if isinstance(v, list)}" "Export does not hold every issue, report and event"
echo "Export downloads every issue, report and event"

echo "PASS: the admin issues API on $image"
