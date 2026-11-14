# What the plugin stores

What Media Reports keeps about a server's users, where it keeps it, who can see it and for how long.

Everything stays on your Jellyfin server. Data leaves the server only through the webhooks and the metrics endpoint you set up yourself; nothing is sent anywhere by default.

## Where

- **Reports database:** `media-reports/media-reports.db` in the server's data folder (`/config/data` in the official Docker image). It is created on first start and survives plugin updates and reinstalls.
- **Daily copy:** `media-reports/media-reports.backup.db`, written each day by the **Copy media reports database** scheduled task. Back up this file rather than the live one. To restore, stop the server and put the copy in place of `media-reports.db`.
- **Settings:** Jellyfin's own plugin configuration file for Media Reports, in the server's plugin configuration folder. It holds the report reasons, the reporting switch, the suspected-problem switches, the webhook addresses and the retention periods.
- **Recent playbacks:** kept in memory only, never written to disk, and lost when the server restarts. Each user's last playbacks (at most 10) are kept for 24 hours by default, so a problem can still be reported after playback stops.

## What each table holds

`report`: one row per report a user files.

- `id`, `created_at`, `issue_id` (the issue it was grouped into).
- `reporter_user_id`, `reporter_name`: who filed it, by Jellyfin user id and the user name at the time.
- `item_id`, `item_type`, `item_name`, `provider_ids` (TMDb, TVDb, IMDb), `series_id`, `series_name`, `series_provider_ids`, `season_number`, `episode_number`: what was reported.
- `file_path`, `file_size`, `file_modified`: the file at report time. The path is shown only to admins.
- `reason_code`, `note`: the reason picked and the user's own note.
- `snapshot`: the playback details, as JSON: client and version, device name, play method, transcode reasons and output, position, the audio and subtitle streams, and the source file's streams. It holds no IP address, no file path and no user id.
- `play_method`, `transcode_reasons`, `client`, `device`: copies from the snapshot, for filtering.
- `reporter_seen_at`: when the reporter last opened the report, for the unread mark.
- `reporter_hidden_at`: when the reporter removed the closed report from their own list.

`issue`: one row per problem, grouping reports on the same title and reason.

- `id`, `item_id`, `reason_code`, `status`, `created_at`, `updated_at`, `closed_at`.
- `admin_notes`: private notes, seen only by admins.
- `checked_file_size`, `checked_file_modified`: the file the issue was last checked against, to notice a replaced file.

`event`: the history of each issue.

- `id`, `at`, `issue_id`, `report_id`, `kind` (filed, withdrawn, status changed, note added, action run, relinked, purged, anonymised), `detail`.
- `actor_user_id`: who did it, empty for the plugin itself.

`admin_seen`: `user_id` and `seen_at`, when each admin last opened the report list.

`client_seen`: one row per app name (such as Jellyfin Web), to warn when the Report button stops loading.

- `client`, `version`: the app's name and version.
- `first_seen`, `last_load`, `last_play`: when the row was written, when the Report button last loaded in that app, and when a playback last started in it.
- It holds no user, device or address. At most 50 apps are kept; a new one replaces the one seen least recently.

`plugin_state`: `key` and `value`, a few named values kept across restarts: whether the Report button counts as stopped, and when reporting was last turned back on.

`finding`: one row per suspected problem the plugin noticed on its own, one per playback and kind. It never becomes a report or an issue, and is deleted 30 days after it was last seen.

- `id`, `kind` (freezing, slow-transcode, low-quality, playback-error), `playback_key` (the session and item it was seen in).
- `item_id`, `item_type`, `title`, `series_id`, `series_name`, `season_number`, `episode_number`: what was playing.
- `user_id`, `user_name`, `device`, `client`: who was watching, and on what.
- `first_seen`, `last_seen`, `detail` (what the server saw, in words), `dismissed_at`.
- It holds no file path or IP address.

## Who sees what

Admins see everything above. A user sees only their own reports, with the status and its history, never another user's reports, the file path or the admin's notes.

## How long it is kept

- Open reports are never deleted.
- Closed reports are deleted, with their history, 365 days after they closed by the daily **Purge closed media reports** task. Change the period in the plugin's settings; 0 keeps them forever.
- Suspected problems are deleted by the same task 30 days after they were last seen.
- Apps in `client_seen` are deleted by the same task 90 days after they last loaded the button or played.
- When a Jellyfin user is deleted, their reports and suspected problems are kept for the admin, but their user id, user name and device names are removed from them.
- Users can withdraw their own report while it is still New.

## Metrics, export and uninstall

- The metrics endpoint carries counts only: no titles, paths or user names.
- Admins can download every report, issue and history entry as one JSON file from the plugin's settings.
- Uninstalling the plugin leaves `media-reports/` in the data folder. Delete that folder to remove all reports.
