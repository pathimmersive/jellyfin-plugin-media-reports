# Media Reports API

This is the HTTP API the Media Reports plugin adds to a Jellyfin server. The Report button and the
admin's Media Reports page use it, and so can any other client. It is API version **1**. A change
that would break a client raises that number. New fields and new routes don't raise it.

Every path below is relative to the server's address, including any base URL
(`https://jellyfin.example.com/jellyfin/MediaReports/Info`).

## Basics

**Authentication.** Send a Jellyfin token in the `Authorization` header, as every Jellyfin client does:

```
Authorization: MediaBrowser Client="My App", Device="Pixel 9", DeviceId="abc123", Version="1.2.0", Token="<access token>"
```

- *User* routes take any signed-in user's token and act only as that user. No route takes a user id.
- *Admin* routes need the token of a Jellyfin administrator, or a Jellyfin API key.
- One route, the Report button script, takes no token.

**Bodies.** Requests and answers are JSON. Jellyfin writes field names in PascalCase (`ReasonCode`).
A request that asks for Jellyfin's camelCase profile (`Accept: application/json;
profile="CamelCase"`) gets camelCase everywhere, error bodies included. Jellyfin leaves a field out when
its value is null, so treat a missing field as null. Times are ISO 8601 with an offset. Item ids are
Jellyfin's GUIDs. Report, issue and finding ids are numbers.

**Values you don't know.** Within API version 1 new values and new fields are added without
raising the version. Accept them:

- a status, reason code, error `Code`, playback `State` or item type you don't recognise: show
  its label, or treat it as a generic one, rather than failing;
- fields you don't expect: ignore them;
- the reportable types from `GET /MediaReports/Info`: offer a Report button only on those.

**Errors.** Every refusal has a JSON body with a stable `Code` and a `Message` for people:

```json
{ "Code": "already-reported", "Message": "You already have an open report on this title for that reason." }
```

Write code against `Code`; `Message` wording may change. Some refusals add a field that names
what was refused: `IssueId` or `IssueIds` on issue changes, and `Field` and `ReasonIndex` on
settings. These codes can come from any route:

| Status | Code | When |
|---|---|---|
| 400 | `bad-request` | The body or a parameter can't be read (bad JSON, a word where a number goes). |
| 401 | `unauthorized` | No token, or a token Jellyfin doesn't accept. |
| 403 | `forbidden` | The token is valid but not allowed here, such as a user's token on an admin route. |
| 404 | `not-found` | No such route under `/MediaReports`. |
| 405 | `method-not-allowed` | The route exists but doesn't take that method. |
| 415 | `unsupported-media-type` | The body isn't sent as `application/json`. |
| 503 | `storage-unavailable` | The plugin's database didn't open. Jellyfin's log says why. |

**Statuses.** User routes give a report's status as a code from this set, with a `StatusLabel`
for people beside it:

| Code | Label | Closed |
|---|---|---|
| `new` | New | no |
| `investigating` | Being looked at | no |
| `fixed` | Fixed | yes |
| `wont-fix` | Won't fix | yes |
| `duplicate` | Duplicate | yes |
| `cant-reproduce` | Can't reproduce | yes |

`investigating` also covers a report whose title's file changed, while the admin checks the
fix. Admin routes use the same codes plus the plugin's own `replaced-verify` (the title's file
changed and the admin still has to check the fix), and `mixed` for a season row whose episodes
differ: `new`, `investigating`, `replaced-verify`, `fixed`, `wont-fix`, `duplicate`,
`cant-reproduce`.

## Plugin

### `GET /MediaReports/Info`

*User.* Says the plugin is installed and which API it speaks. Call it first. A 404 means the plugin
isn't installed.

```json
{ "Version": "0.1.0.0", "ApiVersion": 1, "ReportingEnabled": true,
  "ReportableTypes": ["Movie", "Episode", "Season", "Series"] }
