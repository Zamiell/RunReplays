from __future__ import annotations

import re
import shutil
import subprocess
import sys
from pathlib import Path


UPSTREAM_FILES = (
    "McpMod.Actions.cs",
    "McpMod.MultiplayerActions.cs",
    "McpMod.cs",
)
RAW_BASE_URL = "https://raw.githubusercontent.com/Zamiell/STS2MCP/main"


def curl() -> str:
    executable = shutil.which("curl.exe") or shutil.which("curl")
    if executable is None:
        raise SystemExit("curl is required to validate the upstream STS2MCP actions.")
    return executable


def fetch(url: str) -> str:
    result = subprocess.run(
        [curl(), "-fsSL", url],
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    return result.stdout


def upstream_actions() -> set[str]:
    actions: set[str] = set()
    for filename in UPSTREAM_FILES:
        source = fetch(f"{RAW_BASE_URL}/{filename}")
        actions.update(re.findall(r'"([a-z][a-z0-9_]+)"\s*=>\s*Execute', source))
        actions.update(re.findall(r'action\s*==\s*"([a-z][a-z0-9_]+)"', source))
    return actions


def snapshot_actions(path: Path) -> set[str]:
    return {
        line.strip()
        for line in path.read_text(encoding="utf-8").splitlines()
        if line.strip() and not line.startswith("#")
    }


def main() -> None:
    snapshot_path = Path(__file__).with_name("sts2mcp-actions.txt")
    expected = snapshot_actions(snapshot_path)
    actual = upstream_actions()

    missing = sorted(actual - expected)
    removed = sorted(expected - actual)
    if not missing and not removed:
        print(f"STS2MCP action snapshot is current ({len(actual)} actions).")
        return

    print("STS2MCP action snapshot is out of date.", file=sys.stderr)
    if missing:
        print("\nNew upstream actions:", file=sys.stderr)
        for action in missing:
            print(f"  + {action}", file=sys.stderr)
    if removed:
        print("\nActions no longer present upstream:", file=sys.stderr)
        for action in removed:
            print(f"  - {action}", file=sys.stderr)
    print(
        f"\nUpdate {snapshot_path} after deciding how RunReplays should handle "
        "the upstream command change.",
        file=sys.stderr,
    )
    raise SystemExit(1)


if __name__ == "__main__":
    main()
