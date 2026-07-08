#!/usr/bin/env bash
# Local/CI gate: backend tests + frontend build.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

echo "▶ dotnet test (Release)"
dotnet test EventForge.Tests/EventForge.Tests.csproj --configuration Release --verbosity normal

echo "▶ web build"
(
  cd web
  npm ci
  node node_modules/typescript/bin/tsc -b
  node node_modules/vite/bin/vite.js build
)

echo "✓ CI checks passed"
