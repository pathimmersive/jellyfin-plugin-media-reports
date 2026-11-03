#!/usr/bin/env bash
# Starts a real Jellyfin 12 server in Docker with the plugin, seventeen movies and a
# four-episode series, then drives the Media Reports page in the Dashboard in headless
# Chromium as the admin, at 1280 and 390 px: the menu entry and its count, the Reports
# tab with its states, views, grouping, rollup, filters, sort and paging, and one issue
# with its reports, file, history, status, notes and quick actions.
#
# Usage: scripts/smoke-triage.sh [--keep]
#   --keep  leave the server running after setup and skip the browser checks
#
# Environment:
#   JELLYFIN_IMAGE      image to run (default jellyfin/jellyfin:12)
#   SMOKE_TIMEOUT       seconds to wait for each step (default 180)
#   SMOKE_PYTHON        Python with Playwright for Python installed (default python3)
#   PLAYWRIGHT_CHANNEL  a browser channel to launch, such as msedge (default: Playwright's Chromium)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

image="${JELLYFIN_IMAGE:-jellyfin/jellyfin:12}"
timeout="${SMOKE_TIMEOUT:-180}"
keep=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --keep) keep=1; shift ;;
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
  if [[ -n "${SMOKE_PYTHON:-}" ]]; then "$SMOKE_PYTHON" "$@";
  elif command -v python3 >/dev/null 2>&1 && python3 -c "" >/dev/null 2>&1; then python3 "$@"; else python "$@"; fi
}

json() {
  py -c "import json,sys; d=json.load(sys.stdin); v=$1; print('' if v is None else v)" | tr -d '\r'
}

work="$repo_root/artifacts/smoke-triage"
meta="src/Jellyfin.Plugin.MediaReports/meta.json"
plugin_version="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$meta")"
container="media-reports-triage-$$"

if [[ $keep -eq 0 ]]; then
  trap 'docker rm -f "$container" >/dev/null 2>&1 || true' EXIT
fi

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
echo "== generate the movies and a four-episode series"
clip "movies/Test Movie (2026)/Test Movie (2026).mkv" 20
clip "movies/Gone Movie (2026)/Gone Movie (2026).mkv" 3
for e in 1 2 3 4; do clip "shows/Test Series (2026)/Season 01/Test Series (2026) S01E0$e.mkv" 3; done
# The library is a copy, so one movie can be removed during the run; fifteen more movies
# are copies of a short clip, enough for 120 open issues.
cp -R "$cache" "$work/library"
library="$work/library"
for n in $(seq -w 1 15); do
  mkdir -p "$library/movies/Filler $n (2026)"
  cp "$library/movies/Gone Movie (2026)/Gone Movie (2026).mkv" "$library/movies/Filler $n (2026)/Filler $n (2026).mkv"
done

echo "== start $image"
MSYS_NO_PATHCONV=1 docker create --name "$container" -p 127.0.0.1::8096 \
  -v "$(host_path "$library/movies"):/media/movies:ro" -v "$(host_path "$library/shows"):/media/shows:ro" "$image" >/dev/null
docker cp "$work/stage/plugins" "$container:/config/"
docker start "$container" >/dev/null
port="$(docker port "$container" 8096/tcp | head -n 1 | sed 's/.*://')"
api="http://127.0.0.1:$port"
deadline=$((SECONDS + timeout))
until docker logs "$container" 2>&1 | grep "Startup complete" >/dev/null; do
  (( SECONDS < deadline )) || { docker logs --tail 50 "$container" 2>&1; fail "the server did not start"; }
  sleep 2
done

auth="MediaBrowser Client=\"media-reports-smoke\", Device=\"smoke-script\", DeviceId=\"media-reports-triage\", Version=\"1.0.0\""
token=""
call() {
  local method="$1" path="$2" body="${3:-}"
  curl -sS --fail-with-body -X "$method" "$api$path" \
    -H "Authorization: $auth${token:+, Token=\"$token\"}" -H "Content-Type: application/json" \
    ${body:+--data "$body"} || { echo >&2; fail "$method $path was refused"; }
}

