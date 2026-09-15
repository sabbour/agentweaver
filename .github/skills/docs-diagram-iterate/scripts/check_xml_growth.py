#!/usr/bin/env python3
"""Check pass-1 growth using visible, in-page draw.io semantic structures."""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
from pathlib import Path
from xml.etree import ElementTree

INVISIBLE_STYLE_RE = re.compile(
    r"(?:^|;)(?:opacity|fillOpacity|strokeOpacity|fontOpacity)=0(?:\.0+)?(?:;|$)|"
    r"(?:^|;)(?:display|visibility)=(?:none|hidden)(?:;|$)",
    re.IGNORECASE,
)
EMBEDDED_IMAGE_RE = re.compile(
    r"data:image/[^;,\"']+(?:;[^,;\"']+)*,[^;\"']+",
    re.IGNORECASE,
)
HTML_RE = re.compile(r"<[^>]+>")
APPROVED_STYLE_KEYS = {
    "align",
    "arcSize",
    "aspect",
    "curved",
    "dashed",
    "dashPattern",
    "edgeStyle",
    "endArrow",
    "endFill",
    "fillColor",
    "fontColor",
    "fontFamily",
    "fontSize",
    "fontStyle",
    "glass",
    "gradientColor",
    "html",
    "image",
    "imageAspect",
    "jettySize",
    "labelBackgroundColor",
    "labelBorderColor",
    "orthogonalLoop",
    "perimeter",
    "rounded",
    "shadow",
    "shape",
    "spacing",
    "spacingBottom",
    "spacingLeft",
    "spacingRight",
    "spacingTop",
    "startArrow",
    "startFill",
    "strokeColor",
    "strokeWidth",
    "verticalAlign",
    "whiteSpace",
}


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def parse_float(value: str | None, default: float = 0.0) -> float:
    try:
        result = float(value) if value is not None else default
    except ValueError:
        return default
    return result if math.isfinite(result) else default


def load_uncompressed_xml(path: Path) -> str:
    text = path.read_text(encoding="utf-8")
    parse_document(text, str(path))
    return text


def parse_document(text: str, source: str = "draw.io source") -> tuple[ElementTree.Element, ElementTree.Element]:
    try:
        root = ElementTree.fromstring(text)
    except ElementTree.ParseError as exc:
        raise ValueError(f"{source}: invalid XML: {exc}") from exc
    diagrams = [element for element in root.iter() if local_name(element.tag) == "diagram"]
    if len(diagrams) != 1:
        raise ValueError(f"{source}: exactly one <diagram> page is required")
    models = [child for child in list(diagrams[0]) if local_name(child.tag) == "mxGraphModel"]
    if len(models) != 1:
        raise ValueError(
            f"{source}: compressed or non-editable diagram payload; save as uncompressed XML"
        )
    model = models[0]
    width = parse_float(model.get("pageWidth"))
    height = parse_float(model.get("pageHeight"))
    if width <= 0 or height <= 0:
        raise ValueError(f"{source}: positive pageWidth and pageHeight are required")
    scale = float(model.get("pageScale", "1"))
    if not math.isfinite(scale) or scale <= 0:
        raise ValueError(f"{source}: positive finite pageScale is required")
    return root, model


def parse_style(style: str) -> dict[str, str]:
    result: dict[str, str] = {}
    for token in style.split(";"):
        if not token:
            continue
        if "=" in token:
            key, value = token.split("=", 1)
            result[key] = value
        else:
            result[token] = "1"
    return result


def normalized_label(value: str) -> str:
    text = HTML_RE.sub(" ", EMBEDDED_IMAGE_RE.sub("", value))
    return " ".join(text.split())[:512]


def approved_style(style: str) -> dict[str, str]:
    parsed = parse_style(style)
    result = {}
    for key in sorted(APPROVED_STYLE_KEYS):
        if key not in parsed:
            continue
        value = parsed[key]
        if key == "image" and value.lower().startswith("data:image/"):
            value = "<embedded-image>"
        result[key] = value[:512]
    return result


