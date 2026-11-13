#!/usr/bin/env bash
# Starts a real Jellyfin 12 server in Docker with the plugin, a generated movie and a short
# series, then drives Jellyfin Web in a headless browser (Playwright for Python) at desktop
# width, phone width and in the TV layout, as one user each: where the Report button
# appears, and the report dialog it opens, from sending to what the server stored. Then
# moves the reports on as the admin and follows them in My Reports in each layout.
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
  trap 'status=$?; if (( status != 0 )); then echo "== server log (last 80 lines)" >&2; docker logs --tail 80 "$container" >&2 2>&1 || true; fi; docker rm -f "$container" >/dev/null 2>&1 || true' EXIT
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
import time
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


def admin(path, body=None, method=None):
    auth = 'MediaBrowser Client="media-reports-smoke", Device="smoke", DeviceId="media-reports-web", Version="1.0.0", Token="%s"' % cfg["admin_token"]
    headers = {"Authorization": auth}
    data = None
    if body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    request = urllib.request.Request(API + path, data=data, method=method or ("POST" if body is not None else "GET"), headers=headers)
    with urllib.request.urlopen(request) as response:
        text = response.read()
    return json.loads(text) if text else None


def set_avatar(user_id):
    # A plain blue square, the colour that hid the dot on a phone.
    import base64
    import struct
    import zlib

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    side = 64
    rows = b"".join(b"\x00" + bytes((0, 164, 220)) * side for _ in range(side))
    image = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", side, side, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b"")
    auth = 'MediaBrowser Client="media-reports-smoke", Device="smoke", DeviceId="media-reports-web", Version="1.0.0", Token="%s"' % cfg["admin_token"]
    request = urllib.request.Request("%s/Users/%s/Images/Primary" % (API, user_id), data=base64.b64encode(image), method="POST",
                                     headers={"Authorization": auth, "Content-Type": "image/png"})
    urllib.request.urlopen(request).read()


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
    playback: text('.mediaReportsPlayback'), hint: text('.mediaReportsHint'), error: text('.mediaReportsError'), info: text('.mediaReportsInfo'),
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
    check(s["hint"] is None, where + ": no hint line under the reasons (%s)" % s["hint"])
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
    check(s["playback"] is None, where + ": no line about the attached playback (%s)" % s["playback"])
    check(s["hint"] is None, where + ": no hint line under the reasons (%s)" % s["hint"])
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
    check(s and s.get("playback") is None, layout + ", dialog after playing: no line about the attached playback (%s)" % s.get("playback"))
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


MINE = ".mediaReportsDialog.mediaReportsMine"
SECRET = "Secret admin note: re-encoded from the source."
EXPIRY = "Expires in 12 months"
CLOSED = ("Fixed", "Won't fix")
PILLS = {
    "New": ("rgba(0, 164, 220, 0.18)", "rgb(79, 195, 236)"),
    "Being looked at": ("rgba(242, 176, 30, 0.18)", "rgb(245, 196, 81)"),
    "Fixed": ("rgba(102, 187, 106, 0.18)", "rgb(143, 209, 147)"),
    "Won't fix": ("rgba(255, 255, 255, 0.1)", "rgba(255, 255, 255, 0.75)"),
}

