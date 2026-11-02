#!/usr/bin/env bash
# Starts a real Jellyfin 12 server in Docker with the plugin, a generated movie and a short
# series, then drives Jellyfin Web in a headless browser (Playwright for Python) at desktop
# width, phone width and in the TV layout, as one user each: where the Report button
# appears, and the report dialog it opens, from sending to what the server stored.
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
for user in alice bob carol; do
  call POST /Users/New "{\"Name\":\"$user\",\"Password\":\"$user-password\"}" >/dev/null
done
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
REASONS = ["Won't play", "Playback error", "Poor quality", "Stutter or jitter", "Audio problem", "Subtitle problem", "Wrong metadata", "Other"]
NOTE = "Blurry in dark scenes."
WAIT_MS = 15000
failures = []


def admin(path, body=None):
    auth = 'MediaBrowser Client="media-reports-smoke", Device="smoke", DeviceId="media-reports-web", Version="1.0.0", Token="%s"' % cfg["admin_token"]
    headers = {"Authorization": auth}
    data = None
    if body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    request = urllib.request.Request(API + path, data=data, method="POST" if body is not None else "GET", headers=headers)
    with urllib.request.urlopen(request) as response:
        text = response.read()
    return json.loads(text) if text else None


def configure(**changes):
    path = "/Plugins/%s/Configuration" % cfg["plugin_id"]
    config = admin(path)
    config.update(changes)
    admin(path, config)
    return config


def server_id():
    with urllib.request.urlopen(API + "/System/Info/Public") as response:
        return json.load(response)["Id"]


def check(ok, what):
    print(("ok   " if ok else "FAIL ") + what)
    if not ok:
        failures.append(what)


def browser(p):
    launch = {"channel": os.environ["PLAYWRIGHT_CHANNEL"]} if os.environ.get("PLAYWRIGHT_CHANNEL") else {}
    return p.chromium.launch(**launch)


def sign_in(page, user):
    page.goto(API + "/web/#/login")
    page.wait_for_selector("#txtManualName, button.btnManual", timeout=WAIT_MS)
    if not page.locator("#txtManualName").is_visible():
        page.locator("button.btnManual").first.click()
    page.fill("#txtManualName", user)
    page.fill("#txtManualPassword", user + "-password")
    page.locator("#txtManualPassword").press("Enter")
    page.wait_for_function("() => window.ApiClient && window.ApiClient.getCurrentUserId && window.ApiClient.getCurrentUserId()", timeout=WAIT_MS)
    # Signing in ends on Home; navigating before it lands would be overridden.
    page.wait_for_function("() => location.hash.indexOf('home') !== -1", timeout=WAIT_MS)
    page.wait_for_timeout(1000)


DETAIL_BUTTON = ".page:not(.hide) .mainDetailButtons .mediaReportsButton"
PLAYER_BUTTON = ".osdControls .buttons .mediaReportsButton"
DIALOG = ".mediaReportsDialog"

# Where our button sits and what it looks like, read in the page.
DESCRIBE = """sel => {
  const buttons = [...document.querySelectorAll(sel)].filter(b => b.offsetParent !== null);
  return buttons.map(b => ({
    after: b.previousElementSibling ? b.previousElementSibling.className : '',
    title: b.title, aria: b.getAttribute('aria-label'), id: b.getAttribute('data-id'),
    flag: !!b.querySelector('.material-icons.flag')
  }));
}"""

