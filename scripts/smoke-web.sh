#!/usr/bin/env bash
# Starts a real Jellyfin 12 server in Docker with the plugin, a generated movie and a short
# series, then drives Jellyfin Web in a headless browser (Playwright for Python) at desktop
# width, phone width and in the TV layout, checking where the Report button appears.
#
# Usage: scripts/smoke-web.sh [--without-plugin] [--keep]
#   --without-plugin  start the server with no plugin; the script must fail
#   --keep            leave the server running and print its address (for local work)
#
# Environment:
#   JELLYFIN_IMAGE       image to run (default jellyfin/jellyfin:12)
#   SMOKE_TIMEOUT        seconds to wait for each step (default 180)
#   PLAYWRIGHT_CHANNEL   browser channel, e.g. msedge (default: Playwright's Chromium)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

image="${JELLYFIN_IMAGE:-jellyfin/jellyfin:12}"
timeout="${SMOKE_TIMEOUT:-180}"
with_plugin=1
keep=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --without-plugin) with_plugin=0; shift ;;
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
  py -c "import json,sys; d=json.load(sys.stdin); v=$1; print('' if v is None else v)"
}

work="$repo_root/artifacts/smoke-web"
meta="src/Jellyfin.Plugin.MediaReports/meta.json"
plugin_version="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$meta")"
container="media-reports-web-$$"

if [[ $keep -eq 0 ]]; then
  trap 'docker rm -f "$container" >/dev/null 2>&1 || true' EXIT
fi

echo "== dotnet publish"
rm -rf "$work/publish" "$work/stage" "$work/results"
mkdir -p "$work/results"
dotnet publish src/Jellyfin.Plugin.MediaReports --nologo -c Release -o "$work/publish" >/dev/null
mkdir -p "$work/stage/plugins/MediaReports_${plugin_version}"
cp -R "$work/publish/." "$work/stage/plugins/MediaReports_${plugin_version}/"

docker pull --quiet "$image" >/dev/null
media="$work/media"
clip() {  # clip <path under media> <seconds>
  local out="$media/$1"
  [[ -s "$out" ]] && return 0
  mkdir -p "$(dirname "$out")"
  MSYS_NO_PATHCONV=1 docker run --rm -v "$(host_path "$media"):/out" \
    --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg "$image" -hide_banner -loglevel error \
    -f lavfi -i testsrc2=size=1280x720:rate=24 -f lavfi -i sine=frequency=440:sample_rate=48000 \
    -t "$2" -c:v libx264 -preset veryfast -b:v 2M -pix_fmt yuv420p -c:a aac -b:a 128k -ac 2 "/out/$1"
}
echo "== generate a movie and a two-episode series"
clip "movies/Test Movie (2026)/Test Movie (2026).mkv" 60
clip "shows/Test Series (2026)/Season 01/Test Series (2026) S01E01.mkv" 20
clip "shows/Test Series (2026)/Season 01/Test Series (2026) S01E02.mkv" 20

echo "== start $image (plugin installed: $with_plugin)"
MSYS_NO_PATHCONV=1 docker create --name "$container" -p 127.0.0.1::8096 \
  -v "$(host_path "$media/movies"):/media/movies:ro" -v "$(host_path "$media/shows"):/media/shows:ro" "$image" >/dev/null
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

auth="MediaBrowser Client=\"media-reports-smoke\", Device=\"smoke-script\", DeviceId=\"media-reports-web\", Version=\"1.0.0\""
token=""
call() {
  local method="$1" path="$2" body="${3:-}"
  curl -sS --fail-with-body -X "$method" "$api$path" \
    -H "Authorization: $auth${token:+, Token=\"$token\"}" -H "Content-Type: application/json" \
    ${body:+--data "$body"} || { echo >&2; fail "$method $path was refused"; }
}

echo "== complete the startup wizard, add a user and the libraries"
call POST /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}'
call GET /Startup/User >/dev/null
call POST /Startup/User '{"Name":"admin","Password":"admin-password"}'
call POST /Startup/Complete
token="$(call POST /Users/AuthenticateByName '{"Username":"admin","Pw":"admin-password"}' | json "d['AccessToken']")"
call POST /Users/New '{"Name":"alice","Password":"alice-password"}' >/dev/null
call POST "/Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=false" '{"LibraryOptions":{"PathInfos":[{"Path":"/media/movies"}]}}'
call POST "/Library/VirtualFolders?name=Shows&collectionType=tvshows&refreshLibrary=true" '{"LibraryOptions":{"PathInfos":[{"Path":"/media/shows"}]}}'