# My Reports as drawn: the panel, its rows or its detail, and the confirmation.
MINE_STATE = """() => {
  const d = document.querySelector('.mediaReportsDialog.mediaReportsMine');
  if (!d) return null;
  const r = d.getBoundingClientRect();
  const text = (s, root) => { const e = (root || d).querySelector(s); return e && e.offsetParent !== null ? e.textContent.trim() : null; };
  const pill = p => p ? { text: p.textContent.trim(), bg: getComputedStyle(p).backgroundColor, color: getComputedStyle(p).color } : null;
  return {
    cls: d.className, x: r.left, y: r.top, w: r.width, h: r.height, vw: innerWidth, vh: innerHeight,
    heading: text('.mediaReportsHeading'),
    rows: [...d.querySelectorAll('.mediaReportsRow')].map(row => ({
      title: text('.mediaReportsRowTitle', row), meta: text('.mediaReportsRowMeta', row),
      pill: pill(row.querySelector('.mediaReportsPill')), unread: row.classList.contains('mr-unread'),
      dot: !!row.querySelector('.mediaReportsUnreadDot'), tag: row.tagName,
      action: (() => { const a = row.parentElement.querySelector('.mediaReportsRowAction'); return a && a.offsetParent !== null ? a.className.replace('mediaReportsRowAction', '').trim() : null; })(),
      actionLabel: (() => { const a = row.parentElement.querySelector('.mediaReportsRowAction'); return a ? a.getAttribute('aria-label') : null; })()
    })),
    clear: text('.mediaReportsClear'),
    footnote: [...d.querySelectorAll('p')].some(e => /removed a year/.test(e.textContent)),
    empty: text('.mediaReportsEmpty'), emptyHint: text('.mediaReportsEmptyHint'),
    detail: d.querySelector('.mediaReportsDetail') ? {
      title: text('.mediaReportsDetail .mediaReportsRowTitle'), pill: pill(d.querySelector('.mediaReportsDetail .mediaReportsPill')),
      facts: [...d.querySelectorAll('.mediaReportsFacts dd')].map(e => e.textContent.trim()),
      history: [...d.querySelectorAll('.mediaReportsHistory li .mediaReportsStep')].map(e => e.textContent.trim()),
      back: d.querySelector('.mediaReportsBack') ? (d.querySelector('.mediaReportsBack').closest('.mediaReportsHeadingRow') && d.querySelector('.mediaReportsBack').nextElementSibling && d.querySelector('.mediaReportsBack').nextElementSibling.classList.contains('mediaReportsHeading') ? 'header' : 'elsewhere') : null,
      withdraw: !!d.querySelector('.mediaReportsWithdraw') && d.querySelector('.mediaReportsWithdraw').offsetParent !== null
    } : null,
    confirm: text('.mediaReportsConfirm .mediaReportsConfirmText'),
    all: d.textContent, focusInside: d.contains(document.activeElement),
    focus: document.activeElement ? (document.activeElement.textContent || '').trim() : ''
  };
}"""

# Where the entry, its count and the dot are.
ENTRY_STATE = """() => {
  const visible = e => !!e && e.offsetParent !== null;
  // Nothing in the user button paints over the dot, and no clipping ancestor (a round
  // button among them) cuts any of it off.
  const onTop = e => {
    if (!visible(e)) return false;
    const r = e.getBoundingClientRect();
    e.style.pointerEvents = 'auto';
    const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    e.style.pointerEvents = 'none';
    if (hit !== e) return false;
    for (let n = e.parentElement; n && n !== document.body; n = n.parentElement) {
      const c = getComputedStyle(n);
      if (c.overflow === 'visible') continue;
      const a = n.getBoundingClientRect();
      if (c.borderRadius === '50%' && Math.abs(a.width - a.height) < 1) {
        const dx = r.left + r.width / 2 - (a.left + a.width / 2), dy = r.top + r.height / 2 - (a.top + a.height / 2);
        if (Math.sqrt(dx * dx + dy * dy) + r.width / 2 > a.width / 2 + 0.5) return false;
      } else if (r.left < a.left || r.top < a.top || r.right > a.right || r.bottom > a.bottom) {
        return false;
      }
    }
    return true;
  };
  // The count shares the label's line.
  const oneLine = r => {
    const label = r.querySelector('.listItemBodyText'), count = r.querySelector('.mediaReportsSettingsRowCount');
    if (!label || !count) return false;
    const a = label.firstChild.nodeType === 3 ? (() => { const g = document.createRange(); g.selectNodeContents(label.firstChild); return g.getBoundingClientRect(); })() : label.getBoundingClientRect();
    const b = count.getBoundingClientRect();
    return b.top < a.bottom && b.bottom > a.top;
  };
  const item = document.querySelector('.mediaReportsMenuItem');
  const row = document.querySelector('.page:not(.hide) .mediaReportsSettingsRow');
  const user = document.querySelector('button[aria-controls=app-user-menu]') || document.querySelector('.headerUserButton');
  const dot = user && user.querySelector('.mediaReportsDot');
  return {
    item: item ? { after: item.previousElementSibling ? item.previousElementSibling.getAttribute('href') : null,
                   text: item.textContent.trim(), count: item.querySelector('.mediaReportsMenuItemCount') ? item.querySelector('.mediaReportsMenuItemCount').textContent.trim() : null,
                   role: item.getAttribute('role'), visible: visible(item) } : null,
    row: row ? { after: row.previousElementSibling ? row.previousElementSibling.className : null, text: row.textContent.trim(),
                 count: row.querySelector('.mediaReportsSettingsRowCount') ? row.querySelector('.mediaReportsSettingsRowCount').textContent.trim() : null,
                 oneLine: oneLine(row), pill: row.querySelector('.mediaReportsSettingsRowCount') ? getComputedStyle(row.querySelector('.mediaReportsSettingsRowCount')).backgroundColor : null } : null,
    dot: visible(dot), dotOnTop: onTop(dot), dotRinged: !!dot && getComputedStyle(dot).boxShadow !== 'none'
  };
}"""