```

`ReportingEnabled` is false while the admin has turned reporting off. Filing is refused then, but
a user can still read their reports. `ReportableTypes` are the Jellyfin item types a report can be
filed on; offer a Report button only on those.

## Filing and following reports

### `GET /MediaReports/Report/Context?itemId=<id>`

*User.* Describes what the caller is about to report: a movie, episode, season or series.

```json
{
  "ItemId": "6f1c…", "Name": "Pilot", "Type": "Episode",
  "SeriesName": "Test Series", "ParentIndexNumber": 2, "IndexNumber": 5, "ProductionYear": 2021,
  "NoteMaxLength": 1000,
  "Reasons": [ { "Code": "stutter", "Label": "Stutter or jitter" } ],
  "OpenReports": [ { "Id": 12, "ReasonCode": "audio", "ReasonLabel": "Audio problem", "Status": "new" } ],
  "Playback": { "DeviceName": "Living Room TV", "Client": "Jellyfin Roku", "State": "Stopped", "LastSeen": "2026-11-14T19:02:11+00:00" }
}
```

`SeriesName`, `ParentIndexNumber`, `IndexNumber` and `ProductionYear` are the title's parts, with
Jellyfin's names and meanings, for a client that formats the title itself: for an episode the
series, season and episode; for a season the series and, in `IndexNumber`, the season; the year for
any title. A part the title doesn't have is left out. `NoteMaxLength` is the longest note a report
takes. `Reasons` are the active reasons, in the admin's order. `OpenReports` are the caller's own open
reports on this title. `Playback` is the playback a report would carry: the caller's live playback
of the item, preferring this device, or their most recent one. `State` is `Playing`, `Stopped` or
`Ended`. It is null when there is none, and always null for a season or series.

Refusals: 400 `item-not-reportable` (a type not in `ReportableTypes`, such as a song); 403
`reporting-off`; 404 `item-not-found` (unknown, or not visible to the caller).

### `POST /MediaReports/Report`

*User.* Files a report. The server picks and records the playback, as Context describes.

```json
{ "itemId": "6f1c…", "reasonCode": "stutter", "note": "Freezes at the start of every scene." }
```

`note` is optional, up to `NoteMaxLength` (1,000) characters. Answers 201, with a `Location`
header naming the report, `/MediaReports/Report/Mine/{id}`:

```json
{ "Id": 31, "IssueId": 9, "ItemId": "6f1c…", "ReasonCode": "stutter", "Status": "new",
  "CreatedAt": "2026-11-14T19:05:40+00:00", "Playback": { "DeviceName": "Living Room TV", "Client": "Jellyfin Roku", "State": "Stopped", "LastSeen": "…" } }
```

Reports from several people about one title and reason join one issue (`IssueId`).

Refusals:

| Status | Code | When |
|---|---|---|
| 400 | `reason-not-allowed` | The reason is unknown or retired. |
| 400 | `note-too-long` | The note is over 1,000 characters. |
| 400 | `item-not-reportable` | The item's type isn't in `ReportableTypes`, such as a song or a recording. |
| 403 | `reporting-off` | The admin turned reporting off. |
| 404 | `item-not-found` | The item is unknown, or the caller can't see it. |
| 409 | `already-reported` | The caller already has an open report for this reason on this title. `ReportId` names it, so a client can open it instead. |
| 429 | `too-many-reports` | The caller filed 30 reports in the past hour, withdrawn ones included. The `Retry-After` header gives the seconds until a filing is accepted again. |

One user's filings are handled one at a time, so requests sent together still meet the limit and
the one-open-report-per-reason rule.

### `GET /MediaReports/Report/Mine?page=<n>`

*User.* The caller's reports, newest first, 50 a page, and how many are unread across every page.
`page` starts at 1 and is 1 when left out. Reports the caller removed from their list are left out.

```json
{
  "Reports": [ {
    "Id": 31, "ItemId": "6f1c…", "Title": "Test Series S02E05",
    "SeriesName": "Test Series", "ParentIndexNumber": 2, "IndexNumber": 5, "ProductionYear": 2021, "ReasonCode": "stutter",
    "ReasonLabel": "Stutter or jitter", "CreatedAt": "…", "Status": "fixed", "StatusLabel": "Fixed", "Unread": true,
    "ExpiresAt": "2027-11-14T20:00:00+00:00", "ExpiresLabel": "Expires in 12 months",
    "CanHide": true, "CanWithdraw": false
  } ],
  "Unread": 1, "Page": 1, "PageSize": 50, "Total": 1
}
```

The title's parts are as in Context. `Total` counts every page; a page past the end has an empty
`Reports`. `Unread` is true when the status changed after the caller last marked the report seen.
`ExpiresAt` is when a closed report is deleted under the admin's retention setting. It is null
while the report is open or when nothing is deleted. Works while reporting is off.

Refusal: 400 `bad-query` for a page below 1.

### `GET /MediaReports/Report/Mine/{id}`

*User.* Opens one of the caller's reports. Reading it changes nothing; call `Seen` when the user
sees it.

```json
{ "Id": 31, "ItemId": "6f1c…", "Title": "Test Series S02E05",
  "SeriesName": "Test Series", "ParentIndexNumber": 2, "IndexNumber": 5, "ProductionYear": 2021,
  "ReasonCode": "stutter", "ReasonLabel": "Stutter or jitter", "Note": "Freezes at the start of every scene.",
  "Device": "Living Room TV", "CreatedAt": "…", "Status": "fixed", "StatusLabel": "Fixed",
  "CanWithdraw": false,
  "History": [ { "At": "…", "Label": "Reported" },
               { "At": "…", "Label": "Fixed", "Status": "fixed" } ] }