# The open dialog: its box, layout, reasons, buttons and messages.
DIALOG_STATE = """() => {
  const d = document.querySelector('.mediaReportsDialog');
  if (!d || d.offsetParent === null && getComputedStyle(d).position !== 'fixed') return null;
  const r = d.getBoundingClientRect();
  const chips = [...d.querySelectorAll('.mediaReportsReason')];
  const send = d.querySelector('.mediaReportsSend');
  const text = s => { const e = d.querySelector(s); return e && e.offsetParent !== null ? e.textContent.trim() : null; };
  return {
    cls: d.className, x: r.left, y: r.top, w: r.width, h: r.height, vw: innerWidth, vh: innerHeight,
    role: d.getAttribute('role'), modal: d.getAttribute('aria-modal'), label: d.getAttribute('aria-label'),
    heading: text('.mediaReportsHeading'), name: text('.mediaReportsTitle'),
    reasons: chips.map(c => c.textContent.trim()),
    pressed: chips.filter(c => c.getAttribute('aria-pressed') === 'true').map(c => c.textContent.trim()),
    disabled: chips.filter(c => c.disabled).map(c => c.textContent.trim()),
    note: d.querySelector('.mediaReportsNote') ? d.querySelector('.mediaReportsNote').value : null,
    noteShown: !!d.querySelector('.mediaReportsNote') && d.querySelector('.mediaReportsNote').offsetParent !== null,
    send: send ? send.textContent.trim() : null, sendDisabled: send ? send.disabled : null,
    playback: text('.mediaReportsPlayback'), error: text('.mediaReportsError'), info: text('.mediaReportsInfo'),
    sent: text('.mediaReportsSent'), focusInside: d.contains(document.activeElement),
    focus: document.activeElement ? (document.activeElement.textContent || '').trim() : ''
  };
}"""


def dialog(page):
    return page.evaluate(DIALOG_STATE)


def wait_dialog(page, open_=True):
    page.wait_for_function("open => (document.querySelector('.mediaReportsDialog') !== null) === open", arg=open_, timeout=WAIT_MS)
    page.wait_for_timeout(300)


def placed(page, selector, item_id, where):
    try:
        # Wait out page transitions: the previous page's button can still be showing.
        page.wait_for_function("([d, sel, id]) => { const f = eval(d)(sel); return f.length === 1 && f[0].id === id; }",
                               arg=[DESCRIBE, selector, item_id], timeout=WAIT_MS)
    except Exception:  # noqa: BLE001 - a timeout is the finding
        check(False, where + ": the Report button appears")
        return False
    found = page.evaluate(DESCRIBE, selector)
    check(len(found) == 1, where + ": exactly one Report button (%d)" % len(found))
    b = found[0]
    check("btnUserRating" in b["after"], where + ": it sits right after the favourite button")
    check(b["title"] == LABEL and b["aria"] == LABEL, where + ': it is labelled "%s"' % LABEL)
    check(b["flag"], where + ": it shows the flag icon")
    check(b["id"] == item_id, where + ": it is for this title")
    return True


def open_dialog(page, selector, layout, where):
    if layout == "tv":
        page.locator(selector).first.focus()
        page.keyboard.press("Enter")
    else:
        page.locator(selector).first.click()
    try:
        wait_dialog(page)
        return True
    except Exception:  # noqa: BLE001 - a timeout is the finding
        check(False, where + ": choosing the Report button opens the dialog")
        return False


def pick(page, reason, layout):
    if layout != "tv":
        page.locator(DIALOG + " .mediaReportsReason", has_text=reason).click()
        return
    for _ in range(12):
        if page.evaluate("() => (document.activeElement.textContent || '').trim()") == reason:
            break
        page.keyboard.press("ArrowRight")
    page.keyboard.press("Enter")


def send(page, layout):
    if layout != "tv":
        page.locator(DIALOG + " .mediaReportsSend").click()
        return
    for _ in range(12):
        focused = page.evaluate("() => document.activeElement.className")
        if "mediaReportsSend" in focused:
            break
        # Down from a reason on the right can land on Cancel; Send is to its left.
        page.keyboard.press("ArrowLeft" if "mediaReportsCancel" in focused else "ArrowDown")
    page.keyboard.press("Enter")