def mine_state(page):
    return page.evaluate(MINE_STATE)


def entry(page):
    return page.evaluate(ENTRY_STATE)


def wait_mine(page, open_=True):
    page.wait_for_function("open => (document.querySelector('.mediaReportsDialog.mediaReportsMine') !== null) === open", arg=open_, timeout=WAIT_MS)
    page.wait_for_timeout(400)


def expected_rows(user):
    # Newest first, as the Report dialog filed them in run(): player, series, episode, movie.
    reports = sorted((r for r in admin("/MediaReports/Admin/Export")["reports"] if r["reporterName"] == user), key=lambda r: r["id"], reverse=True)
    def title(r):
        if r["itemType"] == "Episode":
            return "%s S%02dE%02d" % (r["seriesName"], r["seasonNumber"], r["episodeNumber"])
        return r["itemName"]
    labels = {"poor-quality": "Won't fix", "other": "New", "subtitles": "Being looked at", "stutter": "Fixed"}
    return [(title(r), labels[r["reasonCode"]], r["reasonCode"]) for r in reports]


def check_dot(e, where):
    check(e["dotOnTop"], where + ": the whole dot shows; nothing covers or clips it")
    check(e["dotRinged"], where + ": the dot has a ring that sets it apart from the avatar")


def open_mine_from_menu(page, where):
    try:
        page.wait_for_function("() => { const u = document.querySelector('button[aria-controls=app-user-menu]'); return u && u.querySelector('.mediaReportsDot'); }", timeout=WAIT_MS)
    except Exception:  # noqa: BLE001 - a timeout is the finding
        pass
    check(entry(page)["dot"], where + ": the user button carries the unread dot")
    check_dot(entry(page), where)
    page.locator("button[aria-controls=app-user-menu]").click()
    try:
        page.wait_for_selector(".mediaReportsMenuItem", state="visible", timeout=WAIT_MS)
        page.wait_for_function("() => { const c = document.querySelector('.mediaReportsMenuItemCount'); return c && c.textContent.trim(); }", timeout=WAIT_MS)
    except Exception:  # noqa: BLE001 - a timeout is the finding
        check(False, where + ": My Reports is in the User Menu with its count")
        return False
    e = entry(page)["item"]
    check(e["after"] == "#/mypreferencesmenu", where + ": My Reports sits right after Settings in the User Menu (%s)" % e["after"])
    check(e["count"] == "3" and e["role"] == "menuitem", where + ": it carries the unread count 3 (%s)" % e["count"])
    started = time.monotonic()
    page.locator(".mediaReportsMenuItem").click()
    try:
        wait_mine(page)
    except Exception:  # noqa: BLE001 - a timeout is the finding
        check(False, where + ": choosing My Reports opens it")
        return False
    took = time.monotonic() - started
    check(took < 1.0, where + ": My Reports opens within a second of the click (%.2fs)" % took)
    page.wait_for_function("() => document.querySelector('.mediaReportsMine .mediaReportsRow, .mediaReportsMine .mediaReportsEmpty')", timeout=WAIT_MS)
    try:
        page.wait_for_function("() => ![...document.querySelectorAll('.MuiMenu-paper')].some(m => getComputedStyle(m).visibility !== 'hidden' && m.offsetParent !== null)", timeout=WAIT_MS)
    except Exception:  # noqa: BLE001 - a timeout is the finding
        pass
    check(page.locator(".MuiMenu-paper").locator("visible=true").count() == 0, where + ": the User Menu closed behind it")
    page.wait_for_timeout(300)
    check(page.evaluate("() => !!document.activeElement && !!document.activeElement.closest('.mediaReportsMine')"),
          where + ": focus is in My Reports once the menu has closed")
    return True