```

The title's parts are as in Context. `ReasonCode` and each step's `Status` are codes a client can
translate; the filing step has no `Status`. The labels are English text for people.

Refusal: 404 `report-not-found`, also for another user's report.

### `POST /MediaReports/Report/Mine/{id}/Seen`

*User.* Records that the caller has seen the report's current status, so it is no longer unread.
Answers 204. Call it when the report is shown to the user, not when it is fetched ahead.

Refusal: 404 `report-not-found`, also for another user's report.

### `DELETE /MediaReports/Report/Mine/{id}`

*User.* Withdraws a report while its status is `new`. Answers 204.

Refusals: 403 `reporting-off`; 404 `report-not-found`; 409 `not-new`.

### `POST /MediaReports/Report/Mine/{id}/Hide`

*User.* Removes a closed report from the caller's list. Answers 204. Works while reporting is off.

Refusals: 404 `report-not-found`; 409 `not-closed`.

### `POST /MediaReports/Report/Mine/HideClosed`

*User.* Removes every closed report from the caller's list. Works while reporting is off.

```json
{ "Hidden": 2 }
```

### `GET /MediaReports/Playbacks`

*User.* The caller's own live and recent playbacks, newest first, as the plugin saw them: a list
of the objects below. No other user's playbacks are returned, and no session, device, media
source or user id is.

Fields of `Playback`:

| Field | Meaning |
|---|---|
| `State` | `Playing`, `Stopped` or `Ended`. |
| `LastSeen` | When the server last saw it. |
| `Client` | The app's name, such as `Jellyfin Roku`. |
| `ClientVersion` | The app's version. |
| `DeviceName` | The device's name. |
| `ItemId` | The item played. |
| `ItemName` | The item's name. |
| `PlayMethod` | `DirectPlay`, `DirectStream` or `Transcode`. |
| `PositionTicks` | The position, in ticks of 100 ns. |
| `AudioStreamIndex` | The audio stream in use. |
| `SubtitleStreamIndex` | The subtitle stream in use. |
| `Transcoding` | The transcode, or null. |
| `SourceStreams` | The source file's streams. |
| `SourceBitrate` | The source file's bitrate. |

Fields of `Transcoding`:

| Field | Meaning |
|---|---|
| `VideoCodec` | The output video codec. |
| `AudioCodec` | The output audio codec. |
| `Container` | The output container. |
| `IsVideoDirect` | Whether video is copied. |
| `IsAudioDirect` | Whether audio is copied. |
| `Bitrate` | The output bitrate. |
| `Width` | The output width. |
| `Height` | The output height. |
| `HardwareAccelerationType` | Jellyfin's name for the hardware acceleration. |
| `TranscodeReasons` | Why the server transcoded, with Jellyfin's own names. |

Fields of `SourceStreams`:

| Field | Meaning |
|---|---|
| `Index` | The stream index. |
| `Type` | `Video`, `Audio` or `Subtitle`. |
| `Codec` | The codec. |
| `Language` | The language. |
| `Bitrate` | The bitrate. |
| `Width` | The width. |
| `Height` | The height. |
| `Channels` | The channel count. |
| `FrameRate` | The frame rate, for a video stream. |

### `POST /MediaReports/Client/Loaded`

*User.* Only the Report button script in Jellyfin's web client calls it; a native client doesn't
need to. Records that the Report button loaded in the caller's app. The app is the `Client` and
`Version` in the caller's `Authorization` header. Any body is ignored. At most one load per app
is kept per minute. Answers 204.

Refusals: 400 `bad-client` (the header names no client, or one over 100 characters).

### `GET /MediaReports/web/reporter.js`

*No token.* The Report button script that jellyfin-web loads. Answers 304 when the client's
copy is current (`ETag`).

## Admin

Every route here is *Admin*. With a user's token they answer 403 `forbidden`. The admin sees
issues, the reports grouped under them, and their history.

### `GET /MediaReports/Admin/Summary`

Open issues, their reports, and how many issues are new for the calling admin since they last
looked. Reading it changes nothing.

```json
{ "OpenIssues": 6, "Reports": 7, "New": 6, "ButtonStopped": false, "Suspected": 3 }
```

`ButtonStopped` is true when the Report button stopped loading in an app that used to load it.
`Suspected` is the 7-day count of suspected problems not dismissed.

### `POST /MediaReports/Admin/Summary/Seen`

Records now as the calling admin's last look, so Summary counts nothing new until another report
comes in. Call it once the admin has seen the counts. Answers 204.

### `GET /MediaReports/Admin/Issues`

One page of issues. Query:

| Parameter | Values | Default |
|---|---|---|
| `view` | `open`, `closed`, `all` | `open` |
| `groupBy` | `title`, `titleClient` | `title` |
| `reason` | a reason code | all |
| `client` | a client name | all |
| `q` | text in the title | none |
| `sort` | `title`, `reports`, `status`, `last` | `last` (newest first) |
| `page` | from 1, 50 rows a page | 1 |

```json
{ "Rows": [ { "Kind": "issue", "IssueId": 9, "ItemId": "6f1c…", "ItemType": "Episode", "Title": "Test Series",
    "Line": "S02E05 · Pilot", "ReasonCode": "stutter", "ReasonLabel": "Stutter or jitter", "Reports": 2, "People": 2,
    "Clients": ["Jellyfin Roku"], "Client": null, "PlayMethods": [ { "Method": "Transcode", "Count": 2 } ],
    "Status": "new", "StatusLabel": "New", "LastReport": "…", "IsNew": true } ],
  "Total": 120, "Page": 1, "PageSize": 50, "Reasons": [ { "Code": "stutter", "Label": "Stutter or jitter" } ], "Clients": ["Jellyfin Roku"] }
