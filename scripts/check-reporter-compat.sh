#!/usr/bin/env bash
# Checks the Report button script against the oldest browser it supports, Chromium 38 (webOS 3):
# JavaScript syntax, web APIs and CSS. Installs the pinned checking tools from scripts/compat.
#
# Usage: scripts/check-reporter-compat.sh [file]   (default: the plugin's reporter.js)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
args=()
if [[ $# -gt 0 ]]; then
  file="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
  if command -v cygpath >/dev/null 2>&1; then file="$(cygpath -w "$file")"; fi
  args=("$file")
fi
cd "$repo_root/scripts/compat"
npm ci --silent --no-audit --no-fund >/dev/null
node check.mjs "${args[@]}"