def check_list(state, rows, where, layout):
    got = [(r["title"], r["pill"]["text"] if r["pill"] else None) for r in state["rows"]]
    check(got == [(t, s) for t, s, _ in rows], where + ": the reports are listed newest first with their titles and statuses (%s)" % got)
    for r in state["rows"]:
        want = PILLS.get(r["pill"]["text"]) if r["pill"] else None
        check(want is not None and (r["pill"]["bg"], r["pill"]["color"]) == want,
              where + ": the %s pill has the approved colours (%s on %s)" % (r["pill"] and r["pill"]["text"], r["pill"] and r["pill"]["color"], r["pill"] and r["pill"]["bg"]))
    check(all(r["meta"] and r["meta"].split(" · ")[0] in REASONS for r in state["rows"]), where + ": each row names its reason and when")
    check(not state["footnote"], where + ": there is no retention footnote")
    for r in state["rows"]:
        status = r["pill"]["text"] if r["pill"] else None
        closed = status in CLOSED
        check(r["meta"] and (r["meta"].endswith(" · " + EXPIRY) if closed else "Expires" not in r["meta"]),
              where + ": the %s row %s (%s)" % (status, "ends with \"%s\"" % EXPIRY if closed else "shows no expiry", r["meta"]))
        if layout == "tv":
            want = None
        else:
            want = "mediaReportsHide" if closed else "mediaReportsRowWithdraw" if status == "New" else None
        check(r["action"] == want, where + ": the %s row's button is %s (%s)" % (status, want or "none", r["action"]))
    if layout == "tv":
        check(state["clear"] is None, where + ": the TV offers no Clear all closed")
    else:
        check(state["clear"] == "Clear all closed", where + ": Clear all closed sits under the list (%s)" % state["clear"])
    check(SECRET not in state["all"], where + ": the admin's note is nowhere in My Reports")


def open_row(page, title, status, layout):
    index = [(r["title"], r["pill"]["text"]) for r in mine_state(page)["rows"]].index((title, status))
    if layout == "tv":
        page.locator(MINE + " .mediaReportsRow").nth(index).focus()
        page.keyboard.press("Enter")
    else:
        page.locator(MINE + " .mediaReportsRow").nth(index).click()
    page.wait_for_selector(MINE + " .mediaReportsDetail", timeout=WAIT_MS)
    page.wait_for_function("() => document.querySelector('.mediaReportsMine .mediaReportsHistory li')", timeout=WAIT_MS)
    return mine_state(page)["detail"]


def back_to_list(page, layout):
    if layout == "tv":
        page.keyboard.press("Escape")
    else:
        page.locator(MINE + " .mediaReportsBack").click()
    page.wait_for_selector(MINE + " .mediaReportsRow", timeout=WAIT_MS)


def row_index(page, wanted):
    return [(r["title"], r["pill"]["text"]) for r in mine_state(page)["rows"]].index(wanted)


def listed(page):
    return [(r["title"], r["pill"]["text"]) for r in mine_state(page)["rows"]]


def stored(user):
    return {r["reasonCode"] for r in admin("/MediaReports/Admin/Export")["reports"] if r["reporterName"] == user}


def row_withdraw(page, where, wanted, user):
    item = page.locator(MINE + " .mediaReportsItem").nth(row_index(page, wanted))
    item.locator(".mediaReportsRowWithdraw").click()
    page.wait_for_selector(MINE + " .mediaReportsItem .mediaReportsConfirm", timeout=WAIT_MS)
    check(mine_state(page)["confirm"] == "Withdraw this report?", where + ': the row\'s Withdraw asks "Withdraw this report?"')
    page.locator(MINE + " .mediaReportsKeep").click()
    page.wait_for_timeout(300)
    check(mine_state(page)["confirm"] is None and wanted in listed(page), where + ": Keep it leaves the report in the list")
    before = len(listed(page))
    item.locator(".mediaReportsRowWithdraw").click()
    page.locator(MINE + " .mediaReportsWithdrawConfirm").click()
    page.wait_for_function("n => document.querySelectorAll('.mediaReportsMine .mediaReportsRow').length === n", arg=before - 1, timeout=WAIT_MS)
    check(wanted not in listed(page) and "other" not in stored(user), where + ": Withdraw from the row withdraws it (%s)" % listed(page))