```

Three or more episode issues of one season with the same reason come back as one row with
`Kind` `season`, `SeriesId`, `SeasonNumber`, `EpisodeCount`, and the episode rows in `Children`.
Its `Status` is the shared status, or `mixed`.

Refusal: 400 `bad-query` (an unknown `view`, `groupBy` or `sort`, or a page below 1).

### `GET /MediaReports/Admin/Issues/{id}`

One issue: `Status`, `StatusLabel`, `Notes`, `CreatedAt`, `UpdatedAt`, `ClosedAt`, `People`;
`Reports`, newest first, each with `ReporterName`, `Device`, `Client`, `PlayMethod`,
`TranscodeReasons`, `PositionTicks`, `Note` and the full playback `Snapshot`; `History`, oldest
first (`At`, `Kind`, `Detail`, `ActorName`); `ServerId`; and `File`, read from Jellyfin now:
`InLibrary`, `Path`, `Size`, `DateAdded`, `Container`, `Bitrate`, `Streams`, and for a season
or series its folder and `EpisodeCount`. When the item is gone, `InLibrary` is false and `Path`
is the one stored with the newest report.

Refusal: 404 `issue-not-found`.

### `PATCH /MediaReports/Admin/Issues/{id}`

Changes an issue's status, notes, or both, and answers with the issue as above.

```json
{ "status": "fixed", "notes": "Re-encoded the file." }
```

A status change is logged in the issue's history. A closed status (`fixed`, `wont-fix`,
`duplicate`, `cant-reproduce`) sets `ClosedAt` and tells the reporters. Reopening clears it.

Refusals:

| Status | Code | When |
|---|---|---|
| 400 | `status-not-allowed` | An unknown status, or `replaced-verify`, which only the plugin sets. |
| 400 | `notes-too-long` | Notes over 10,000 characters. |
| 404 | `issue-not-found` | No such issue. |
| 409 | `newer-open-issue` | A reopen while a newer open issue exists for the same title and reason. `IssueId` names it. |

### `POST /MediaReports/Admin/Issues/Status`

Sets one status on several issues, all or none. Answers with the ids set.

```json
{ "issueIds": [9, 10, 11], "status": "investigating" }
```

Refusals are those of PATCH, plus 400 `no-issues` for an empty list. `IssueIds` names every
refused issue, and nothing changes.

### `POST /MediaReports/Admin/Issues/{id}/Refresh`

Queues Jellyfin's metadata refresh of the issue's title, filling missing metadata and images.
Answers 202 at once.

Refusals: 404 `issue-not-found`; 404 `not-in-library` (the title is gone).

### `POST /MediaReports/Admin/Issues/{id}/Scan`

Queues Jellyfin's scan for new and changed files in the folder that holds the title: an
episode's season folder, or a season's or series' own folder. For a movie it refreshes the movie,
which rereads its file. Never the whole library. Answers 202 at once.

Refusals: 404 `issue-not-found`; 404 `not-in-library`.

### `GET /MediaReports/Admin/Findings?kind=<kind>`

Suspected problems the plugin noticed during playback, without anyone reporting them. `Kinds`
gives each kind's count over the past 7 days. `Rows` lists findings not dismissed, newest first,
at most 200.

```json
{ "Kinds": [ { "Kind": "slow-transcode", "Label": "Transcoder too slow", "Count": 3 } ],
  "Rows": [ { "Id": 4, "Kind": "slow-transcode", "KindLabel": "Transcoder too slow", "ItemId": "…", "Title": "Test Movie",
    "Line": null, "UserName": "alice", "Device": "Living Room TV", "Client": "Jellyfin Roku",
    "FirstSeen": "…", "LastSeen": "…", "Detail": "Transcoding at 14.2 fps against 23.98 fps for 45 s", "OtherDevices": 1 } ] }