def check_layout(state, layout, where):
    if layout == "desktop":
        check("mr-desktop" in state["cls"] and abs(state["w"] - 640) <= 2 and abs(state["x"] - (state["vw"] - state["w"]) / 2) <= 2,
              where + ": a centred 640 px panel (%dx%d at %d)" % (state["w"], state["h"], state["x"]))
    elif layout == "phone":
        check("mr-phone" in state["cls"] and state["w"] == state["vw"] and state["h"] == state["vh"], where + ": full screen (%dx%d)" % (state["w"], state["h"]))
    elif layout == "tv":
        check("mr-tv" in state["cls"] and state["w"] == state["vw"] and state["h"] == state["vh"], where + ": full screen (%dx%d)" % (state["w"], state["h"]))
    else:
        check("mr-panel" in state["cls"] and abs(state["x"] + state["w"] - state["vw"]) <= 1 and state["h"] == state["vh"] and state["w"] <= 480,
              where + ": a side panel at the right (%dx%d)" % (state["w"], state["h"]))
    check(state["role"] == "dialog" and state["modal"] == "true", where + ": it is a modal dialog to assistive tech")


REMOTE_BUTTONS_JS = """() => {
  const d = document.querySelector('.mediaReportsDialog');
  const send = d.querySelector('.mediaReportsSend'), cancel = d.querySelector('.mediaReportsCancel');
  const look = b => { const c = getComputedStyle(b); return c.backgroundColor + ' ' + c.color; };
  const rs = send.getBoundingClientRect(), rc = cancel.getBoundingClientRect();
  cancel.focus();
  const sendIdle = look(send), cancelFocused = look(cancel);
  send.focus();
  const sendFocused = look(send), cancelIdle = look(cancel);
  const rows = [...d.querySelectorAll('.mediaReportsReason')].map(r => r.getBoundingClientRect());
  const lefts = [...new Set(rows.map(r => Math.round(r.left)))];
  const usesGap = [...d.querySelectorAll('*')].some(e => ['flex', 'grid'].includes(getComputedStyle(e).display)
    && ['rowGap', 'columnGap'].some(g => !['normal', '0px'].includes(getComputedStyle(e)[g])));
  return {stacked: rc.top >= rs.bottom && Math.abs(rc.left - rs.left) < 1 && Math.abs(rc.width - rs.width) < 1,
          apart: Math.round(rc.top - rs.bottom), usesGap, columns: lefts.length,
          even: rows.every(r => Math.abs(r.width - rows[0].width) < 1 && Math.abs(r.height - rows[0].height) < 1),
          sendIdle, cancelFocused, sendFocused, cancelIdle};
}"""


def remote_buttons(page, where):
    """With a remote, Send and Cancel are stacked and only the focused one is highlighted."""
    r = page.evaluate(REMOTE_BUTTONS_JS)
    check(r["stacked"] and r["apart"] >= 8, where + ": Send and Cancel are stacked with space between them (%dpx)" % r["apart"])
    check(r["columns"] == 2 and r["even"], where + ": the reasons are two columns of equal rows")
    check(not r["usesGap"], where + ": spacing does not rely on flex gap, which older webOS browsers lack")
    check(r["sendIdle"] == r["cancelIdle"] and r["sendFocused"] == r["cancelFocused"] and r["sendIdle"] != r["sendFocused"],
          where + ": only the focused one of Send and Cancel is highlighted (%s)" % r)


STYLE_JS = """sel => { const b = document.querySelector(sel); const c = getComputedStyle(b);
  return [c.color, c.backgroundColor, c.transform].join(' '); }"""


def focused_style(page, selector):
    """The button's look once focused and its transition has settled."""
    page.evaluate("sel => document.querySelector(sel).focus()", selector)
    page.wait_for_timeout(600)
    return page.evaluate(STYLE_JS, selector)