def remove_row(page, where, wanted, user):
    before = len(listed(page))
    item = page.locator(MINE + " .mediaReportsItem").nth(row_index(page, wanted))
    check(item.locator(".mediaReportsHide").get_attribute("aria-label") == "Remove from list", where + ": the closed row's button is named Remove from list")
    item.locator(".mediaReportsHide").click()
    page.wait_for_function("n => document.querySelectorAll('.mediaReportsMine .mediaReportsRow').length === n", arg=before - 1, timeout=WAIT_MS)
    s = mine_state(page)
    check(wanted not in listed(page) and s["confirm"] is None, where + ": Remove from list takes it out at once, without asking (%s)" % listed(page))
    check(s["focusInside"], where + ": focus stays in My Reports")
    check("stutter" in stored(user), where + ": the removed report is still on the server for the admin")


def clear_closed(page, where, user):
    rows = listed(page)
    closed = [r for r in rows if r[1] in CLOSED]
    page.locator(MINE + " .mediaReportsClear").click()
    page.wait_for_selector(MINE + " .mediaReportsClearArea .mediaReportsConfirm", timeout=WAIT_MS)
    want = "Remove the closed report from your list?" if len(closed) == 1 else "Remove %d closed reports from your list?" % len(closed)
    check(mine_state(page)["confirm"] == want, where + ': Clear all closed asks "%s" (%s)' % (want, mine_state(page)["confirm"]))
    page.locator(MINE + " .mediaReportsClearCancel").click()
    page.wait_for_timeout(300)
    check(listed(page) == rows and mine_state(page)["clear"] == "Clear all closed", where + ": Cancel keeps every report")
    page.locator(MINE + " .mediaReportsClear").click()
    page.locator(MINE + " .mediaReportsClearConfirm").click()
    page.wait_for_function("n => document.querySelectorAll('.mediaReportsMine .mediaReportsRow').length === n", arg=len(rows) - len(closed), timeout=WAIT_MS)
    s = mine_state(page)
    check(listed(page) == [r for r in rows if r[1] not in CLOSED] and s["clear"] is None,
          where + ": confirming removes only the closed reports, and the button goes (%s)" % listed(page))
    check({"poor-quality"} <= stored(user), where + ": the cleared reports are still on the server for the admin")


def my_reports_expiry(p):
    # The expiry follows the server's retention period; at 0 nothing expires.
    for days, want in ((30, "Expires in 30 days"), (0, None)):
        configure(ReportRetentionDays=days)
        b = browser(p)
        page = b.new_context(viewport={"width": 1440, "height": 900}).new_page()
        sign_in(page, "carol")
        page.wait_for_timeout(1000)
        page.evaluate("window.MediaReports.myReports()")
        wait_mine(page)
        page.wait_for_selector(MINE + " .mediaReportsRow", timeout=WAIT_MS)
        metas = [(r["pill"]["text"], r["meta"]) for r in mine_state(page)["rows"]]
        closed = [m for st, m in metas if st in CLOSED]
        check(closed and all((m.endswith(" · " + want) if want else "Expires" not in m) for m in closed),
              "retention %d days: closed rows %s (%s)" % (days, 'read "%s"' % want if want else "show no expiry", closed))
        b.close()
    configure(ReportRetentionDays=365)