def geometry_for(cell: ElementTree.Element) -> ElementTree.Element | None:
    return next(
        (child for child in list(cell) if local_name(child.tag) == "mxGeometry"),
        None,
    )


def wrapper_labels(root: ElementTree.Element) -> dict[str, str]:
    labels: dict[str, str] = {}
    for element in root.iter():
        if local_name(element.tag) not in {"object", "UserObject"}:
            continue
        label = element.get("label") or element.get("value") or ""
        for child in list(element):
            if local_name(child.tag) == "mxCell" and child.get("id"):
                labels[child.get("id")] = label
    return labels


def absolute_bounds(
    cell_id: str,
    cells: dict[str, ElementTree.Element],
    cache: dict[str, tuple[float, float, float, float] | None],
) -> tuple[float, float, float, float] | None:
    if cell_id in cache:
        return cache[cell_id]
    cell = cells[cell_id]
    geometry = geometry_for(cell)
    if geometry is None:
        cache[cell_id] = None
        return None
    x = parse_float(geometry.get("x"))
    y = parse_float(geometry.get("y"))
    width = parse_float(geometry.get("width"))
    height = parse_float(geometry.get("height"))
    parent_id = cell.get("parent")
    parent_bounds = absolute_bounds(parent_id, cells, cache) if parent_id in cells else None
    if parent_bounds:
        px, py, pw, ph = parent_bounds
        if geometry.get("relative") == "1":
            x = px + x * pw
            y = py + y * ph
        else:
            x += px
            y += py
    offset = next(
        (
            child
            for child in list(geometry)
            if local_name(child.tag) == "mxPoint" and child.get("as") == "offset"
        ),
        None,
    )
    if offset is not None:
        x += parse_float(offset.get("x"))
        y += parse_float(offset.get("y"))
    result = (x, y, width, height)
    cache[cell_id] = result
    return result


def edge_points(cell: ElementTree.Element) -> list[tuple[float, float]]:
    geometry = geometry_for(cell)
    if geometry is None:
        return []
    points = []
    for element in geometry.iter():
        if local_name(element.tag) != "mxPoint":
            continue
        points.append((parse_float(element.get("x")), parse_float(element.get("y"))))
    return points


def metadata_padding(root: ElementTree.Element) -> list[str]:
    offenders: list[str] = []
    for element in root.iter():
        element_id = element.get("id", local_name(element.tag))
        for key, value in element.attrib.items():
            compact = re.sub(r"\s+", "", value)
            suspicious_name = any(token in key.lower() for token in ("padding", "filler", "junk"))
            approved_large = key in {"style", "value", "label", "tooltip", "link", "image"}
            if (suspicious_name and len(compact) > 32) or (not approved_large and len(compact) > 1024):
                offenders.append(f"{element_id}:{key}")
    return offenders