echo "== complete the startup wizard, add users and the libraries"
call POST /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}'
call GET /Startup/User >/dev/null
call POST /Startup/User '{"Name":"admin","Password":"admin-password"}'
call POST /Startup/Complete
token="$(call POST /Users/AuthenticateByName '{"Username":"admin","Pw":"admin-password"}' | json "d['AccessToken']")"
for user in alice bob carol; do
  call POST /Users/New "{\"Name\":\"$user\",\"Password\":\"$user-password\"}" >/dev/null
done
call POST "/Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=false" '{"LibraryOptions":{"PathInfos":[{"Path":"/media/movies"}]}}'
call POST "/Library/VirtualFolders?name=Shows&collectionType=tvshows&refreshLibrary=true" '{"LibraryOptions":{"PathInfos":[{"Path":"/media/shows"}]}}'
items() { call GET "/Items?IncludeItemTypes=$1&Recursive=true&SortBy=SortName"; }
deadline=$((SECONDS + timeout))
until [[ "$(items Movie | json "len(d['Items'])")" == 17 && "$(items Episode | json "len(d['Items'])")" == 4 ]]; do
  (( SECONDS < deadline )) || fail "the library did not show 17 movies and 4 episodes"
  call POST /Library/Refresh >/dev/null
  sleep 10
done
items Movie >"$work/results/movies.json"
items Episode >"$work/results/episodes.json"