def my_reports(p, layout, user, context_args):
    where = layout + ", My Reports"
    b = browser(p)
    page = b.new_context(**context_args).new_page()
    errors = []
    page.on("pageerror", lambda e: errors.append(str(e)) if "reporter.js" in (e.stack or "") else None)
    page.on("console", lambda m: errors.append(m.text) if "[Media Reports]" in m.text else None)
    sign_in(page, user)
    rows = expected_rows(user)
    if layout == "tv":
        page.evaluate("localStorage.setItem('layout', 'tv')")
        page.reload()
        page.wait_for_function("() => document.documentElement.classList.contains('layout-tv')", timeout=WAIT_MS)
        try:
            page.wait_for_function("() => { const u = document.querySelector('.headerUserButton'); return u && u.querySelector('.mediaReportsDot'); }", timeout=WAIT_MS)
        except Exception:  # noqa: BLE001 - a timeout is the finding
            pass
        check(entry(page)["dot"], where + ": the user button carries the unread dot")
        check_dot(entry(page), where)
        page.goto(API + "/web/#/mypreferencesmenu")
        try:
            page.wait_for_selector(".page:not(.hide) .mediaReportsSettingsRow", timeout=WAIT_MS)
        except Exception:  # noqa: BLE001 - a timeout is the finding
            check(False, where + ": My Reports is a row in Settings")
            b.close()
            return
        e = entry(page)["row"]
        check("lnkUserProfile" in (e["after"] or ""), where + ": the row sits right after Profile (%s)" % e["after"])
        check(e["count"] == "3 updated", where + ': it reads "3 updated" (%s)' % e["count"])
        check(e["oneLine"], where + ": the count sits on the label's line")
        check(e["pill"] == "rgb(0, 164, 220)", where + ": the count is a blue pill (%s)" % e["pill"])
        page.locator(".page:not(.hide) .mediaReportsSettingsRow").focus()
        page.keyboard.press("Enter")
        wait_mine(page)
        page.wait_for_selector(MINE + " .mediaReportsRow", timeout=WAIT_MS)
    elif not open_mine_from_menu(page, where):
        b.close()
        return

    s = mine_state(page)
    if layout == "desktop":
        check("mr-desktop" in s["cls"] and abs(s["x"] + s["w"] / 2 - s["vw"] / 2) < 2 and s["w"] == 640, where + ": a centred 640 px panel (%dx%d)" % (s["w"], s["h"]))
    else:
        check(("mr-" + layout) in s["cls"] and s["w"] == s["vw"] and s["h"] == s["vh"], where + ": full screen (%dx%d)" % (s["w"], s["h"]))
    check(s["heading"] == "My Reports", where + ": headed My Reports")
    check(s["focusInside"], where + ": focus moves into it")
    check_list(s, rows, where, layout)
    check([r["unread"] and r["dot"] for r in s["rows"]] == [st != "New" for _, st, _ in rows], where + ": the three changed reports are marked unread")

    by_code = {code: (t, st) for t, st, code in rows}
    fixed = open_row(page, *by_code["stutter"], layout)
    check(fixed["facts"][:1] == ["Stutter or jitter"] and (layout == "tv" or NOTE in fixed["facts"]),
          where + ": a report shows its reason" + ("" if layout == "tv" else " and note") + " (%s)" % fixed["facts"])
    check(fixed["history"] == ["Reported", "Fixed"], where + ": its history reads Reported, Fixed (%s)" % fixed["history"])
    check(SECRET not in mine_state(page)["all"], where + ": the admin's note isn't shown")
    check(not fixed["withdraw"], where + ": a Fixed report offers no Withdraw")
    if layout == "tv":
        check(fixed["back"] is None, where + ": the TV shows no Back button; the remote's BACK does it")
    else:
        check(fixed["back"] == "header", where + ": Back is an icon in the header, before My Reports (%s)" % fixed["back"])
    back_to_list(page, layout)
    looked = open_row(page, *by_code["subtitles"], layout)
    check(looked["history"] == ["Reported", "Being looked at"] and not looked["withdraw"], where + ": an Investigating report reads Being looked at and offers no Withdraw")
    back_to_list(page, layout)

    new = open_row(page, *by_code["other"], layout)
    if layout == "tv":
        check(not new["withdraw"], where + ": the TV offers no Withdraw, even for a New report")
        back_to_list(page, layout)
    elif layout == "desktop":
        check(new["withdraw"], where + ": a New report offers Withdraw")
        back_to_list(page, layout)
        row_withdraw(page, where, by_code["other"], user)
    else:
        check(new["withdraw"], where + ": a New report offers Withdraw")
        page.locator(MINE + " .mediaReportsWithdraw").click()
        page.wait_for_selector(MINE + " .mediaReportsConfirm", timeout=WAIT_MS)
        check(mine_state(page)["confirm"] == "Withdraw this report?", where + ': Withdraw asks "Withdraw this report?"')
        page.locator(MINE + " .mediaReportsKeep").click()
        page.wait_for_timeout(500)
        s = mine_state(page)
        check(s["confirm"] is None and s["detail"] and s["detail"]["withdraw"], where + ": Keep it leaves the report in place")
        page.locator(MINE + " .mediaReportsWithdraw").click()
        page.wait_for_selector(MINE + " .mediaReportsConfirm", timeout=WAIT_MS)
        page.locator(MINE + " .mediaReportsWithdrawConfirm").click()
        page.wait_for_selector(MINE + " .mediaReportsRow", timeout=WAIT_MS)
        page.wait_for_timeout(500)
        left = [(r["title"], r["pill"]["text"]) for r in mine_state(page)["rows"]]
        check(by_code["other"] not in left and len(left) == 3, where + ": a withdrawn report leaves the list (%s)" % left)
        stored = [r for r in admin("/MediaReports/Admin/Export")["reports"] if r["reporterName"] == user and r["reasonCode"] == "other"]
        check(not stored, where + ": and is gone from the server")

    poor = open_row(page, *by_code["poor-quality"], layout)
    check(poor["history"] == ["Reported", "Won't fix"], where + ": a Won't fix report reads Won't fix")
    back_to_list(page, layout)
    s = mine_state(page)
    check(not any(r["unread"] for r in s["rows"]), where + ": opening each changed report clears its mark")

    if layout == "desktop":
        remove_row(page, where, by_code["stutter"], user)
    if layout != "tv":
        clear_closed(page, where, user)

    page.keyboard.press("Escape")
    wait_mine(page, open_=False)
    try:
        page.wait_for_function("() => !document.querySelector('.mediaReportsDot')", timeout=WAIT_MS)
    except Exception:  # noqa: BLE001 - a timeout is the finding
        pass
    check(not entry(page)["dot"], where + ": after closing, the dot is gone")
    if layout == "tv":
        page.wait_for_timeout(500)
        e = entry(page)["row"]
        check(e is not None and e["count"] is None, where + ": and the Settings row has no count (%s)" % e)
    else:
        page.locator("button[aria-controls=app-user-menu]").click()
        page.wait_for_selector(".mediaReportsMenuItem", state="visible", timeout=WAIT_MS)
        e = entry(page)["item"]
        check(e["count"] is None, where + ": and the User Menu entry has no count (%s)" % e["count"])
        page.keyboard.press("Escape")
    check(not errors, where + ": no errors from the Report button script" + (": " + "; ".join(errors) if errors else ""))
    b.close()


