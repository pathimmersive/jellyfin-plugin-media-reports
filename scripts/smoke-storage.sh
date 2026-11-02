#!/usr/bin/env bash
# Starts a real Jellyfin 12 server in Docker with the plugin and checks report storage end
# to end: the database is created on first start; a database written at schema version 1 is
# migrated on the next start; deleting a user anonymises their reports; the purge task
# removes old closed issues and keeps open ones; the copy task writes a consistent copy that
# a server can start on; and the export downloads for an admin and is refused otherwise.
#
# Usage: scripts/smoke-storage.sh [--without-plugin]
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

work="$repo_root/artifacts/smoke-storage"
meta="src/Jellyfin.Plugin.MediaReports/meta.json"
plugin_version="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$meta")"
container="media-reports-storage-$$"
db_dir="/config/data/media-reports"
auth="MediaBrowser Client=\"media-reports-smoke\", Device=\"smoke-script\", DeviceId=\"media-reports-storage\", Version=\"1.0.0\""
token=""

trap 'docker rm -f "$container" >/dev/null 2>&1 || true' EXIT

echo "== dotnet publish"
rm -rf "$work"
mkdir -p "$work/results"
dotnet publish src/Jellyfin.Plugin.MediaReports --nologo -c Release -o "$work/publish" >/dev/null
mkdir -p "$work/stage/plugins/MediaReports_${plugin_version}"
cp -R "$work/publish/." "$work/stage/plugins/MediaReports_${plugin_version}/"

docker pull --quiet "$image" >/dev/null
echo "== start $image (plugin installed: $with_plugin)"
MSYS_NO_PATHCONV=1 docker create --name "$container" -p 127.0.0.1::8096 "$image" >/dev/null
if [[ $with_plugin -eq 1 ]]; then
  docker cp "$work/stage/plugins" "$container:/config/"
fi

api=""
last_start=""
start_server() {
  local since
  since="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  last_start="$since"
  docker start "$container" >/dev/null
  local port
  port="$(docker port "$container" 8096/tcp | head -n 1 | sed 's/.*://')"
  api="http://127.0.0.1:$port"
  local deadline=$((SECONDS + timeout))
  until docker logs --since "$since" "$container" 2>&1 | grep "Startup complete" >/dev/null; do
    (( SECONDS < deadline )) || { docker logs --tail 50 "$container" 2>&1; fail "the server did not start"; }
    sleep 2
  done
}

stop_server() {
  docker stop -t 30 "$container" >/dev/null
}

# Calls the API as the signed-in user; prints the body and fails on an error status.
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

# Prints only the HTTP status of a call made with the given token.
status_as() {
  local tok="$1" method="$2" path="$3"
  curl -sS -o /dev/null -w '%{http_code}' -X "$method" "$api$path" -H "Authorization: $auth, Token=\"$tok\""
}

# Reads a value from JSON on stdin with a Python expression over `d`.
json() {
  py -c "import json,sys; d=json.load(sys.stdin); v=$1; print('' if v is None else v)"
}

sign_in() {
  call POST /Users/AuthenticateByName "{\"Username\":\"$1\",\"Pw\":\"$2\"}" | json "d['AccessToken']"
}

export_reports() {
  call GET /MediaReports/Admin/Export >"$work/results/$1.json"
}

run_task() {
  local name="$1" id
  id="$(call GET /ScheduledTasks | json "next((t['Id'] for t in d if t['Name'] == '$name'), None)")"
  [[ -n "$id" ]] || fail "scheduled task \"$name\" is not listed"
  call GET /ScheduledTasks | json "next(t for t in d if t['Name'] == '$name')['Triggers'][0]['Type']" | grep -x DailyTrigger >/dev/null \
    || fail "\"$name\" has no daily trigger"
  call POST "/ScheduledTasks/Running/$id" >/dev/null
  local deadline=$((SECONDS + timeout)) result=""
  while :; do
    result="$(call GET "/ScheduledTasks/$id" | json "d['State'] + ':' + ((d.get('LastExecutionResult') or {}).get('Status') or '')")"
    [[ "$result" == "Idle:Completed" ]] && break
    [[ "$result" == Idle:Failed || "$result" == Idle:Aborted ]] && fail "\"$name\" ended $result"
    (( SECONDS < deadline )) || fail "\"$name\" did not finish ($result)"
    sleep 1
  done
  echo "ran: $name"
}

