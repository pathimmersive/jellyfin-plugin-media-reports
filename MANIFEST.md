# Jellyfin Media Reports Plugin
This is a greenfield, open source Jellyfin server plugin that lets users of a Jellyfin server report problems with a specific movie, show, season, or episode, and gives the server admin a place to view and work those reports. Today the users of my server tell me casually when something is wrong (won't play, plays incorrectly, poor quality, stutters, wrong audio or subtitles), and those reports get lost or arrive without enough detail to act on. Jellyfin has no built-in way to do this; the maintainers declined it in 2024 as out of scope. The closest existing option is Jellyfin-Enhanced's "Report Issue" button, which only files into Seerr, has four fixed issue types and two statuses, and captures no playback diagnostics.

The key advantage of doing this as a plugin is that the server already knows how the media was being played when the problem happened: client, device, direct play vs. transcode, transcode reasons, codecs, bitrate, position. Every report should carry that context automatically so the user only needs to pick a reason.

## Major Features
1. A plugin-hosted, phone-friendly report page (e.g. `/Report/`) that works for every user regardless of which client they watch on:
    - Shows "Playing now on <device>" and "Recently watched" for the signed-in user, so a problem on a TV can be reported from a phone in two taps
    - Search fallback for anything else
    - Reachable via a sidebar link, a QR code, or a phone home-screen shortcut
2. Report contents:
    - The item (movie, series, season, or episode), a reason from an admin-editable list (won't play, playback error, poor quality, stutter/jitter, audio problem, subtitle problem, wrong metadata, other), and an optional note
    - Automatically attached diagnostics from the user's current or most recent playback session: client and version, device, play method, transcode info and reasons, media stream details, playback position
3. Admin triage page in the Jellyfin Dashboard:
    - Table of reports with filtering and sorting
    - Statuses: New, Investigating, Fixed, Won't Fix, Duplicate, Can't Reproduce, plus "Replaced – verify" when the underlying file changes
    - Reports grouped by item and reason so repeated reports on the same title show as one problem
    - Item details at a glance (file path, media info) with a link to open the item in Jellyfin
    - Quick actions: refresh metadata, rescan
4. Notifications:
    - Admin alerts through a Discord or generic webhook, plus an entry in Jellyfin's activity log
    - Reporter feedback when a report is resolved: an on-screen message if they are connected, otherwise a "My Reports" list with an unread indicator
5. Optional in-app "Report a problem" button in jellyfin-web based clients (browser, Android app, iOS app, possibly webOS). Off by default and isolated so a jellyfin-web change can only break the button, never reporting itself.
6. Later phases:
    - "Suspected problems" view built from playback heuristics (early stops, restart loops, repeated stops at the same position, unexpected transcodes). These never file reports automatically.
    - Radarr/Sonarr integration (delete file and search for a replacement), off by default
    - Optional forwarding of reports to Seerr

## Technical Considerations
1. Server side is a C# Jellyfin plugin on .NET 10 targeting the Jellyfin 12.x plugin ABI (`Jellyfin.Controller 12.*`). Plugins must be rebuilt for every Jellyfin major release.
2. Plugin storage is a private SQLite file in the plugin data folder. Plugins cannot add tables to `jellyfin.db`.
3. Playback context comes from `ISessionManager` events (`PlaybackStart`, `PlaybackProgress`, `PlaybackStopped`) and session/transcoding info. The plugin keeps a short per-user buffer of recent playback state so reports filed after playback stops still carry diagnostics.
4. Reports store the Jellyfin item ID plus provider IDs (TMDb/TVDb/IMDb), season/episode numbers, and the original file path. Jellyfin item IDs are derived from the file path, so a Radarr/Sonarr upgrade that renames a file creates a new item; reports must re-link to the replacement.
5. Jellyfin 12.0 has no built-in notification system and the Webhook plugin cannot carry custom plugin events, so the plugin sends its own webhooks.
6. The report page authenticates with the signed-in user's existing jellyfin-web session; admin endpoints require the `RequiresElevation` policy.
7. Distribution is a self-hosted Jellyfin plugin repository `manifest.json` (GitLab Pages) with release artifacts and checksums produced by CI.

## Open Source Considerations
1. The repository is public and licensed GPL-3.0-or-later, consistent with the Jellyfin plugin template and the GPL licensing of the Jellyfin libraries the plugin links against.
2. Third-party dependencies keep their own licenses and are listed in `THIRD_PARTY_NOTICES.md`. Only GPL-3.0-compatible licenses are accepted.
3. The plugin is an unofficial, third-party project. It does not use the Jellyfin logo or imply endorsement by the Jellyfin project.
4. Standard community files: README, LICENSE, CONTRIBUTING, CODE_OF_CONDUCT, SECURITY, and issue/MR templates.
5. Reports contain user names, device names, and IP-adjacent session data. The plugin documents what it stores, keeps it on the server, and gives the admin a retention setting.
6. No private Path Immersive infrastructure details (hostnames, registry URLs, credentials, server configs) are committed to this repository.

## Known Unknowns (first spike)
The first milestone is a one-week spike that answers these before any feature work:
1. Report a problem from a phone via QR code while a TV is playing something that forces a transcode, then again after playback stops. Confirm the diagnostics captured in both cases.
2. Does transcode info survive after playback stops, and for how long?
3. Which clients in use on my server display an on-screen session message (`DisplayMessage`)?
4. Which clients do the users of my server actually use? Log it before deciding how much the in-app button matters.
5. Confirm the `TranscodeReason` values and session fields against the v12.0 source rather than `master`.

## References
- `docs/research/jellyfin-media-issue-reporting-plugin.md` — prior art, client coverage, diagnostics, triage design, effort estimates (roughly 13–15 person-weeks for the full plan, 1–2 weeks per year of maintenance)