cat >"$work/results/server.json" <<EOF
{"api": "$api", "container": "$container", "admin_token": "$token",
 "plugin_id": "$(sed -n 's/.*"guid": *"\([^"]*\)".*/\1/p' "$meta")",
 "movies": $(cat "$work/results/movies.json"), "episodes": $(cat "$work/results/episodes.json"),
 "library": "$(host_path "$library" | sed 's/\\/\\\\/g')"}
EOF

if [[ $keep -eq 1 ]]; then
  echo "server kept: $api (container $container); details in $work/results/server.json"
  exit 0
fi

echo "== drive the Media Reports page"
# Needs Playwright for Python (the version CI pins) and a Chromium it can launch.
py - "$work/results/server.json" <<'PY'
import json
import os
import re
import sqlite3
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

from playwright.sync_api import sync_playwright

cfg = json.load(open(sys.argv[1], encoding="utf-8"))
API = cfg["api"]
WAIT_MS = 15000
PAGE = "/web/#/configurationpage?name=mediareports"
# jellyfin-web keeps earlier views in the page, hidden; checks look at the one on screen.
V = ".page:not(.hide) "
failures = []


def check(ok, what):
    print(("ok   " if ok else "FAIL ") + what)
    if not ok:
        failures.append(what)


def request(method, path, token=None, body=None, device="smoke"):
    auth = 'MediaBrowser Client="media-reports-smoke", Device="%s", DeviceId="triage-%s", Version="1.0.0"' % (device, device.replace(" ", "-"))
    if token:
        auth += ', Token="%s"' % token
    headers = {"Authorization": auth}
    data = None
    if body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(API + path, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req) as response:
            text = response.read()
            return response.status, (json.loads(text) if text else None)
    except urllib.error.HTTPError as error:
        text = error.read()
        return error.code, (json.loads(text) if text else None)


def ok(method, path, token=None, body=None, device="smoke"):
    status, result = request(method, path, token, body, device)
    if status >= 300:
        raise SystemExit("FAIL: %s %s answered %s: %s" % (method, path, status, result))
    return result


ADMIN = cfg["admin_token"]


def sign_in_api(user, device):
    return ok("POST", "/Users/AuthenticateByName", body={"Username": user, "Pw": user + "-password"}, device=device)["AccessToken"]


def report(token, device, item, reason, note=None):
    body = {"itemId": item, "reasonCode": reason}
    if note:
        body["note"] = note
    return ok("POST", "/MediaReports/Report", token, body, device)["IssueId"]


def admin_patch(issue, body):
    return ok("PATCH", "/MediaReports/Admin/Issues/%s" % issue, ADMIN, body)


movies = {m["Name"]: m["Id"] for m in cfg["movies"]["Items"]}
episodes = [e["Id"] for e in cfg["episodes"]["Items"]]
MOVIE = movies["Test Movie"] if "Test Movie" in movies else next(v for k, v in movies.items() if k.startswith("Test Movie"))
GONE = next(v for k, v in movies.items() if k.startswith("Gone Movie"))
FILLERS = [v for k, v in sorted(movies.items()) if k.startswith("Filler")]
series = ok("GET", "/Items?IncludeItemTypes=Series&Recursive=true", ADMIN)["Items"][0]["Id"]
SERVER_ID = ok("GET", "/System/Info/Public")["Id"]


def browser(p):
    launch = {"channel": os.environ["PLAYWRIGHT_CHANNEL"]} if os.environ.get("PLAYWRIGHT_CHANNEL") else {}
    return p.chromium.launch(**launch)


def sign_in(page, user):
    page.goto(API + "/web/#/login")
    page.wait_for_selector(V + "#txtManualName, button.btnManual", timeout=WAIT_MS)
    if not page.locator(V + "#txtManualName").is_visible():
        page.locator(V + "button.btnManual").first.click()
    page.fill(V + "#txtManualName", user)
    page.fill(V + "#txtManualPassword", user + "-password")
    page.locator(V + "#txtManualPassword").press("Enter")
    page.wait_for_function("() => window.ApiClient && window.ApiClient.getCurrentUserId && window.ApiClient.getCurrentUserId()", timeout=WAIT_MS)
    page.wait_for_function("() => location.hash.indexOf('home') !== -1", timeout=WAIT_MS)
    page.wait_for_timeout(1000)


def open_page(page, query=""):
    page.goto(API + PAGE + query)
    loaded(page)


def loaded(page):
    page.wait_for_function("""() => { const r = document.querySelector('.page:not(.hide) .mediaReportsTriage');
        return r && !r.querySelector('[aria-busy=true]') && r.children.length > 0; }""", timeout=WAIT_MS)


def text(page, selector):
    found = page.locator(V + selector)
    return found.first.inner_text().strip() if found.count() else None


def rows(page):
    return page.evaluate("""() => [...document.querySelectorAll('.page:not(.hide) .mrTable tbody tr')].filter(r => !r.classList.contains('mrSetAll')).map(r => ({
        kind: r.className, issue: r.getAttribute('data-issue'), dot: !!r.querySelector('.mrDot'),
        cells: [...r.querySelectorAll('td')].map(td => td.innerText.trim()) }))""")


def wait_rows(page, count):
    page.wait_for_function("n => document.querySelectorAll('.page:not(.hide) .mrTable tbody tr.mrIssueRow, .page:not(.hide) .mrTable tbody tr.mrSeasonRow').length === n", arg=count, timeout=WAIT_MS)


def no_overflow(page):
    return page.evaluate("() => document.documentElement.scrollWidth <= window.innerWidth + 1")


with sync_playwright() as p:
    b = browser(p)
    page = b.new_context(viewport={"width": 1280, "height": 900}).new_page()
    errors = []
    page.on("pageerror", lambda e: errors.append(str(e)))
    sign_in(page, "admin")

    # ---- Just installed --------------------------------------------------------------
    page.goto(API + "/web/#/dashboard")
    page.wait_for_selector('a[href$="configurationpage?name=mediareports"]', timeout=WAIT_MS)
    entry = page.locator('a[href$="configurationpage?name=mediareports"]')
    check(entry.locator(".MuiListItemText-root").inner_text().strip() == "Media Reports" and entry.locator(".material-icons").inner_text() == "report_problem",
          "the Dashboard menu lists Media Reports under Plugins with the report_problem icon")
    entry.click()
    loaded(page)
    check(page.locator(V + ".mrJustInstalled").count() == 1 and "No reports yet" in text(page, ".mrJustInstalled"), "a fresh install shows the just-installed state")

    # ---- Reports from three users ----------------------------------------------------
    alice = sign_in_api("alice", "Living Room TV")
    bob = sign_in_api("bob", "Bob Laptop")
    carol = sign_in_api("carol", "Carol Phone")
    play = {"ItemId": MOVIE, "PlaySessionId": "triage-play", "PlayMethod": "Transcode", "PositionTicks": 0, "CanSeek": True}
    ok("POST", "/Sessions/Playing", alice, play, "Living Room TV")
    ok("POST", "/Sessions/Playing/Progress", alice, dict(play, PositionTicks=43250000000), "Living Room TV")
    movie_issue = report(alice, "Living Room TV", MOVIE, "stutter", "Picture keeps jumping back a little every second.")
    report(bob, "Bob Laptop", MOVIE, "stutter")
    report(carol, "Carol Phone", MOVIE, "stutter")
    ok("POST", "/Sessions/Playing/Stopped", alice, dict(play, PositionTicks=43260000000), "Living Room TV")
    season_issues = [report(alice, "Living Room TV", e, "subtitles") for e in episodes[:3]]
    lone = report(bob, "Bob Laptop", episodes[3], "metadata")
    gone_issue = report(bob, "Bob Laptop", GONE, "audio")
    series_issue = report(carol, "Carol Phone", series, "other", "Two seasons have the same poster.")
    closed_issue = report(alice, "Living Room TV", FILLERS[0], "poor-quality")
    admin_patch(closed_issue, {"status": "Fixed"})

    # ---- The menu count and the Reports tab ----------------------------------------
    # A fresh load of the Dashboard, as when the admin opens it.
    page.goto(API + "/web/#/dashboard")
    page.reload()
    page.wait_for_function("() => { const b = document.querySelector('.mediaReportsMenuCount'); return b && b.textContent === '7'; }", timeout=WAIT_MS)
    check(True, "the menu entry counts the 7 issues new to this admin")
    page.locator('a[href$="configurationpage?name=mediareports"]').click()
    loaded(page)
    wait_rows(page, 5)
    summary = text(page, ".mrSummary")
    check(summary == "7 open issues from 9 reports · 7 new since you last looked", "the summary line (%s)" % summary)
    listed = rows(page)
    check(all(r["dot"] for r in listed), "every issue new since the admin last looked carries the dot")
    season = [r for r in listed if "mrSeasonRow" in r["kind"]]
    check(len(season) == 1 and "Season 1 · 3 episodes" in season[0]["cells"][0], "three episodes with one reason roll up into one season row")
    movie_row = next((r for r in listed if r["issue"] == str(movie_issue)), None)
    check(movie_row and movie_row["cells"][2].startswith("3") and "3 people" in movie_row["cells"][2] and movie_row["cells"][4] == "Direct play",
          "the movie's row counts 3 reports from 3 people and how it played (%s)" % (movie_row and movie_row["cells"]))
    check(listed[0]["issue"] == str(series_issue), "rows are sorted by last report, newest first")
    page.wait_for_function("() => !document.querySelector('.mediaReportsMenuCount')", timeout=WAIT_MS)
    check(True, "opening the tab clears the menu count")

    # Leaving and coming back with nothing new: no dots.
    page.goto(API + "/web/#/dashboard")
    page.wait_for_timeout(2500)
    open_page(page)
    wait_rows(page, 5)
    check(not any(r["dot"] for r in rows(page)), "reopening the tab with no new reports shows no dots")
    gap = page.evaluate("""() => { const v = document.querySelector('.page:not(.hide) .mediaReportsTriage');
        return v.querySelector('.mrControls').getBoundingClientRect().top - v.querySelector('.mrTabs').getBoundingClientRect().bottom; }""")
    check(gap >= 16, "the filters sit apart from the tabs (%dpx)" % gap)

    # Season row: expand, Set status for all.
    page.locator(V + "tr.mrSeasonRow button.mrTitleLink").click()
    check(page.locator(V + "tr.mrChild[data-issue]").count() == 3, "the season row expands to its three episode issues")
    page.locator(V + "tr.mrSetAll select").select_option("Investigating")
    page.wait_for_function("() => [...document.querySelectorAll('.page:not(.hide) tr.mrSeasonRow .mrPill')].some(p => p.textContent === 'Investigating')", timeout=WAIT_MS)
    statuses = [ok("GET", "/MediaReports/Admin/Issues/%s" % i, ADMIN) for i in season_issues]
    check(all(s["Status"] == "Investigating" and s["History"][-1]["Kind"] == "status changed" for s in statuses),
          "Set status for all makes every episode Investigating, each with its own history line")

    # Views.
    page.locator(V + "button.mrSeg", has_text="Closed").click()
    wait_rows(page, 1)
    check(rows(page)[0]["issue"] == str(closed_issue) and "Fixed" in rows(page)[0]["cells"][5], "Closed lists only the closed issue")
    page.locator(V + "button.mrSeg", has_text="All").click()
    wait_rows(page, 6)
    check(True, "All lists open and closed issues")
    page.locator(V + "button.mrSeg", has_text="Open").click()
    wait_rows(page, 5)

    # By title and client.
    page.locator(V + "button.mrSeg", has_text="By title and client").click()
    page.wait_for_function("() => document.querySelectorAll('.page:not(.hide) .mrTable tbody tr.mrIssueRow').length >= 7", timeout=WAIT_MS)
    split = [r for r in rows(page) if r["issue"] == str(movie_issue)]
    check(len(split) == 2 and sorted(r["cells"][3] for r in split) == ["No playback attached", "media-reports-smoke"],
          "by title and client splits the movie's issue by client (%s)" % [r["cells"][3] for r in split])
    page.locator(V + "button.mrSeg", has_text="By title").first.click()
    wait_rows(page, 5)

    # Filters, the no-match state and Clear filters.
    page.select_option(V + "select[data-filter=reason]", "audio")
    wait_rows(page, 1)
    check(rows(page)[0]["issue"] == str(gone_issue), "the Reason filter keeps only that reason")
    page.select_option(V + "select[data-filter=client]", "media-reports-smoke")
    page.wait_for_selector(V + ".mrNoMatch", timeout=WAIT_MS)
    check("No open issues for “Audio problem” on media-reports-smoke." == text(page, ".mrNoMatch p"), "a filter matching nothing names the filters (%s)" % text(page, ".mrNoMatch p"))
    page.locator(V + ".mrNoMatch button", has_text="Clear filters").click()
    wait_rows(page, 5)
    check(True, "Clear filters restores every issue")

    # Search and sort.
    page.fill(V + ".mrSearch input", "test series")
    page.locator(V + ".mrSearch input").press("Enter")
    wait_rows(page, 3)
    check(True, "the title search matches the series' episodes, season and series")
    page.fill(V + ".mrSearch input", "")
    page.locator(V + ".mrSearch input").press("Enter")
    wait_rows(page, 5)
    page.locator(V + "th button", has_text="Title").click()
    page.wait_for_function("() => (document.querySelector('.page:not(.hide) th[aria-sort] button') || {}).textContent === 'Title'", timeout=WAIT_MS)
    titles = [r["cells"][0] for r in rows(page)]
    check(titles == sorted(titles, key=str.lower), "sorting by Title orders the rows (%s)" % [t.split("\n")[0] for t in titles])

    # Filter, open an issue, then Back keeps the filters and sort.
    page.select_option(V + "select[data-filter=reason]", "stutter")
    wait_rows(page, 1)
    page.locator(V + "tr.mrIssueRow a.mrTitleLink").first.click()
    page.wait_for_selector(V + ".mediaReportsIssue", timeout=WAIT_MS)
    issue_url = page.url
    head = text(page, ".mrIssueSummary")
    check(re.match(r"^Stutter or jitter · 3 reports from 3 people · first .+, last .+$", head or ""), "the issue's header (%s)" % head)
    layout = page.evaluate("""() => { const v = document.querySelector('.page:not(.hide) .mediaReportsIssue');
        const box = (sel) => v.querySelector(sel).getBoundingClientRect();
        return { headers: [box('.mrReports > .mrH3').top, box('.mrFileSection > .mrH3').top],
                 cards: [box('.mrReports > .mrReport').top, box('.mrFile').top],
                 widths: [box('.mrReports').width, box('.mrFileSection').width],
                 triage: box('.mrTriageSection').bottom <= box('.mrReports').top,
                 select: box('.mrStatusSelect').width === box('.mrTriage .mrField').width }; }""")
    check(abs(layout["headers"][0] - layout["headers"][1]) < 1 and abs(layout["cards"][0] - layout["cards"][1]) < 1 and abs(layout["widths"][0] - layout["widths"][1]) < 2,
          "Reports and File are equal columns whose headers and first cards line up (%s)" % layout)
    check(layout["triage"] and layout["select"], "Triage, with a status menu as wide as its column, comes above the reports")
    cards = page.locator(V + ".mrReport")
    check(cards.count() == 3 and "Picture keeps jumping back" in cards.nth(2).inner_text(), "the issue lists its 3 reports newest first, with the note")
    cards.nth(2).locator("button.mrLinkBtn").click()
    details = cards.nth(2).locator("dl.mrDetails").inner_text()
    check("Living Room TV" in details and "Transcode" in details, "a report expands to its playback details")
    check("No playback was attached" in cards.nth(0).inner_text(), "a report without playback says so")
    file_text = text(page, ".mrFile")
    check("Test Movie (2026).mkv" in file_text and "H264" in file_text and "AAC" in file_text, "the File section shows the path and streams as Jellyfin has them")
    history = page.locator(V + ".mrHistory li").all_inner_texts()
    check(history[0].startswith("Reported by alice") and len(history) == 3, "History lists the reports in order with who filed them")
    options = page.locator(V + ".mrStatusSelect option").all_inner_texts()
    check("Replaced – verify" not in options, "the status menu doesn't offer Replaced – verify")
    check("Closing notifies the 3 people who reported it." in page.locator(V + ".mrTriage").inner_text(), "the status box says closing notifies the reporters")
    page.locator(V + "button", has_text="Refresh metadata").click()
    page.wait_for_function("() => (document.querySelector('.page:not(.hide) .mrMessage') || {}).textContent === 'Refresh started'", timeout=WAIT_MS)
    page.wait_for_function("() => [...document.querySelectorAll('.page:not(.hide) .mrHistory li')].some(l => l.textContent.startsWith('Refresh metadata'))", timeout=WAIT_MS)
    check(True, "Refresh metadata says it started and adds a history line")
    page.fill(V + ".mrNotes", "Re-encode planned.")
    page.locator(V + "button", has_text="Save notes").click()
    page.wait_for_function("() => [...document.querySelectorAll('.page:not(.hide) .mrHistory li')].some(l => l.textContent.startsWith('Notes changed by admin'))", timeout=WAIT_MS)
    check(page.input_value(V + ".mrNotes") == "Re-encode planned.", "notes are saved with a history line")
    page.select_option(V + ".mrStatusSelect", "Fixed")
    page.wait_for_function("() => [...document.querySelectorAll('.page:not(.hide) .mrHistory li')].some(l => l.textContent.startsWith('New → Fixed, by admin'))", timeout=WAIT_MS)
    check(True, "setting Fixed adds the status change to History")
    newer = report(alice, "Living Room TV", MOVIE, "stutter")
    page.select_option(V + ".mrStatusSelect", "New")
    page.wait_for_function("() => /newer open issue/.test((document.querySelector('.page:not(.hide) .mrMessage') || {}).textContent || '')", timeout=WAIT_MS)
    link = page.locator(V + ".mrMessage a").get_attribute("href")
    check(link and ("issue=%s" % newer) in link and page.input_value(V + ".mrStatusSelect") == "Fixed",
          "reopening while a newer issue is open is refused, linking to it")
    page.go_back()
    page.wait_for_function("() => location.hash.indexOf('issue=') === -1", timeout=WAIT_MS)
    loaded(page)
    page.wait_for_selector(V + ".mrTable", timeout=WAIT_MS)
    kept = page.evaluate("""() => { const v = document.querySelector('.page:not(.hide) .mediaReportsTriage');
        return [v.querySelector('select[data-filter=reason]').value, (v.querySelector('th[aria-sort] button') || {}).textContent]; }""")
    check(kept == ["stutter", "Title"], "Back returns to the list with the same filter and sort (%s)" % kept)
    check(rows(page)[0]["issue"] == str(newer), "the Fixed issue left the Open view; the newer one is listed")

    # A removed movie and a quick action on an episode.
    page.goto(API + PAGE + "&issue=%s" % season_issues[0])
    page.wait_for_selector(V + '.mediaReportsIssue[data-issue="%s"]' % season_issues[0], timeout=WAIT_MS)
    page.locator(V + "button", has_text="Rescan").click()
    page.wait_for_function("() => (document.querySelector('.page:not(.hide) .mrMessage') || {}).textContent === 'Rescan started'", timeout=WAIT_MS)
    page.wait_for_function("() => [...document.querySelectorAll('.page:not(.hide) .mrHistory li')].some(l => l.textContent.startsWith('Rescan: Season 1'))", timeout=WAIT_MS)
    check(True, "Rescan on an episode scans its season folder and adds a history line")

    gone_dir = os.path.join(cfg["library"], "movies", "Gone Movie (2026)")
    for name in os.listdir(gone_dir):
        os.remove(os.path.join(gone_dir, name))
    os.rmdir(gone_dir)
    ok("POST", "/Library/Refresh", ADMIN)
    deadline = time.time() + 180
    while request("GET", "/Items/%s" % GONE, ADMIN)[0] != 404:
        if time.time() > deadline:
            raise SystemExit("FAIL: the removed movie stayed in the library")
        time.sleep(5)
    page.goto(API + PAGE + "&issue=%s" % gone_issue)
    page.wait_for_selector(V + '.mediaReportsIssue[data-issue="%s"]' % gone_issue, timeout=WAIT_MS)
    file_text = text(page, ".mrFile")
    check("This title is no longer in the library." in file_text and "Gone Movie (2026).mkv" in file_text and page.locator(V + "button", has_text="Rescan").count() == 0,
          "a removed movie shows the stored path and hides the quick actions (%s)" % file_text.replace(chr(10), " | "))

    page.goto(API + PAGE + "&issue=%s" % series_issue)
    page.wait_for_selector(V + '.mediaReportsIssue[data-issue="%s"]' % series_issue, timeout=WAIT_MS)
    file_text = text(page, ".mrFile")
    check("Folder" in file_text and "Episodes\n4" in file_text and "No playback was attached" in text(page, ".mrReport"),
          "a series issue shows its folder and episode count, and no playback (%s)" % file_text.replace("\n", " | "))

    # ---- A phone's width ---------------------------------------------------------------
    phone = b.new_context(viewport={"width": 390, "height": 844}, is_mobile=True, has_touch=True).new_page()
    sign_in(phone, "admin")
    open_page(phone)
    check(no_overflow(phone), "at 390 px the Reports tab doesn't overflow the page")
    phone.goto(issue_url)
    phone.wait_for_selector(V + ".mediaReportsIssue", timeout=WAIT_MS)
    check(no_overflow(phone), "at 390 px an issue doesn't overflow the page")
    phone.close()

    # ---- A user who isn't an admin -------------------------------------------------------
    user = b.new_context(viewport={"width": 1280, "height": 900}).new_page()
    sign_in(user, "bob")
    user.goto(API + PAGE)
    user.wait_for_timeout(3000)
    check(user.locator(V + ".mediaReportsTriage .mrTable, .mediaReportsTriage .mrJustInstalled").count() == 0, "a user who isn't an admin doesn't get the page")
    user.close()

    # ---- 120 open issues: pages of 50 --------------------------------------------------
    for item in FILLERS:
        for reason in ["wont-play", "playback-error", "poor-quality", "stutter", "audio", "subtitles", "metadata", "other"]:
            report(carol, "Carol Phone", item, reason)
    open_page(page)
    wait_rows(page, 50)
    check(text(page, ".mrPageOf") == "Page 1 of 3", "120 more open issues give 50 rows on page 1 of 3 (%s)" % text(page, ".mrPageOf"))
    page.locator(V + ".mrPaging button", has_text="Next").click()
    page.wait_for_function("() => (document.querySelector('.page:not(.hide) .mrPageOf') || {}).textContent === 'Page 2 of 3'", timeout=WAIT_MS)
    page.locator(V + ".mrPaging button", has_text="Next").click()
    page.wait_for_function("() => (document.querySelector('.page:not(.hide) .mrPageOf') || {}).textContent === 'Page 3 of 3'", timeout=WAIT_MS)
    check(len(rows(page)) > 0 and page.locator(V + ".mrPaging button", has_text="Next").is_disabled(), "Next walks to the last page")

    check(not errors, "no script errors on the page" + (": " + "; ".join(errors) if errors else ""))

    # ---- Couldn't load: a database newer than this plugin ------------------------------
    container = cfg["container"]
    newer_db = os.path.join(tempfile.mkdtemp(), "media-reports.db")
    with sqlite3.connect(newer_db) as db:
        db.execute("PRAGMA user_version = 99")
    subprocess.run(["docker", "stop", "-t", "30", container], check=True, capture_output=True)
    subprocess.run(["docker", "cp", newer_db, container + ":/config/data/media-reports/media-reports.db"], check=True, capture_output=True)
    subprocess.run(["docker", "start", container], check=True, capture_output=True)
    # A restarted container gets a new host port.
    port = subprocess.run(["docker", "port", container, "8096/tcp"], check=True, capture_output=True, text=True).stdout.split()[0].rsplit(":", 1)[1]
    API = "http://127.0.0.1:" + port
    deadline = time.time() + 180
    while True:
        try:
            if request("GET", "/System/Info/Public")[0] == 200:
                break
        except Exception:  # noqa: BLE001
            pass
        if time.time() > deadline:
            raise SystemExit("FAIL: the server did not come back")
        time.sleep(3)
    time.sleep(5)
    sign_in(page, "admin")
    open_page(page)
    page.wait_for_selector(V + ".mrCouldNotLoad", timeout=WAIT_MS)
    check("media-reports.backup.db" in text(page, ".mrCouldNotLoad") and page.locator(V + ".mrCouldNotLoad button", has_text="Try again").count() == 1,
          "when the database didn't open, the tab shows the couldn't-load state naming the daily copy")
    b.close()

if failures:
    print("FAIL: %d check(s) failed" % len(failures))
    sys.exit(1)
print("PASS: the Media Reports page works in the Dashboard")
PY
