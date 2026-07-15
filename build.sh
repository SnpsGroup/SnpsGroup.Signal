#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD_PROJECT="$SCRIPT_DIR/src/build/_build.csproj"

dotnet run --project "$BUILD_PROJECT" -- "$@"
