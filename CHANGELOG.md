# Changelog

All notable changes to Media Reports are listed here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow the
policy in the README's [Versioning](README.md#versioning) section.

## [0.1.0] - 2026-11-16

First public release, for Jellyfin 12.0 and later 12.x.

### Added

- A Report button on movie, episode, season and series pages and in the video player of
  Jellyfin Web, Jellyfin for Android and Jellyfin for webOS. The user picks a reason and
  can add a note; the playback details the server already knows are attached.
- My Reports in the user menu: each user follows their own reports, sees status changes
  and can withdraw a report while it is still new.
- The Media Reports page in the Dashboard: reports on the same title and reason grouped
  into one issue, filters and sorting, a status workflow, private notes, history, and
  Refresh metadata and Rescan for the title.
- Suspected problems: playback on every app, TV apps included, is watched for freezing,
  a transcoder slower than the video, low quality and playback errors, without anyone
  pressing a button.
- Alerts for each new report to a Discord webhook, a generic JSON webhook and Jellyfin's
  activity log, and a warning when the Report button stops loading after a Jellyfin update.
- Prometheus metrics at `/MediaReports/Metrics`, with counts only.
- Reports follow a title when its file is replaced or moved to a new item.
- Retention: closed reports deleted after 365 days by default, a daily copy of the
  database, and a JSON export of everything.
- A documented HTTP API for other clients ([docs/api.md](docs/api.md)).
- Each release carries `SHA256SUMS` and a build provenance attestation for the zip and its
  manifest entry; the README's Install section shows how to check a download.

[0.1.0]: https://github.com/pathimmersive/jellyfin-plugin-media-reports/releases/tag/v0.1.0
