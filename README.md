# Media Reports for Jellyfin

Media Reports is a Jellyfin server plugin that lets the users of a server report a problem with a movie, show, season or episode: it won't play, plays incorrectly, looks poor, stutters, or has the wrong audio or subtitles. The admin gets one place to view and work those reports. Each report will carry the playback details the server already knows, such as client, device, direct play or transcode and why, codecs and position, so the user only has to pick a reason.

> **Status: early development.** The plugin installs and loads but has no features yet, and no release has been published. See [MANIFEST.md](MANIFEST.md) for the plan.

This is an unofficial, third-party plugin. It is not affiliated with or endorsed by the Jellyfin project.

## Requirements

- Jellyfin 12.0 or later 12.x (`targetAbi` 12.0.0.0). It is tested on 12.0, 12.1 and 12.2.

## Install

Once the first release is published:

1. In the Jellyfin dashboard, open **Plugins → Repositories** and add this repository URL:

   ```
   https://path-immersive.gitlab.io/open-source/jellyfin/jellyfin-plugin-media-reports/manifest.json
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

## What it stores

This version stores nothing beyond Jellyfin's standard, empty plugin configuration file. Reports will hold user names, device names and playback session details; they will stay on your server and be documented here before any release that stores them.

## License

[GPL-3.0-or-later](LICENSE). Third-party components are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