first_id() {  # first_id <item type>
  call GET "/Items?IncludeItemTypes=$1&Recursive=true&Limit=1&SortBy=SortName" | json "d['Items'][0]['Id'] if d['Items'] else None"
}
movie="" series="" season="" episode=""
deadline=$((SECONDS + timeout))
until [[ -n "$movie" && -n "$series" && -n "$season" && -n "$episode" ]]; do
  (( SECONDS < deadline )) || fail "the library did not show the movie and the series"
  movie="$(first_id Movie)"; series="$(first_id Series)"; season="$(first_id Season)"; episode="$(first_id Episode)"
  [[ -n "$movie" && -n "$series" && -n "$season" && -n "$episode" ]] || { call POST /Library/Refresh >/dev/null; sleep 10; }
done
echo "items: movie $movie, series $series, season $season, episode $episode"

cat >"$work/results/server.json" <<EOF
{"api": "$api", "plugin_id": "$(sed -n 's/.*"guid": *"\([^"]*\)".*/\1/p' "$meta")", "admin_token": "$token",
 "user": "alice", "password": "alice-password",
 "items": {"Movie": "$movie", "Series": "$series", "Season": "$season", "Episode": "$episode"}}
EOF

if [[ $keep -eq 1 ]]; then
  echo "server kept: $api (container $container); details in $work/results/server.json"
  exit 0
fi

echo "== drive Jellyfin Web"
# Needs Playwright for Python (the version CI pins) and a Chromium it can launch.
py - "$work/results/server.json" <<'PY'
import json
import os
import sys
import urllib.request

from playwright.sync_api import sync_playwright

cfg = json.load(open(sys.argv[1], encoding="utf-8"))
API = cfg["api"]
ITEMS = cfg["items"]
LABEL = "Report a problem"
WAIT_MS = 15000
failures = []


def server_id():
    with urllib.request.urlopen(API + "/System/Info/Public") as response:
        return json.load(response)["Id"]


def set_reporting(on):
    auth = 'MediaBrowser Client="media-reports-smoke", Device="smoke", DeviceId="media-reports-web", Version="1.0.0", Token="%s"' % cfg["admin_token"]
    url = "%s/Plugins/%s/Configuration" % (API, cfg["plugin_id"])
    with urllib.request.urlopen(urllib.request.Request(url, headers={"Authorization": auth})) as response:
        config = json.load(response)
    config["ReportingEnabled"] = on
    request = urllib.request.Request(url, data=json.dumps(config).encode(), method="POST",
                                     headers={"Authorization": auth, "Content-Type": "application/json"})
    urllib.request.urlopen(request).close()


def check(ok, what):
    print(("ok   " if ok else "FAIL ") + what)
    if not ok:
        failures.append(what)


def sign_in(page):
    page.goto(API + "/web/#/login")
    page.wait_for_selector("#txtManualName, button.btnManual", timeout=WAIT_MS)
    if not page.locator("#txtManualName").is_visible():
        page.locator("button.btnManual").first.click()
    page.fill("#txtManualName", cfg["user"])
    page.fill("#txtManualPassword", cfg["password"])
    page.locator("#txtManualPassword").press("Enter")
    page.wait_for_function("() => window.ApiClient && window.ApiClient.getCurrentUserId && window.ApiClient.getCurrentUserId()", timeout=WAIT_MS)
    # Signing in ends on Home; navigating before it lands would be overridden.
    page.wait_for_function("() => location.hash.indexOf('home') !== -1", timeout=WAIT_MS)
    page.wait_for_timeout(1000)


DETAIL_BUTTON = ".page:not(.hide) .mainDetailButtons .mediaReportsButton"
PLAYER_BUTTON = ".osdControls .buttons .mediaReportsButton"

# Where our button sits and what it looks like, read in the page.
DESCRIBE = """sel => {
  const buttons = [...document.querySelectorAll(sel)].filter(b => b.offsetParent !== null);
  return buttons.map(b => ({
    after: b.previousElementSibling ? b.previousElementSibling.className : '',
    title: b.title, aria: b.getAttribute('aria-label'), id: b.getAttribute('data-id'),
    flag: !!b.querySelector('.material-icons.flag')
  }));
}"""


def placed(page, selector, item_id, where):
    try:
        # Wait out page transitions: the previous page's button can still be showing.
        page.wait_for_function("([d, sel, id]) => { const f = eval(d)(sel); return f.length === 1 && f[0].id === id; }",
                               arg=[DESCRIBE, selector, item_id], timeout=WAIT_MS)
    except Exception:  # noqa: BLE001 - a timeout is the finding
        check(False, where + ": the Report button appears")
        return
    found = page.evaluate(DESCRIBE, selector)
    check(len(found) == 1, where + ": exactly one Report button (%d)" % len(found))
    if found:
        b = found[0]
        check("btnUserRating" in b["after"], where + ": it sits right after the favourite button")
        check(b["title"] == LABEL and b["aria"] == LABEL, where + ': it is labelled "%s"' % LABEL)
        check(b["flag"], where + ": it shows the flag icon")
        check(b["id"] == item_id, where + ": it is for this title")


def run(p, layout, context_args):
    launch = {"channel": os.environ["PLAYWRIGHT_CHANNEL"]} if os.environ.get("PLAYWRIGHT_CHANNEL") else {}
    browser = p.chromium.launch(**launch)
    context = browser.new_context(**context_args)
    page = context.new_page()
    errors = []
    page.on("pageerror", lambda e: errors.append(str(e)) if "reporter.js" in (e.stack or "") else None)
    page.on("console", lambda m: errors.append(m.text) if "[Media Reports]" in m.text else None)
    sign_in(page)
    if layout == "tv":
        page.evaluate("localStorage.setItem('layout', 'tv')")
        page.reload()
        page.wait_for_function("() => document.documentElement.classList.contains('layout-tv')", timeout=WAIT_MS)
    sid = server_id()
    details = lambda item: "%s/web/#/details?id=%s&serverId=%s" % (API, item, sid)

    check(page.locator('script[src*="MediaReports/web/reporter.js"]').count() == 1, layout + ": the page loads reporter.js once")
    for kind in ("Movie", "Episode", "Season", "Series"):
        page.goto(details(ITEMS[kind]))
        placed(page, DETAIL_BUTTON, ITEMS[kind], "%s, %s page" % (layout, kind.lower()))

    # Moving between pages adds the button to each page once.
    for kind in ("Series", "Movie", "Series", "Movie"):
        page.goto(details(ITEMS[kind]))
    placed(page, DETAIL_BUTTON, ITEMS["Movie"], layout + ", movie page after moving between pages")

    page.evaluate("""() => { window.__reported = null;
        document.addEventListener('mediareports:report', e => { window.__reported = e.detail; }); }""")
    if page.locator(DETAIL_BUTTON).count():
        page.locator(DETAIL_BUTTON).first.click()
    reported = page.evaluate("() => window.__reported")
    check(bool(reported) and reported.get("itemId") == ITEMS["Movie"] and reported.get("fromPlayer") is False,
          layout + ": choosing the button asks to report this title")

    page.locator(".page:not(.hide) .mainDetailButtons .btnPlay").first.click()
    page.wait_for_selector(".osdControls", state="attached", timeout=WAIT_MS)
    for _ in range(4):
        page.mouse.move(300, 300)
        page.mouse.move(500, 400)
        page.keyboard.press("ArrowUp") if layout == "tv" else None
        page.wait_for_timeout(400)
    placed(page, PLAYER_BUTTON, ITEMS["Movie"], layout + ", player")
    page.keyboard.press("Escape")

    check(not errors, layout + ": no errors from the Report button script" + (": " + "; ".join(errors) if errors else ""))
    browser.close()


PHONE = {"viewport": {"width": 390, "height": 844}, "is_mobile": True, "has_touch": True, "device_scale_factor": 2,
         "user_agent": "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Mobile Safari/537.36"}

with sync_playwright() as p:
    run(p, "desktop", {"viewport": {"width": 1440, "height": 900}})
    run(p, "phone", PHONE)
    run(p, "tv", {"viewport": {"width": 1280, "height": 720}})

    # Switched off: the page carries no script and shows no button, without a restart.
    set_reporting(False)
    try:
        launch = {"channel": os.environ["PLAYWRIGHT_CHANNEL"]} if os.environ.get("PLAYWRIGHT_CHANNEL") else {}
        browser = p.chromium.launch(**launch)
        page = browser.new_context(viewport={"width": 1440, "height": 900}).new_page()
        sign_in(page)
        page.goto("%s/web/#/details?id=%s&serverId=%s" % (API, ITEMS["Movie"], server_id()))
        page.wait_for_selector(".page:not(.hide) .mainDetailButtons .btnUserRating", timeout=WAIT_MS)
        page.wait_for_timeout(2000)
        check(page.locator('script[src*="reporter.js"]').count() == 0, "reporting off: the page carries no reporter.js")
        check(page.locator(".mediaReportsButton").count() == 0, "reporting off: no Report button")
        browser.close()
    finally:
        set_reporting(True)

if failures:
    print("FAIL: %d check(s) failed" % len(failures))
    sys.exit(1)
print("PASS: the Report button appears where it should in Jellyfin Web")
PY
