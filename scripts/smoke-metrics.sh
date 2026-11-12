#!/usr/bin/env bash
# Starts a real Jellyfin 12 server in Docker with the plugin and a Prometheus container on
# the same Docker network. Files reports from two clients, reads /MediaReports/Metrics with a
# Jellyfin API key and checks every series and that nothing private is in it, checks it is
# refused without a token and with a user's token, then lets Prometheus scrape the server
# with the scrape config the Settings tab shows and queries the series.
#
# Usage: scripts/smoke-metrics.sh
#
# Environment:
#   JELLYFIN_IMAGE    image to run (default jellyfin/jellyfin:12)
#   PROMETHEUS_IMAGE  image to run (default prom/prometheus:v3.5.0)
#   SMOKE_TIMEOUT     seconds to wait for each step (default 180)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

image="${JELLYFIN_IMAGE:-jellyfin/jellyfin:12}"
prometheus_image="${PROMETHEUS_IMAGE:-prom/prometheus:v3.5.0}"
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

work="$repo_root/artifacts/smoke-metrics"
meta="src/Jellyfin.Plugin.MediaReports/meta.json"
plugin_version="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$meta")"
container="media-reports-metrics-$$"
prometheus="media-reports-prometheus-$$"
network="media-reports-metrics-$$"

