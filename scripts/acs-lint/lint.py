#!/usr/bin/env python3
"""Run AGT lint on explicit ACS manifest files."""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Sequence


def lint_manifests(manifests: Sequence[Path], agt: str) -> int:
    invalid = False
    for manifest in manifests:
        if manifest.is_symlink() or not manifest.is_file():
            print(
                f"error: manifest must be an existing, non-symlink file: {manifest}",
                file=sys.stderr,
            )
            invalid = True
    if invalid:
        return 1

    failed = False
    for manifest in manifests:
        try:
            result = subprocess.run(
                [agt, "--json", "lint-policy", "--strict", str(manifest)],
                check=False,
            )
        except OSError as error:
            print(f"error: cannot run AGT for {manifest}: {error}", file=sys.stderr)
            return 1
        failed |= result.returncode != 0
    return int(failed)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifests", nargs="+", type=Path)
    args = parser.parse_args()
    agt = shutil.which("agt")
    if not agt:
        print("error: agt is not installed; run the locked ACS lint setup", file=sys.stderr)
        return 1
    return lint_manifests(args.manifests, agt)


if __name__ == "__main__":
    raise SystemExit(main())
