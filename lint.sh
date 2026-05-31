#!/usr/bin/env bash
set -euo pipefail

if command -v python >/dev/null 2>&1; then
    PYTHON=python
elif command -v python3 >/dev/null 2>&1; then
    PYTHON=python3
elif command -v py >/dev/null 2>&1; then
    PYTHON=py
else
    echo "python, python3, or py is required to run lint.sh" >&2
    exit 1
fi

"$PYTHON" scripts/validate_sts2mcp_actions.py