```

Kinds: `freezing`, `slow-transcode`, `low-quality`, `playback-error`.
Refusal: 400 `unknown-kind`.

### `POST /MediaReports/Admin/Findings/{id}/Dismiss`

Hides a finding. Answers 204. Refusal: 404 `finding-not-found`.

### `GET /MediaReports/Admin/Settings`

```json
{ "ReportingEnabled": true,
  "Reasons": [ { "Code": "stutter", "Label": "Stutter or jitter", "Retired": false, "Reports": 4 } ],
  "ReportRetentionDays": 365, "SuspectFreezing": true, "SuspectSlowTranscode": true,
  "SuspectLowQuality": true, "SuspectPlaybackError": true,
  "DiscordWebhookUrl": "", "WebhookUrl": "", "MetricsUrl": "https://jellyfin.example.com/MediaReports/Metrics" }
```

### `PUT /MediaReports/Admin/Settings`

Checks and saves the settings. A field left out keeps its saved value. A reason without a `code`
is new. Answers with the saved settings. Nothing is saved when anything is refused.

Refusals, each 400 with `Field` (and `ReasonIndex` for a reason): `label-empty`,
`label-too-long` (over 40 characters), `label-duplicate`, `no-active-reason`, `code-duplicate`,
`code-in-use`, `retention-invalid` (negative or not a whole number), `webhook-invalid` (not http
or https).

### `POST /MediaReports/Admin/Settings/TestAlert`

Sends one sample report to each webhook address given, saved or not. Saves nothing.

```json
{ "discordWebhookUrl": "https://discord.com/api/webhooks/…", "webhookUrl": "" }
```

Answers one result per address given:

```json
[ { "Field": "discordWebhookUrl", "Accepted": false, "Status": 404, "Message": "Answered 404" } ]
```

### `GET /MediaReports/Admin/Export`

Downloads every stored report, issue and event as one JSON file,
`media-reports-export-<date>.json`.

### `GET /MediaReports/Metrics`

Counts in the Prometheus text format, for a scraper with a Jellyfin API key. It answers even when
the database didn't open, with `media_reports_storage_up 0`. Labels hold reason codes and client
names only, never titles, paths or user names.