def report_from_page(page, layout):
    """The page dialog: layout, reasons, focus, couldn't send, sending, sent, already reported, note too long, BACK."""
    where = layout + ", dialog on the movie page"
    if not open_dialog(page, DETAIL_BUTTON, layout, where):
        return
    s = dialog(page)
    check_layout(s, layout, where)
    check(s["heading"] == LABEL and s["name"] and "Test Movie" in s["name"], where + ": it names the title")
    check(s["reasons"] == REASONS, where + ": the eight reasons in order (%s)" % s["reasons"])
    check(s["sendDisabled"] is True and s["send"] == "Send report", where + ": Send is disabled until a reason is picked")
    check(s["focusInside"], where + ": focus moves into the dialog")
    check(s["playback"] is None, where + ": no playback is mentioned before this user played it")
    check(s["noteShown"] == (layout != "tv"), where + (": no note field on TV" if layout == "tv" else ": a note field"))

    if layout != "tv":
        for _ in range(25):
            page.keyboard.press("Tab")
        check(dialog(page)["focusInside"], where + ": Tab keeps focus inside the dialog")
    page.keyboard.press("Escape")
    wait_dialog(page, False)
    check("mediaReportsButton" in page.evaluate("() => document.activeElement.className"), where + ": closing returns focus to the Report button")

    open_dialog(page, DETAIL_BUTTON, layout, where)
    pick(page, "Stutter or jitter", layout)
    s = dialog(page)
    check(s["pressed"] == ["Stutter or jitter"] and s["sendDisabled"] is False, where + ": picking a reason enables Send")
    if layout != "tv":
        page.fill(DIALOG + " .mediaReportsNote", NOTE)
    else:
        remote_buttons(page, where)

    # Couldn't send: the report request fails in the browser.
    page.route("**/MediaReports/Report", lambda route: route.abort())
    send(page, layout)
    page.wait_for_function("() => { const e = document.querySelector('.mediaReportsError'); return !!e && e.style.display !== 'none' && !!e.textContent; }", timeout=WAIT_MS)
    s = dialog(page)
    check(s["error"] and "wasn't sent" in s["error"], where + ": could not send shows an alert")
    check(s["pressed"] == ["Stutter or jitter"] and (layout == "tv" or s["note"] == NOTE), where + ": the reason and note are kept")
    check(s["send"] == "Try again", where + ': Send becomes "Try again"')
    page.unroute("**/MediaReports/Report")

    # Sending, then sent: the Send button's changes are recorded in the page.
    page.evaluate("""() => { const b = document.querySelector('.mediaReportsSend'); window.__sendStates = [];
        new MutationObserver(() => window.__sendStates.push([b.textContent, b.disabled]))
          .observe(b, { childList: true, characterData: true, subtree: true, attributes: true }); }""")
    send(page, layout)
    page.wait_for_function("() => !!document.querySelector('.mediaReportsSent')", timeout=WAIT_MS)
    states = page.evaluate("() => window.__sendStates")
    check(any(t.startswith("Sending") and disabled for t, disabled in states), where + ': Send reads "Sending" and is disabled while the server answers')
    s = dialog(page)
    check(s["sent"] and s["sent"].startswith("Report sent") and "My Reports" in s["sent"], where + ': "Report sent", pointing at My Reports')
    page.keyboard.press("Enter") if layout == "tv" else page.locator(DIALOG + " .mediaReportsDone").click()
    wait_dialog(page, False)

    # Already reported: the earlier reason is named and blocked; the others stay open.
    open_dialog(page, DETAIL_BUTTON, layout, where)
    s = dialog(page)
    check(s["info"] and "Stutter or jitter" in s["info"], where + ": it says this title was already reported for Stutter or jitter")
    check(s["disabled"] == ["Stutter or jitter"], where + ": that reason can't be picked again, the others can (%s)" % s["disabled"])

    if layout != "tv":
        pick(page, "Audio problem", layout)
        page.fill(DIALOG + " .mediaReportsNote", "x" * 1001)
        send(page, layout)
        page.wait_for_function("() => { const e = document.querySelector('.mediaReportsError'); return !!e && e.style.display !== 'none' && !!e.textContent; }", timeout=WAIT_MS)
        check("too long" in (dialog(page)["error"] or ""), where + ": a note over 1,000 characters is refused as too long")
        page.keyboard.press("Escape")
    else:
        page.keyboard.press("Backspace")
    wait_dialog(page, False)
    check(True, where + (": BACK closes the dialog" if layout == "tv" else ": ESC closes the dialog"))


