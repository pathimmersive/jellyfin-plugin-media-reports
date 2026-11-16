#!/usr/bin/env bash
# Checks the GitHub repository's description, homepage and topics say what the plugin does,
# so they can't drift from the README. Reads them with the GitHub CLI; needs `gh auth login`.
#
# Usage: scripts/check-repo-settings.sh
set -euo pipefail

repo="pathimmersive/jellyfin-plugin-media-reports"
manifest_url="https://pathimmersive.github.io/jellyfin-plugin-media-reports/manifest.json"

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

description="$(gh repo view "$repo" --json description --jq .description)"
homepage="$(gh repo view "$repo" --json homepageUrl --jq .homepageUrl)"
topics="$(gh repo view "$repo" --json repositoryTopics --jq '[.repositoryTopics[]?.name] | join(" ")')"

problems=0
problem() { echo "FAIL: $*" >&2; problems=$((problems + 1)); }

grep -qi "report" <<<"$description" || problem "the description doesn't mention users reporting problems: $description"
grep -qi "every app" <<<"$description" || problem "the description doesn't say playback is watched on every app: $description"
grep -qi "without anyone reporting" <<<"$description" || problem "the description doesn't mention problems noticed without a report: $description"
[[ "$homepage" == "$manifest_url" ]] || problem "the homepage is '$homepage', not $manifest_url"
for topic in jellyfin jellyfin-plugin playback monitoring; do
  grep -qw -- "$topic" <<<"$topics" || problem "the topics ($topics) leave out $topic"
done

(( problems == 0 )) || fail "$problems problem(s)"
echo "PASS: the repository's description, homepage and topics match the plugin"
