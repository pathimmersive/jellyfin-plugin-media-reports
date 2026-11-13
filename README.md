<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/logo/readme-dark.svg">
    <img alt="Media Reports: users flag it, your server spots it" src="assets/logo/readme-light.svg" width="480">
  </picture>
</p>

# Media Reports for Jellyfin

Media Reports is a Jellyfin server plugin that lets the users of a server report a problem with a movie, show, season or episode: it won't play, plays incorrectly, looks poor, stutters, or has the wrong audio or subtitles. The admin gets one place to view and work those reports. Each report will carry the playback details the server already knows, such as client, device, direct play or transcode and why, codecs and position, so the user only has to pick a reason.

> **Status: early development.** The plugin installs and loads but has no features yet, and no release has been published.

This is an unofficial, third-party plugin. It is not affiliated with or endorsed by the Jellyfin project.

## Requirements

- Jellyfin 12.0 or later 12.x (`targetAbi` 12.0.0.0). It is tested on 12.0, 12.1 and 12.2.

## Install

Once the first release is published:

1. In the Jellyfin dashboard, open **Plugins → Repositories** and add this repository URL:

   ```
   https://pathimmersive.github.io/jellyfin-plugin-media-reports/manifest.json
   ```

2. Open **Plugins → Catalog**, install **Media Reports**, and restart the server.

Jellyfin checks each download against the checksum in the repository manifest.

## Build from source

You need the .NET 10 SDK (pinned in `global.json`) and, for the load checks, Docker.

```bash
dotnet test                          # build and run the unit tests
bash scripts/smoke-load.sh           # load the plugin into Jellyfin 12.0, 12.1 and 12.x containers
bash scripts/repo-install-check.sh   # install it from a local plugin repository into a Jellyfin container
```

`scripts/package.sh` builds a release zip and its manifest entry. See [CONTRIBUTING.md](CONTRIBUTING.md) for how changes are made.

## What the plugin stores

Everything stays on your Jellyfin server. Data leaves the server only through the webhooks and the metrics endpoint you set up yourself; nothing is sent anywhere by default.

### Where

- **Reports database:** `media-reports/media-reports.db` in the server's data folder (`/config/data` in the official Docker image). It is created on first start and survives plugin updates and reinstalls.
- **Daily copy:** `media-reports/media-reports.backup.db`, written each day by the **Copy media reports database** scheduled task. Back up this file rather than the live one. To restore, stop the server and put the copy in place of `media-reports.db`.
- **Settings:** Jellyfin's own plugin configuration file for Media Reports, in the server's plugin configuration folder. It holds the report reasons, the reporting switch, webhook addresses and the retention periods.
- **Recent playbacks:** kept in memory only, never written to disk, and lost when the server restarts. Each user's last playbacks (at most 10) are kept for 24 hours by default, so a problem can still be reported after playback stops.

### What each table holds

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
- It holds no user, device or address.

`plugin_state`: `key` and `value`, a few named values kept across restarts: whether the Report button counts as stopped, and when reporting was last turned back on.

`finding`: one row per suspected problem the plugin noticed on its own, one per playback and kind. It never becomes a report or an issue, and is deleted 30 days after it was last seen.

- `id`, `kind` (freezing, slow-transcode, low-quality, playback-error), `playback_key` (the session and item it was seen in).
- `item_id`, `item_type`, `title`, `series_id`, `series_name`, `season_number`, `episode_number`: what was playing.
- `user_id`, `user_name`, `device`, `client`: who was watching, and on what.
- `first_seen`, `last_seen`, `detail` (what the server saw, in words), `dismissed_at`.
- It holds no file path or IP address.

### Who sees what

Admins see everything above. A user sees only their own reports, with the status and its history, never another user's reports, the file path or the admin's notes.

### How long it is kept

- Open reports are never deleted.
- Closed reports are deleted, with their history, 365 days after they closed by the daily **Purge closed media reports** task. Change the period in the plugin's settings; 0 keeps them forever.
- Suspected problems are deleted by the same task 30 days after they were last seen.
- When a Jellyfin user is deleted, their reports are kept for the admin, but their user id, user name and device names are removed from them.
- Users can withdraw their own report while it is still New.

### Metrics, export and uninstall

- The metrics endpoint carries counts only: no titles, paths or user names.
- Admins can download every report, issue and history entry as one JSON file from the plugin's settings.
- Uninstalling the plugin leaves `media-reports/` in the data folder. Delete that folder to remove all reports.

## License

[GPL-3.0-or-later](LICENSE). Third-party components are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
