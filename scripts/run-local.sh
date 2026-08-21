#!/usr/bin/env bash
# Local (non-Docker) run of the AChat API for quick iteration.
# Usage: bash scripts/run-local.sh [port]
set -euo pipefail
cd "$(dirname "$0")/.."
PORT="${1:-5199}"
export ASPNETCORE_URLS="http://localhost:${PORT}"
export ASPNETCORE_ENVIRONMENT=Development
# Point at the local dev Postgres (port 5433) unless the caller already set it.
export ACHAT_ConnectionStrings__DefaultConnection="${ACHAT_ConnectionStrings__DefaultConnection:-Host=localhost;Port=5433;Database=achat;Username=achat;Password=achat}"
exec dotnet run --project src/AChat.Api --no-build