def my_reports_empty(p):
    b = browser(p)
    page = b.new_context(viewport={"width": 1440, "height": 900}).new_page()
    sign_in(page, "admin")
    page.wait_for_timeout(1500)
    check(not entry(page)["dot"], "empty My Reports: no dot without unread reports")
    page.locator("button[aria-controls=app-user-menu]").click()
    try:
        page.wait_for_selector(".mediaReportsMenuItem", state="visible", timeout=WAIT_MS)
        page.locator(".mediaReportsMenuItem").click()
        wait_mine(page)
        page.wait_for_selector(MINE + " .mediaReportsEmpty", timeout=WAIT_MS)
    except Exception:  # noqa: BLE001 - a timeout is the finding
        check(False, "empty My Reports: it opens and says so")
        b.close()
        return
    s = mine_state(page)
    check(s["empty"] == "You haven't reported anything.", "empty My Reports: it says \"You haven't reported anything.\"")
    check("flag" in (s["emptyHint"] or "") and "favourite" in (s["emptyHint"] or ""), "empty My Reports: it says where the flag button is (%s)" % s["emptyHint"])
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

    # My Reports: the admin moves the issues on, and each user follows theirs.
    issues = {r["reasonCode"]: r["issueId"] for r in export["reports"]}
    admin("/MediaReports/Admin/Issues/%d" % issues["stutter"], {"status": "Fixed", "notes": SECRET}, method="PATCH")
    admin("/MediaReports/Admin/Issues/Status", {"issueIds": [issues["subtitles"]], "status": "Investigating"})
    admin("/MediaReports/Admin/Issues/Status", {"issueIds": [issues["poor-quality"]], "status": "WontFix"})
    # Bob and Carol have profile pictures, so the dot has to sit above one.
    for user in admin("/Users"):
        if user["Name"] in ("bob", "carol"):
            set_avatar(user["Id"])
    my_reports(p, "desktop", "alice", {"viewport": {"width": 1440, "height": 900}})
    my_reports(p, "phone", "bob", PHONE)
    my_reports(p, "tv", "carol", {"viewport": {"width": 1280, "height": 720}})
    my_reports_empty(p)
    my_reports_expiry(p)

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
