#!/usr/bin/env python3
"""Materialize a verified CLI policy export as one ACS artifact directory."""

from __future__ import annotations

import argparse
import hashlib
import hmac
import json
import re
import struct
import sys
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[2]
MAX_EXPORT_BYTES = 16 * 1024 * 1024
MAX_MANIFEST_BYTES = 65_536
MAX_FILE_COUNT = 64
MAX_FILE_BYTES = 262_144
MAX_ARTIFACT_BYTES = 1_048_576
ARTIFACT_PREFIX = b"agentweaver-native-acs-artifact-v1\0"
SHA256_PATTERN = re.compile(r"[0-9a-f]{64}\Z")


def _unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON property: {key}")
        result[key] = value
    return result


def _require_object(value: Any, keys: set[str], label: str) -> dict[str, Any]:
    if not isinstance(value, dict) or set(value) != keys:
        raise ValueError(f"{label} must contain exactly: {', '.join(sorted(keys))}")
    return value


def _read_bytes(value: Any, maximum: int, label: str) -> bytes:
    if not isinstance(value, list) or not value or len(value) > maximum:
        raise ValueError(f"{label} is empty or exceeds its byte limit")
    if any(type(byte) is not int or byte < 0 or byte > 255 for byte in value):
        raise ValueError(f"{label} must contain byte values from 0 to 255")
    return bytes(value)


def _canonical_path(value: Any) -> str:
    if not isinstance(value, str) or not value or len(value) > 256:
        raise ValueError("artifact path is empty or exceeds 256 characters")
    if not value.isascii() or value.startswith("/") or "\\" in value or ":" in value:
        raise ValueError(f"artifact path is not canonical and relative: {value!r}")
    segments = value.split("/")
    if any(
        not segment
        or segment in {".", ".."}
        or any(not (character.isalnum() or character in "._-") for character in segment)
        for segment in segments
    ):
        raise ValueError(f"artifact path is not canonical and relative: {value!r}")
    if segments[0] == "manifest.yaml":
        raise ValueError("artifact paths cannot replace or descend from manifest.yaml")
    return value


def _append_field(digest: Any, value: bytes) -> None:
    digest.update(struct.pack(">I", len(value)))
    digest.update(value)


def _content_hash(manifest: bytes, files: list[tuple[str, bytes]]) -> str:
    digest = hashlib.sha256()
    digest.update(ARTIFACT_PREFIX)
    _append_field(digest, manifest)
    for path, contents in sorted(files):
        _append_field(digest, path.encode("utf-8"))
        _append_field(digest, contents)
    return digest.hexdigest()


def _parse_artifact(export: Any) -> tuple[bytes, list[tuple[str, bytes]]]:
    if not isinstance(export, dict):
        raise ValueError("export must be a JSON object")
    artifact = _require_object(
        export.get("artifact"),
        {"manifestBytes", "files", "contentSha256"},
        "artifact",
    )
    manifest = _read_bytes(artifact["manifestBytes"], MAX_MANIFEST_BYTES, "manifest")
    entries = artifact["files"]
    if not isinstance(entries, list) or not entries or len(entries) > MAX_FILE_COUNT:
        raise ValueError("artifact file count is empty or exceeds 64")

    files: list[tuple[str, bytes]] = []
    paths: set[str] = set()
    total_bytes = len(manifest)
    for entry in entries:
        item = _require_object(entry, {"relativePath", "bytes"}, "artifact file")
        path = _canonical_path(item["relativePath"])
        if path in paths:
            raise ValueError(f"duplicate artifact path: {path}")
        paths.add(path)
        contents = _read_bytes(item["bytes"], MAX_FILE_BYTES, f"artifact file {path!r}")
        total_bytes += len(contents)
        files.append((path, contents))

    for path in paths:
        segments = path.split("/")
        if any("/".join(segments[:index]) in paths for index in range(1, len(segments))):
            raise ValueError(f"artifact file paths conflict: {path}")
    if total_bytes > MAX_ARTIFACT_BYTES:
        raise ValueError("artifact exceeds 1 MiB")

    content_hash = artifact["contentSha256"]
    envelope_hash = export.get("contentSha256")
    actual_hash = _content_hash(manifest, files)
    if (
        not isinstance(content_hash, str)
        or not SHA256_PATTERN.fullmatch(content_hash)
        or not isinstance(envelope_hash, str)
        or not SHA256_PATTERN.fullmatch(envelope_hash)
        or not hmac.compare_digest(actual_hash, content_hash)
        or not hmac.compare_digest(actual_hash, envelope_hash)
    ):
        raise ValueError("export contentSha256 does not match the artifact bytes")
    return manifest, files


def materialize(export_path: Path, output_path: Path) -> Path:
    try:
        with export_path.open("rb") as stream:
            raw_export = stream.read(MAX_EXPORT_BYTES + 1)
        if len(raw_export) > MAX_EXPORT_BYTES:
            raise ValueError("export exceeds 16 MiB")
        export = json.loads(
            raw_export.decode("utf-8"),
            object_pairs_hook=_unique_object,
        )
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ValueError(f"cannot read export: {error}") from error

    manifest, files = _parse_artifact(export)
    target = output_path if output_path.is_absolute() else ROOT / output_path
    if target.exists() or target.is_symlink():
        raise ValueError(f"output directory already exists: {target}")
    target = target.resolve()
    try:
        target.relative_to(ROOT)
    except ValueError as error:
        raise ValueError("output directory must be inside the repository") from error
    if target == ROOT:
        raise ValueError("output directory must be below the repository root")

    target.mkdir(parents=True)
    (target / "manifest.yaml").write_bytes(manifest)
    for relative_path, contents in files:
        destination = target.joinpath(*relative_path.split("/"))
        destination.parent.mkdir(parents=True, exist_ok=True)
        with destination.open("xb") as stream:
            stream.write(contents)
    return target / "manifest.yaml"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("export", type=Path, help="CLI JSON policy export")
    parser.add_argument("--output-dir", required=True, type=Path)
    args = parser.parse_args()
    try:
        manifest_path = materialize(args.export, args.output_dir)
    except (OSError, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    print(manifest_path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