def report_from(page, kind, reason, layout, sid):
    page.goto("%s/web/#/details?id=%s&serverId=%s" % (API, ITEMS[kind], sid))
    if not placed(page, DETAIL_BUTTON, ITEMS[kind], "%s, %s page" % (layout, kind.lower())):
        return
    where = "%s, dialog on the %s page" % (layout, kind.lower())
    if not open_dialog(page, DETAIL_BUTTON, layout, where):
        return
    pick(page, reason, layout)
    send(page, layout)
    page.wait_for_function("() => !!document.querySelector('.mediaReportsSent')", timeout=WAIT_MS)
    check(True, where + ": the report is sent")
    page.keyboard.press("Escape")
    wait_dialog(page, False)


def report_from_player(page, layout, sid):
    page.goto("%s/web/#/details?id=%s&serverId=%s" % (API, ITEMS["Movie"], sid))
    placed(page, DETAIL_BUTTON, ITEMS["Movie"], layout + ", movie page before playing")
    page.locator(".page:not(.hide) .mainDetailButtons .btnPlay").first.click()
    page.wait_for_selector(".osdControls", state="attached", timeout=WAIT_MS)
    page.wait_for_function("() => { const v = document.querySelector('video'); return v && !v.paused && v.currentTime > 1; }", timeout=WAIT_MS * 2)
    for _ in range(4):
        page.mouse.move(300, 300)
        page.mouse.move(500, 400)
        if layout == "tv":
            page.keyboard.press("ArrowUp")
        page.wait_for_timeout(400)
    if not placed(page, PLAYER_BUTTON, ITEMS["Movie"], layout + ", player"):
        return
    if layout == "tv":
        f = {"idle": page.evaluate(STYLE_JS, PLAYER_BUTTON), "fav": focused_style(page, ".osdControls .buttons .btnUserRating"),
             "ours": focused_style(page, PLAYER_BUTTON)}
        check(f["ours"] == f["fav"] and f["ours"] != f["idle"], "tv, player: the Report button is highlighted on focus like the favourite button (%s)" % f)
    where = layout + ", dialog in the player"
    if not open_dialog(page, PLAYER_BUTTON, "desktop" if layout != "tv" else "tv", where):
        return
    s = dialog(page)
    check_layout(s, "panel", where)
    check(s["playback"] and "playing right now" in s["playback"], where + ": it says the playback right now is attached")
    pick(page, "Poor quality", layout)
    if layout != "tv":
        page.locator(DIALOG + " .mediaReportsNote").type("Looks soft.")
    else:
        remote_buttons(page, where)
    send(page, layout)
    page.wait_for_function("() => !!document.querySelector('.mediaReportsSent')", timeout=WAIT_MS)
    check(not page.evaluate("() => document.querySelector('video').paused"), where + ": playback is not paused")
    try:
        wait_dialog(page, False)
        page.wait_for_timeout(0)
        closed = True
    except Exception:  # noqa: BLE001
        closed = False
    check(closed, where + ": the panel closes by itself after it is sent")
    check(not page.evaluate("() => document.querySelector('video').paused"), where + ": playback carries on after the panel closes")
    page.keyboard.press("Escape")
    page.go_back()
    page.wait_for_timeout(1500)

    page.goto("%s/web/#/details?id=%s&serverId=%s" % (API, ITEMS["Movie"], sid))
    placed(page, DETAIL_BUTTON, ITEMS["Movie"], layout + ", movie page after playing")
    open_dialog(page, DETAIL_BUTTON, layout, layout + ", dialog after playing")
    s = dialog(page) or {}
    check((s.get("playback") or "").startswith("How you last played it, on "), layout + ", dialog after playing: it names the device and day of the last playback")
    page.keyboard.press("Escape")


