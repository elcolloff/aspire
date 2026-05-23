#!/usr/bin/env bash
# Smoke-tests the already-installed `aspire` CLI by scaffolding a starter
# project and running its restore. Assumes `aspire` is on PATH.
#
# Used by CI after a real installer run (Homebrew cask / WinGet manifest /
# dotnet-tool / archive script) to catch regressions that only show up once the
# installed bits actually launch — broken launcher resolution, missing layout
# assets, packaging-time PATH issues, etc.
set -euo pipefail

usage() {
  cat <<EOF
Usage: $(basename "$0") [OPTIONS]

Options:
  --work-dir PATH       Parent directory in which to create a fresh scaffold
                        subdirectory. A unique subdirectory is always created
                        inside it; nothing in PATH is removed.
                        Default: \${RUNNER_TEMP:-/tmp}
  --project-name NAME   Project name passed to 'aspire new'. Default: SmokeApp
  --log-level LEVEL     --log-level value passed to aspire commands. Default: trace
  --nuget-source PATH   Optional local NuGet feed directory (or v3 index URL).
                        When provided, a NuGet.config is written into the scaffold
                        dir mapping Aspire* -> this source so that 'aspire restore'
                        can resolve the same-build Aspire.AppHost.Sdk that the
                        embedded templates pinned the scaffolded AppHost to.
                        Everything non-Aspire still flows from nuget.org.
  --help                Show this help message
EOF
}

WORK_DIR=""
PROJECT_NAME="SmokeApp"
LOG_LEVEL="trace"
NUGET_SOURCE=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --work-dir) WORK_DIR="$2"; shift 2 ;;
    --project-name) PROJECT_NAME="$2"; shift 2 ;;
    --log-level) LOG_LEVEL="$2"; shift 2 ;;
    --nuget-source) NUGET_SOURCE="$2"; shift 2 ;;
    --help) usage; exit 0 ;;
    *) echo "Error: unknown option: $1" >&2; usage >&2; exit 1 ;;
  esac
done

PARENT_DIR="${WORK_DIR:-${RUNNER_TEMP:-/tmp}}"
mkdir -p "$PARENT_DIR"

# Always scaffold into a fresh subdirectory created with mktemp under
# $PARENT_DIR. This deliberately avoids ever rm -rf'ing a caller-provided
# path: even if --work-dir points at a sensitive directory, the worst case is
# a new empty aspire-cli-smoke.XXXXXX subdirectory being created underneath.
# CI tears down RUNNER_TEMP between jobs; local users can clean up whenever.
scaffold_dir="$(mktemp -d "$PARENT_DIR/aspire-cli-smoke.XXXXXX")"
echo "Scaffolding into: $scaffold_dir"

aspire --version
cd "$scaffold_dir"

aspire --log-level "$LOG_LEVEL" new aspire-starter --name "$PROJECT_NAME" --output . --non-interactive --nologo --suppress-agent-init

# Stage a source-mapped NuGet.config in CWD AFTER scaffolding (aspire new rejects a
# non-empty --output dir, so we can't drop the config before). Embedded templates mean
# `aspire new` doesn't consult NuGet at all; only `aspire restore` needs the feed —
# specifically to resolve Aspire.AppHost.Sdk at the CLI build version (e.g.
# 13.4.0-pr.<n>.g<sha>), which only exists in the per-build feed, not on nuget.org.
# Aspire* is pinned to the local feed; non-Aspire transitives still flow from nuget.org.
if [[ -n "$NUGET_SOURCE" ]]; then
  echo "Writing source-mapped NuGet.config (Aspire* -> $NUGET_SOURCE; everything else -> nuget.org)"
  cat > "$scaffold_dir/NuGet.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <fallbackPackageFolders>
    <clear />
  </fallbackPackageFolders>
  <packageSources>
    <clear />
    <add key="local-pr" value="$NUGET_SOURCE" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local-pr">
      <package pattern="Aspire*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
  <disabledPackageSources>
    <clear />
  </disabledPackageSources>
</configuration>
EOF
fi

aspire --log-level "$LOG_LEVEL" restore --non-interactive --nologo
