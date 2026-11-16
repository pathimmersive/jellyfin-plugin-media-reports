#!/usr/bin/env bash
# Lints the GitHub Actions workflows with actionlint, run from its Docker image so nothing
# is installed. Fails on any finding.
#
# Usage: scripts/check-workflows.sh
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

# Under Git Bash on Windows, Docker needs a Windows path and no path rewriting.
host_dir="$(pwd -W 2>/dev/null || pwd)"
MSYS_NO_PATHCONV=1 docker run --rm -v "$host_dir:/repo" -w /repo rhysd/actionlint:1.7.7
echo "PASS: the workflows pass actionlint"