def run(p, layout, user, context_args):
    b = browser(p)
    page = b.new_context(**context_args).new_page()
    errors = []
    page.on("pageerror", lambda e: errors.append(str(e)) if "reporter.js" in (e.stack or "") else None)
    page.on("console", lambda m: errors.append(m.text) if "[Media Reports]" in m.text else None)
    sign_in(page, user)
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
    for kind in ("Series", "Movie", "Series", "Movie"):
        page.goto(details(ITEMS[kind]))
    if placed(page, DETAIL_BUTTON, ITEMS["Movie"], layout + ", movie page after moving between pages"):
        report_from_page(page, layout)

    report_from(page, "Episode", "Subtitle problem", layout, sid)
    report_from(page, "Series", "Other", layout, sid)
    report_from_player(page, layout, sid)

    check(not errors, layout + ": no errors from the Report button script" + (": " + "; ".join(errors) if errors else ""))
    b.close()


PHONE = {"viewport": {"width": 390, "height": 844}, "is_mobile": True, "has_touch": True, "device_scale_factor": 2,
         "user_agent": "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Mobile Safari/537.36"}

with sync_playwright() as p:
    run(p, "desktop", "alice", {"viewport": {"width": 1440, "height": 900}})
    run(p, "phone", "bob", PHONE)
    run(p, "tv", "carol", {"viewport": {"width": 1280, "height": 720}})

    # What the dialogs stored.
    export = admin("/MediaReports/Admin/Export")
    users = {"alice": "desktop", "bob": "phone", "carol": "tv"}
    for name, layout in users.items():
        mine = [r for r in export["reports"] if r["reporterName"] == name]
        by = lambda reason: [r for r in mine if r["reasonCode"] == reason]
        movie = by("stutter")
        check(len(movie) == 1 and movie[0]["itemId"].replace("-", "") == ITEMS["Movie"] and (layout == "tv" or movie[0]["note"] == NOTE),
              layout + ": the movie report is stored with its reason" + ("" if layout == "tv" else " and note"))
        check(not by("audio"), layout + ": the refused report was not stored")
        episode = by("subtitles")
        check(len(episode) == 1 and episode[0]["itemType"] == "Episode" and episode[0]["itemId"].replace("-", "") == ITEMS["Episode"],
              layout + ": the episode report names the episode")
        series = by("other")
        check(len(series) == 1 and series[0]["itemType"] == "Series" and series[0]["snapshot"] is None,
              layout + ": the series report names the series and has no playback")
        player = by("poor-quality")
        check(len(player) == 1 and player[0]["snapshot"] and player[0]["snapshot"]["state"] == "Playing",
              layout + ": the player report carries the live playback")

    # A retired reason isn't offered; switched off, the page carries no script and no button.
    original = admin("/Plugins/%s/Configuration" % cfg["plugin_id"])
    try:
        configure(Reasons=[dict(r, Retired=(r["Code"] == "metadata")) for r in original["Reasons"]])
        b = browser(p)
        page = b.new_context(viewport={"width": 1440, "height": 900}).new_page()
        sign_in(page, "alice")
        page.goto("%s/web/#/details?id=%s&serverId=%s" % (API, ITEMS["Season"], server_id()))
        if placed(page, DETAIL_BUTTON, ITEMS["Season"], "retired reason, season page") and open_dialog(page, DETAIL_BUTTON, "desktop", "retired reason"):
            check("Wrong metadata" not in dialog(page)["reasons"], "retired reason: \"Wrong metadata\" is not offered")
        b.close()

        configure(ReportingEnabled=False)
        b = browser(p)
        page = b.new_context(viewport={"width": 1440, "height": 900}).new_page()
        sign_in(page, "alice")
        page.goto("%s/web/#/details?id=%s&serverId=%s" % (API, ITEMS["Movie"], server_id()))
        page.wait_for_selector(".page:not(.hide) .mainDetailButtons .btnUserRating", timeout=WAIT_MS)
        page.wait_for_timeout(2000)
        check(page.locator('script[src*="reporter.js"]').count() == 0, "reporting off: the page carries no reporter.js")
        check(page.locator(".mediaReportsButton").count() == 0, "reporting off: no Report button")
        b.close()
    finally:
        admin("/Plugins/%s/Configuration" % cfg["plugin_id"], original)

if failures:
    print("FAIL: %d check(s) failed" % len(failures))
    sys.exit(1)
print("PASS: the Report button and dialog work in Jellyfin Web")
PY
