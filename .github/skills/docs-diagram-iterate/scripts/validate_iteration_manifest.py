#!/usr/bin/env python3
"""Validate iteration-manifest ordering and cross-field publication gates."""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path


def validate_manifest(manifest: dict) -> list[str]:
    errors: list[str] = []
    passes = manifest.get("passes")
    if not isinstance(passes, list) or len(passes) < 4:
        return ["passes must contain at least four entries"]

    numbers = [entry.get("number") for entry in passes]
    expected = list(range(1, len(passes) + 1))
    if numbers != expected:
        errors.append(f"pass numbers must be ordered and contiguous: expected {expected}, got {numbers}")
    if len(numbers) != len(set(numbers)):
        errors.append("pass numbers must be unique")

    for index, entry in enumerate(passes, 1):
        if entry.get("number") != index:
            continue
        expected_mode = "visual-upgrade" if index == 1 else "correction-only"
        if entry.get("mode") != expected_mode:
            errors.append(f"pass {index}: mode must be {expected_mode}")
        for field in ("drawio", "png", "change_record"):
            if not entry.get(field):
                errors.append(f"pass {index}: {field} is required")
        for field in ("png_inspected_print", "png_inspected_enlarged"):
            if entry.get(field) is not True:
                errors.append(f"pass {index}: {field} must be true")

    first = passes[0]
    baseline = first.get("baseline_meaningful_xml")
    result = first.get("result_meaningful_xml")
    ratio = first.get("growth_ratio")
    if first.get("growth_metric") != "visible-semantic-canonical-xml-v1":
        errors.append("pass 1: growth_metric must be visible-semantic-canonical-xml-v1")
    if not isinstance(baseline, int) or baseline < 1:
        errors.append("pass 1: baseline_meaningful_xml must be a positive integer")
    if not isinstance(result, int) or result < 1:
        errors.append("pass 1: result_meaningful_xml must be a positive integer")
    if isinstance(baseline, int) and baseline > 0 and isinstance(result, int):
        actual_ratio = result / baseline
        if actual_ratio < 9:
            errors.append(f"pass 1: actual growth ratio is {actual_ratio:.4f}, below 9x")
        if not isinstance(ratio, (int, float)) or not math.isclose(
            float(ratio), actual_ratio, rel_tol=1e-4, abs_tol=1e-4
        ):
            errors.append("pass 1: growth_ratio does not match measured counts")

    fourth = passes[3]
    if fourth.get("all_arrows_traced") is not True or not isinstance(fourth.get("arrow_trace"), list):
        errors.append("pass 4: all arrows must be traced and recorded")

    latest = passes[-1]
    final_pass = manifest.get("final_pass")
    if final_pass != latest.get("number"):
        errors.append("final_pass must reference the actual latest pass")
    if latest.get("mode") != "correction-only":
        errors.append("final pass must be correction-only")
    if latest.get("all_arrows_traced") is not True or not isinstance(latest.get("arrow_trace"), list):
        errors.append("final pass must trace and record every arrow")
    for field in ("orientation_defects", "overlap_defects", "arrow_defects"):
        if latest.get(field) != 0:
            errors.append(f"final pass: {field} must be zero")

    artifact_paths = []
    pitch = manifest.get("pitch", {})
    for field in ("drawio", "png", "change_record"):
        if pitch.get(field):
            artifact_paths.append(pitch[field])
    for entry in passes:
        artifact_paths.extend(
            entry[field] for field in ("drawio", "png", "change_record") if entry.get(field)
        )
    if len(artifact_paths) != len(set(artifact_paths)):
        errors.append("pitch and pass artifact paths must be unique")
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    args = parser.parse_args()
    try:
        manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        print(str(exc), file=sys.stderr)
        return 2
    errors = validate_manifest(manifest)
    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1
    print("iteration manifest is valid")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
