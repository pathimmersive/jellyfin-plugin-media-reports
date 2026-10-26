# Third-party notices

Media Reports is licensed under GPL-3.0-or-later. Third-party components keep their own licenses, and only licenses compatible with GPL-3.0 are accepted.

## Distributed with the plugin

Components shipped in the plugin's release zip. `scripts/check-repo-docs.sh` fails if the published plugin contains a package that is not listed here.

| Package | License |
|---|---|

None. The plugin uses the Jellyfin server libraries (`Jellyfin.Controller`, `Jellyfin.Model`, GPL-3.0) at runtime, but the server provides them and they are not shipped with the plugin.

## Used only to build and test

Not distributed with the plugin.

| Package | License |
|---|---|
| Microsoft.NET.Test.Sdk | MIT |
| Moq | BSD-3-Clause |
| xunit | Apache-2.0 |
| xunit.runner.visualstudio | Apache-2.0 |
