# Contributing

Thanks for helping. Bug reports, ideas and pull requests are all welcome.

## Reporting

- **Bugs and ideas:** open an issue using the Bug or Feature template.
- **Security problems:** do not open a public issue; see [SECURITY.md](SECURITY.md).
- **Conduct:** everyone taking part follows the [Code of Conduct](CODE_OF_CONDUCT.md).

## Building and testing

You need the .NET 10 SDK (pinned in `global.json`) and Docker.

```bash
dotnet test                          # unit tests
bash scripts/smoke-load.sh           # the plugin loads on Jellyfin 12.0, 12.1 and the latest 12.x
bash scripts/repo-install-check.sh   # Jellyfin installs it from a plugin repository and loads it
bash scripts/check-repo-docs.sh      # repository documents and third-party notices are complete
```

The scripts start their own throwaway Jellyfin containers. Test against a local container like these, never against a server other people are using.

The plugin builds against the lowest Jellyfin version it supports (`Jellyfin.Controller` 12.0.*). Don't raise that to use a newer API without raising `targetAbi` in `meta.json` too.

## Pull requests

- Keep each pull request to one change, with tests or a check that proves it.
- Write commit subjects in the [Conventional Commits](https://www.conventionalcommits.org/) style: `feat: ...`, `fix: ...`, `docs: ...`, `build: ...`, `chore: ...`.
- If you add a dependency that ships with the plugin, add it to [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) with its license. Only licenses compatible with GPL-3.0 are accepted; `scripts/check-repo-docs.sh` fails otherwise.
- Don't add the Jellyfin logo or anything that suggests the Jellyfin project endorses this plugin.

## License

This project is licensed under [GPL-3.0-or-later](LICENSE). By contributing, you agree that your contributions are licensed under the same terms.
