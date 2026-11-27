#!/usr/bin/env bash
set -euo pipefail
SELF_DIR="$(cd "$(dirname "$0")" && pwd)"
exec "$SELF_DIR/usr/bin/DbConnect" "$@"