def analyze(text: str, source: str = "draw.io source") -> dict:
    root, model = parse_document(text, source)
    page_scale = float(model.get("pageScale", "1"))
    page_width = parse_float(model.get("pageWidth")) * page_scale
    page_height = parse_float(model.get("pageHeight")) * page_scale
    cells = {
        cell.get("id"): cell
        for cell in root.iter()
        if local_name(cell.tag) == "mxCell" and cell.get("id")
    }
    labels = wrapper_labels(root)
    bounds_cache: dict[str, tuple[float, float, float, float] | None] = {}
    invisible: list[str] = []
    off_page: list[str] = []
    canonical: list[dict] = []
    signatures: dict[str, str] = {}
    duplicates: list[str] = []

    for cell_id, cell in cells.items():
        kind = "vertex" if cell.get("vertex") == "1" else "edge" if cell.get("edge") == "1" else None
        if kind is None:
            continue
        style_text = cell.get("style", "")
        if cell.get("visible") == "0" or INVISIBLE_STYLE_RE.search(style_text):
            invisible.append(cell_id)
            continue
        bounds = absolute_bounds(cell_id, cells, bounds_cache) if kind == "vertex" else None
        points = edge_points(cell) if kind == "edge" else []
        if bounds:
            x, y, width, height = bounds
            if width <= 0 or height <= 0:
                invisible.append(cell_id)
                continue
            if x < 0 or y < 0 or x + width > page_width or y + height > page_height:
                off_page.append(cell_id)
        if any(x < 0 or y < 0 or x > page_width or y > page_height for x, y in points):
            off_page.append(cell_id)

        structure = {
            "kind": kind,
            "label": normalized_label(cell.get("value") or labels.get(cell_id, "")),
            "style": approved_style(style_text),
            "bounds": [round(value, 3) for value in bounds] if bounds else None,
            "source": cell.get("source"),
            "target": cell.get("target"),
            "points": [[round(x, 3), round(y, 3)] for x, y in points],
        }
        signature = json.dumps(structure, sort_keys=True, separators=(",", ":"))
        if signature in signatures:
            duplicates.append(f"{cell_id} duplicates {signatures[signature]}")
        else:
            signatures[signature] = cell_id
        canonical.append(structure)

    count = sum(
        len(json.dumps(item, sort_keys=True, separators=(",", ":")))
        for item in canonical
    )
    return {
        "metric": "visible-semantic-canonical-xml-v1",
        "page_width": page_width,
        "page_height": page_height,
        "visible_structures": len(canonical),
        "meaningful_count": count,
        "invisible_cells": sorted(set(invisible)),
        "off_page_cells": sorted(set(off_page)),
        "duplicate_cells": sorted(set(duplicates)),
        "metadata_padding": sorted(set(metadata_padding(root))),
    }


def invisible_cells(text: str) -> list[str]:
    return analyze(text)["invisible_cells"]


def meaningful_count(text: str) -> int:
    return analyze(text)["meaningful_count"]


def assess(pitch_text: str, pass_text: str, minimum_ratio: float = 9.0) -> dict:
    pitch = analyze(pitch_text, "pitch")
    result = analyze(pass_text, "pass 1")
    baseline_count = pitch["meaningful_count"]
    result_count = result["meaningful_count"]
    ratio = result_count / baseline_count if baseline_count else 0.0
    defects = {
        key: result[key]
        for key in ("invisible_cells", "off_page_cells", "duplicate_cells", "metadata_padding")
    }
    return {
        "growth_metric": result["metric"],
        "baseline_meaningful_xml": baseline_count,
        "result_meaningful_xml": result_count,
        "baseline_visible_structures": pitch["visible_structures"],
        "result_visible_structures": result["visible_structures"],
        "growth_ratio": round(ratio, 6),
        "minimum_ratio": minimum_ratio,
        **defects,
        "passed": baseline_count > 0 and ratio >= minimum_ratio and not any(defects.values()),
    }


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Require pass-1 draw.io XML to contain at least 9x visible semantic structure."
    )
    parser.add_argument("pitch", type=Path)
    parser.add_argument("pass_one", type=Path)
    parser.add_argument("--minimum-ratio", type=float, default=9.0)
    parser.add_argument("--json", action="store_true", dest="as_json")
    args = parser.parse_args()

    try:
        pitch_xml = load_uncompressed_xml(args.pitch)
        pass_xml = load_uncompressed_xml(args.pass_one)
        report = assess(pitch_xml, pass_xml, args.minimum_ratio)
    except (OSError, ValueError) as exc:
        print(str(exc), file=sys.stderr)
        return 2
    report.update({"pitch": str(args.pitch), "pass_one": str(args.pass_one)})

    if args.as_json:
        print(json.dumps(report, indent=2))
    else:
        print(
            f"visible semantic XML: {report['baseline_meaningful_xml']} -> "
            f"{report['result_meaningful_xml']} "
            f"({report['growth_ratio']:.3f}x; required {args.minimum_ratio:.3f}x)"
        )
        for field in ("invisible_cells", "off_page_cells", "duplicate_cells", "metadata_padding"):
            if report[field]:
                print(f"{field}: {', '.join(report[field])}")
        print("PASS" if report["passed"] else "FAIL")
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