cleanup() {
  docker rm -f "$container" "$prometheus" >/dev/null 2>&1 || true
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
docker pull --quiet "$prometheus_image" >/dev/null
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
echo "== generate two movies"
clip "movies/Test Movie (2026)/Test Movie (2026).mkv" 10
clip "movies/Other Movie (2026)/Other Movie (2026).mkv" 10

echo "== start $image with the plugin"
docker network create "$network" >/dev/null
MSYS_NO_PATHCONV=1 docker create --name "$container" --network "$network" -p 127.0.0.1::8096 \
  -v "$(host_path "$cache/movies"):/media/movies:ro" "$image" >/dev/null
docker cp "$work/stage/plugins" "$container:/config/"
docker start "$container" >/dev/null
port="$(docker port "$container" 8096/tcp | head -n 1 | sed 's/.*://')"
api="http://127.0.0.1:$port"
deadline=$((SECONDS + timeout))
until docker logs "$container" 2>&1 | grep "Startup complete" >/dev/null; do
  (( SECONDS < deadline )) || { docker logs --tail 50 "$container" 2>&1; fail "the server did not start"; }
  sleep 2
done

# auth <client> <device> [token]: the MediaBrowser authorization header for a client and device.
auth() {
  echo "MediaBrowser Client=\"$1\", Device=\"$2\", DeviceId=\"smoke-$(tr ' ' '-' <<<"$2")\", Version=\"1.0.0\"${3:+, Token=\"$3\"}"
}

# req <authorization header or empty> <method> <path> [body]: prints the status; the body goes to last.
req() {
  local header="$1" method="$2" path="$3" body="${4:-}"
  local headers=(-H "Content-Type: application/json")
  [[ -n "$header" ]] && headers+=(-H "Authorization: $header")
  curl -sS -D "$work/results/last.headers" -o "$work/results/last" -w '%{http_code}' -X "$method" "$api$path" "${headers[@]}" ${body:+--data "$body"}
}

# ok <authorization header or empty> <method> <path> [body]: prints the body; fails unless 2xx.
ok() {
  local code
  code="$(req "$@")"
  [[ "$code" == 2* ]] || { cat "$work/results/last" >&2; echo >&2; fail "$2 $3 answered $code"; }
  cat "$work/results/last"
}

admin_auth() { auth "media-reports-smoke" "admin box" "$admin"; }

echo "== complete the startup wizard, add a user and the library"
ok "$(auth media-reports-smoke "admin box")" POST /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null
ok "$(auth media-reports-smoke "admin box")" GET /Startup/User >/dev/null
ok "$(auth media-reports-smoke "admin box")" POST /Startup/User '{"Name":"admin","Password":"admin-password"}' >/dev/null
ok "$(auth media-reports-smoke "admin box")" POST /Startup/Complete >/dev/null
admin="$(ok "$(auth media-reports-smoke "admin box")" POST /Users/AuthenticateByName '{"Username":"admin","Pw":"admin-password"}' | json "d['AccessToken']")"
ok "$(admin_auth)" POST /Users/New '{"Name":"alice","Password":"alice-password"}' >/dev/null
ok "$(admin_auth)" POST "/Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=true" \
  '{"LibraryOptions":{"PathInfos":[{"Path":"/media/movies"}]}}' >/dev/null
alice_id="$(ok "$(admin_auth)" GET /Users | json "[u['Id'] for u in d if u['Name'] == 'alice'][0]")"
items() { ok "$(admin_auth)" GET "/Items?userId=$alice_id&IncludeItemTypes=Movie&Recursive=true&SortBy=SortName"; }
deadline=$((SECONDS + timeout))
until [[ "$(items | json "len(d['Items'])")" == 2 ]]; do
  (( SECONDS < deadline )) || fail "the library did not show the two movies"
  ok "$(admin_auth)" POST /Library/Refresh >/dev/null
  sleep 5
done
other="$(items | json "d['Items'][0]['Id']")"
movie="$(items | json "d['Items'][1]['Id']")"

echo "== reports from two clients"
# file_from <client> <device> <item> <reason>: plays the item on that client, then files a report.
file_from() {
  local token
  token="$(ok "$(auth "$1" "$2")" POST /Users/AuthenticateByName '{"Username":"alice","Pw":"alice-password"}' | json "d['AccessToken']")"
  local state="{\"ItemId\":\"$3\",\"PlaySessionId\":\"metrics-$RANDOM\",\"PlayMethod\":\"DirectPlay\",\"PositionTicks\":0,\"CanSeek\":true}"
  ok "$(auth "$1" "$2" "$token")" POST /Sessions/Playing "$state" >/dev/null
  ok "$(auth "$1" "$2" "$token")" POST /MediaReports/Report "{\"itemId\":\"$3\",\"reasonCode\":\"$4\"}" >/dev/null
  echo "$token"
}
web_token="$(file_from "Jellyfin Web" "Office PC" "$movie" stutter)"
file_from "Jellyfin Web" "Kitchen Laptop" "$other" stutter >/dev/null
file_from "Jellyfin Android TV" "Living Room TV" "$movie" audio >/dev/null

echo "== read the metrics with an API key"
ok "$(admin_auth)" POST "/Auth/Keys?app=prometheus" >/dev/null
key="$(ok "$(admin_auth)" GET /Auth/Keys | json "[k['AccessToken'] for k in d['Items'] if k['AppName'] == 'prometheus'][0]")"
[[ -n "$key" ]] || fail "no API key was created"
ok "MediaBrowser Token=\"$key\"" GET /MediaReports/Metrics >"$work/results/metrics.txt"
grep -qi '^content-type: text/plain; version=0.0.4' "$work/results/last.headers" || fail "the metrics are not in the Prometheus text format: $(grep -i content-type "$work/results/last.headers")"
expect_line() { grep -qxF "$1" "$work/results/metrics.txt" || { cat "$work/results/metrics.txt" >&2; fail "the metrics do not hold: $1"; }; }
expect_line 'media_reports_reports_filed_total{reason="stutter",client="Jellyfin Web"} 2'
expect_line 'media_reports_reports_filed_total{reason="audio",client="Jellyfin Android TV"} 1'
expect_line 'media_reports_open_issues{reason="stutter",status="New"} 2'
expect_line 'media_reports_open_issues{reason="audio",status="New"} 1'
expect_line 'media_reports_open_reports 3'
expect_line 'media_reports_reporting_enabled 1'
expect_line 'media_reports_storage_up 1'
expect_line "media_reports_info{version=\"$plugin_version\"} 1"
for private in "Test Movie" "Other Movie" alice "Office PC" "Living Room TV" "/media/" ".mkv"; do
  ! grep -qF "$private" "$work/results/metrics.txt" || fail "the metrics hold \"$private\""
done
echo "metrics: every series as expected, no title, path, user or device name"

echo "== refused without a token and with a user's token"
code="$(req "" GET /MediaReports/Metrics)"
[[ "$code" == 401 ]] || fail "no token answered $code, not 401"
code="$(req "$(auth "Jellyfin Web" "Office PC" "$web_token")" GET /MediaReports/Metrics)"
[[ "$code" == 403 ]] || fail "a user's token answered $code, not 403"
echo "no token: 401; a user's token: 403"

echo "== Prometheus scrapes with the scrape config the Settings tab shows"
metrics_url="$(ok "$(admin_auth)" GET /MediaReports/Admin/Settings | json "d['MetricsUrl']")"
[[ "$metrics_url" == http*://*/MediaReports/Metrics ]] || fail "Settings gives no metrics address: $metrics_url"
# The tab's own scrapeConfig function from triage.js, with the key put in where it says to.
MSYS_NO_PATHCONV=1 node -e '
const fs = require("fs");
const src = fs.readFileSync(process.argv[1], "utf8");
const fn = src.match(/function scrapeConfig\([\s\S]*?\n}\n/)[0];
const scrapeConfig = new Function(fn + "; return scrapeConfig;")();
const text = scrapeConfig(process.argv[2]).replace("<API key>", process.argv[3]);
fs.writeFileSync(process.argv[4], "global:\n  scrape_interval: 5s\n" + text + "\n");
' src/Jellyfin.Plugin.MediaReports/Web/triage.js "$metrics_url" "$key" "$(host_path "$work/results/prometheus.yml")"
cat "$work/results/prometheus.yml" | sed "s/$key/<API key>/"
MSYS_NO_PATHCONV=1 docker create --name "$prometheus" --network "$network" "$prometheus_image" \
  --config.file=/etc/prometheus/prometheus.yml >/dev/null
docker cp "$work/results/prometheus.yml" "$prometheus:/etc/prometheus/prometheus.yml"
docker start "$prometheus" >/dev/null
prom_query() {  # prom_query <PromQL>: the query's result list, as JSON
  MSYS_NO_PATHCONV=1 docker exec "$prometheus" wget -qO- "http://localhost:9090/api/v1/query?query=$1" 2>/dev/null | json "d['data']['result']" || true
}
deadline=$((SECONDS + 90))
until [[ "$(prom_query 'up%7Bjob%3D%22media-reports%22%7D' | tr -d ' ')" == *"'1']"* ]]; do
  if (( SECONDS >= deadline )); then
    MSYS_NO_PATHCONV=1 docker exec "$prometheus" wget -qO- http://localhost:9090/api/v1/targets >&2 || true
    fail "Prometheus did not scrape the server"
  fi
  sleep 3
done
prom_query media_reports_open_reports | grep -q "'3'" || fail "Prometheus has no media_reports_open_reports of 3: $(prom_query media_reports_open_reports)"
echo "Prometheus scraped the server with the tab's scrape config: up 1, media_reports_open_reports 3"

echo "PASS: Prometheus metrics on $image"