start_server
echo "== complete the startup wizard, sign in, and add a reporter and a viewer"
call POST /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}'
call GET /Startup/User >/dev/null
call POST /Startup/User '{"Name":"admin","Password":"admin-password"}'
call POST /Startup/Complete
token="$(sign_in admin admin-password)"
[[ -n "$token" ]] || fail "could not sign in"
reporter_id="$(call POST /Users/New '{"Name":"reporter","Password":"reporter-password"}' | json "d['Id']")"
call POST /Users/New '{"Name":"viewer","Password":"viewer-password"}' >/dev/null

echo "== the database exists after the first start"
MSYS_NO_PATHCONV=1 docker exec "$container" test -s "$db_dir/media-reports.db" || fail "$db_dir/media-reports.db was not created"

plugin_id="$(sed -n 's/.*"guid": *"\([^"]*\)".*/\1/p' "$meta")"
days="$(call GET "/Plugins/$plugin_id/Configuration" | json "d['ReportRetentionDays']")"
[[ "$days" == 365 ]] || fail "the retention period is $days days, not 365"
echo "retention period: 365 days"

echo "== replace it with a database written at schema version 1"
stop_server
seed="$work/seed.db"
py - "$seed" "$reporter_id" <<'PY'
import re, sqlite3, sys, uuid, datetime
seed, reporter = sys.argv[1], str(uuid.UUID(sys.argv[2]))
source = open("src/Jellyfin.Plugin.MediaReports/Storage/ReportDatabase.cs", encoding="utf-8").read()
version_1 = re.search(r'"""\n(.*?)"""', source[source.index("Migrations ="):], re.S).group(1)
now = datetime.datetime.now(datetime.timezone.utc)
ts = lambda days: (now - datetime.timedelta(days=days)).strftime("%Y-%m-%dT%H:%M:%S.0000000Z")
db = sqlite3.connect(seed)
db.executescript(version_1)
other = str(uuid.uuid4())
issues = [  # id, item, reason, status, created days ago, closed days ago
    (1, str(uuid.uuid4()), "stutter", "New", 730, None),
    (2, str(uuid.uuid4()), "audio", "Fixed", 500, 400),
    (3, str(uuid.uuid4()), "subtitles", "Investigating", 10, None),
]
for i, item, reason, status, created, closed in issues:
    db.execute("INSERT INTO issue (id, item_id, reason_code, status, resolution_text, created_at, updated_at, closed_at) VALUES (?,?,?,?,?,?,?,?)",
               (i, item, reason, status, "old resolution", ts(created), ts(created), ts(closed) if closed else None))
reports = [(1, reporter, "reporter", "Reporter's Phone"), (3, reporter, "reporter", "Reporter's Phone"), (2, other, "other", "Living Room TV")]
for n, (issue, user, name, device) in enumerate(reports, 1):
    item = issues[issue - 1][1]
    snapshot = '{"schemaVersion":1,"client":"Jellyfin Web","deviceName":"%s","playMethod":"Transcode"}' % device
    db.execute("INSERT INTO report (id, created_at, reporter_user_id, reporter_name, item_id, item_type, item_name, reason_code, issue_id, snapshot, play_method, client, device) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)",
               (n, ts(issues[issue - 1][4]), user, name, item, "Movie", "Seeded Movie %d" % issue, issues[issue - 1][2], issue, snapshot, "Transcode", "Jellyfin Web", device))
    db.execute("INSERT INTO event (at, issue_id, report_id, actor_user_id, kind) VALUES (?,?,?,?,?)", (ts(issues[issue - 1][4]), issue, n, user, "filed"))
db.execute("PRAGMA user_version = 1")
db.commit()
db.close()
PY
MSYS_NO_PATHCONV=1 docker cp "$(host_path "$seed")" "$container:$db_dir/media-reports.db"
start_server
token="$(sign_in admin admin-password)"
if ! docker logs --since "$last_start" "$container" 2>&1 | grep "migrated to schema version 2" >/dev/null; then
  docker logs --since "$last_start" "$container" 2>&1 | grep -i -E "media ?reports|sqlite|error" | tail -40
  fail "the version 1 database was not migrated"
fi
export_reports migrated
py - "$work/results/migrated.json" <<'PY' || fail "the migrated database is not as seeded"
import json, sys
d = json.load(open(sys.argv[1]))
assert d["schemaVersion"] == 2, d["schemaVersion"]
assert len(d["issues"]) == 3 and len(d["reports"]) == 3, (len(d["issues"]), len(d["reports"]))
assert all("resolutionText" not in i for i in d["issues"])
assert all(r["snapshot"]["schemaVersion"] == 1 for r in d["reports"])
print("migrated: schema 2, 3 issues, 3 reports, snapshots readable")
PY

echo "== the export is admin-only"
viewer_token="$(sign_in viewer viewer-password)"
code="$(status_as "$viewer_token" GET /MediaReports/Admin/Export)"
[[ "$code" == 403 || "$code" == 401 ]] || fail "a non-admin got $code from the export"
echo "non-admin export: $code"
headers="$(curl -sS -D - -o /dev/null "$api/MediaReports/Admin/Export" -H "Authorization: $auth, Token=\"$token\"")"
grep -qi '^content-disposition: attachment; filename=media-reports-export-' <<<"$headers" || fail "the export is not a file download"

echo "== deleting the reporter anonymises their reports"
call DELETE "/Users/$reporter_id" >/dev/null
export_reports anonymised
py - "$work/results/anonymised.json" "$reporter_id" <<'PY' || fail "the deleted user's reports were not anonymised"
import json, sys, uuid
d = json.load(open(sys.argv[1]))
reporter = str(uuid.UUID(sys.argv[2]))
text = json.dumps(d).lower()
assert reporter not in text and reporter.replace("-", "") not in text, "user id still present"
assert "reporter's phone" not in text and '"reporter"' not in text, "name or device still present"
mine = [r for r in d["reports"] if r["issueId"] in (1, 3)]
assert len(mine) == 2 and all(r["reporterUserId"] is None and r["reporterName"] is None for r in mine)
assert sum(1 for e in d["events"] if e["kind"] == "anonymised") == 2
print("anonymised: 2 reports kept, no id, name or device left")
PY

echo "== the purge task removes the issue closed 400 days ago and keeps the open one from 730 days ago"
run_task "Purge closed media reports"
export_reports purged
py - "$work/results/purged.json" <<'PY' || fail "the purge did not keep and remove the right issues"
import json, sys
d = json.load(open(sys.argv[1]))
ids = sorted(i["id"] for i in d["issues"])
assert ids == [1, 3], ids
assert all(r["issueId"] != 2 for r in d["reports"]) and all(e["issueId"] != 2 for e in d["events"])
print("purged: issue 2 gone, issues 1 and 3 kept")
PY

echo "== the copy task writes a consistent copy a server can start on"
run_task "Copy media reports database"
MSYS_NO_PATHCONV=1 docker cp "$container:$db_dir/media-reports.backup.db" "$(host_path "$work/backup.db")"
py - "$work/backup.db" <<'PY' || fail "the copy is not a sound database"
import sqlite3, sys
db = sqlite3.connect(sys.argv[1])
assert db.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
assert db.execute("SELECT COUNT(*) FROM report").fetchone()[0] == 2
print("copy: integrity ok, 2 reports")
PY
stop_server
MSYS_NO_PATHCONV=1 docker cp "$(host_path "$work/backup.db")" "$container:$db_dir/media-reports.db"
start_server
token="$(sign_in admin admin-password)"
export_reports restored
py - "$work/results/restored.json" <<'PY' || fail "the server started on the copy does not list the reports"
import json, sys
d = json.load(open(sys.argv[1]))
assert sorted(i["id"] for i in d["issues"]) == [1, 3] and len(d["reports"]) == 2
print("restored: the server lists both issues and both reports")
PY

echo "PASS: report storage on $image"
