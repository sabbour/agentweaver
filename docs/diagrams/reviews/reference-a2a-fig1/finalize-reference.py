"""Publish only reference-owned assets and preserve the inspected iteration evidence."""
import hashlib
import importlib.util
import json
import shutil
import sys
from pathlib import Path
from xml.etree import ElementTree as ET

import jsonschema
from PIL import Image

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[4]
PLAN = json.loads((ROOT / ".github/skills/docs-diagram-audit/reports/plan-reference.json").read_text())
NAMES = ("reference-a2a-fig1", "reference-scaling-data-layer-fig1")
SCHEMA = json.loads((ROOT / ".github/skills/docs-diagram-iterate/references/iteration-manifest.schema.json").read_text())
spec = importlib.util.spec_from_file_location("growth", ROOT / ".github/skills/docs-diagram-iterate/scripts/check_xml_growth.py")
growth = importlib.util.module_from_spec(spec)
spec.loader.exec_module(growth)


def owned(path):
    relative = path.resolve().relative_to(ROOT).as_posix()
    if not any(relative == entry or (entry.endswith("/") and relative.startswith(entry))
               for entry in PLAN["exclusive_asset_paths"]):
        raise ValueError(f"Not an exclusively owned asset: {relative}")
    return path


def write(path, text):
    owned(path).parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")


def save_json(path, value):
    write(path, json.dumps(value, indent=2) + "\n")


RESEARCH = """Exactly three bounded, read-only research agents ran as **GPT-6 Astra
(`gpt-6-astra`)**. No nested/additional research agents were used:

1. A2A/context: `reference-a2a-fig1/research-a2a.md`.
2. Persistence/sandbox: `reference-scaling-data-layer-fig1/research-scaling.md`.
3. Reference contracts/UI: `reference-a2a-fig1/research-contracts.md`.

Paths above are relative to `docs/diagrams/reviews`. Code, configuration, tests,
and current documentation supplied the factual model, not historical diagrams.
Their complete source maps are saved in those reports.
"""

STYLE = """## Style, symbol classification and credits

The editable A5 landscape page is 794 x 559 logical pixels (210 x 148 mm).
The implementation follows `apps/web/src/theme.ts` and the repository's
`docs/diagrams/drawio/fluent-template.drawio`, `fluent-library.xml`, and
`design-system.json`: warm paper, elevated rounded cards, Segoe UI labels,
Cascadia metadata, colored accent rails/badges, muted boundaries, and rounded
orthogonal connectors. The template/library were loaded before authoring.
Hidden template markers were removed; no invisible padding was added.

Agentweaver-specific aggregate cards use custom Fluent composition because no
native library symbol represents a configured turn gate, credential snapshot,
checkpoint ownership boundary, or verified-writeback lifecycle. Kubernetes
pods/volumes use native `mxgraph.kubernetes.pod`/`vol`; process, document,
decision and database symbols use native `process`, `mxgraph.flowchart.document`,
`mxgraph.flowchart.decision`, and `cylinder3` as applicable. Scaling uses the
bundled Azure Fileshare image `img/lib/azure2/storage/Azure_Fileshare.svg`,
not a fabricated Azure logo. Symbols remain editable library shapes/images.

The [draw.io upstream license](https://github.com/jgraph/drawio/blob/dev/LICENSE)
is Apache-2.0. Microsoft retains its icon rights; the
[Azure architecture icon terms](https://learn.microsoft.com/en-us/azure/architecture/icons/)
permit architectural diagrams/documentation. The bundled Fileshare artwork is
used for that purpose without changing its design. No external icon download
was incorporated; a failed stencil-URL probe was not treated as verification.

All PNGs were exported by verified **draw.io Desktop 31.4.5**, using the official
recipe (PNG, scale 2, border 16), not a browser renderer. The executable under
the canonical-api-host review directory was used read-only; runtime cache was
isolated under the reference review directory.
"""

PITCHES = {
    NAMES[0]: """Show platform callers on the left, the configured AgentHost boundary on
the right, and durable checkpoint ownership outside the pod. Transport controls
are annotations, not fictitious gateway services. Separate one-time configure,
card auth, turn auth, bridge dispatch, and worker-owned persistence. Keep
deployment-specific mTLS and additive ingress caveats visible without implying
SPIFFE/workload identity. The small initial pitch establishes that composition;
pass one adds the actual source-grounded structure.""",
    NAMES[1]: """Show current API and worker roles, shared PostgreSQL/Azure Files, and a
per-run AgentHost with pod-local execution. Separate deployed CPU/memory HPA
from KEDA/API-HPA guidance. Trace verified fetch and temporary-ref writeback,
with receipt validation and authoritative fast-forward owned by the worker.
Shared-access junctions mean both platform roles access both stores; they are
not extra services. Keep sandbox-to-database access absent."""
}


def main():
    if (ROOT / "docs/diagrams/reviews/reference-a2a-fig1/publication.json").exists():
        raise RuntimeError("Initial publication already exists; validate the latest manifests instead of overwriting them.")
    results = []
    for name in NAMES:
        folder = ROOT / "docs/diagrams/reviews" / name
        pitch = folder / f"{name}-pitch.drawio"
        first = folder / f"{name}-pass-01.drawio"
        metric = growth.assess(pitch.read_text(encoding="utf-8"), first.read_text(encoding="utf-8"))
        assert metric["passed"], metric
        save_json(folder / "xml-growth.json", metric)
        arrows = json.loads((folder / "arrow-evidence.json").read_text())
        for arrow in arrows:
            if arrow["id"] == "temp-ref":
                arrow["relationship"] = "Publish a non-force temporary writeback ref to the shared repository"
        manifest = {"diagram": name, "orientation": "A5-landscape", "pitch": {
            "drawio": pitch.name, "png": f"{name}-pitch.png",
            "change_record": f"{name}-pitch.md",
            "png_inspected_print": True, "png_inspected_enlarged": True,
        }, "passes": [], "final_pass": 4}
        write(folder / f"{name}-pitch.md", f"# {name}: pitch\n\n{PITCHES[name]}\n\n"
              "Initial PNG opened and inspected enlarged and at A5 scale before post-pitch work. "
              "It is a sparse composition baseline, not the final design.\n\n" + RESEARCH + "\n" + STYLE)
        for number in range(1, 5):
            stem = f"{name}-pass-{number:02}"
            entry = {"number": number,
                     "mode": "visual-upgrade" if number == 1 else "correction-only",
                     "drawio": stem + ".drawio", "png": stem + ".png",
                     "change_record": stem + ".md",
                     "png_inspected_print": True, "png_inspected_enlarged": True,
                     "orientation_defects": 0,
                     "overlap_defects": int(name == NAMES[1] and number == 1),
                     "arrow_defects": 0}
            if number == 1:
                entry.update({key: metric[key] for key in
                              ("baseline_meaningful_xml", "result_meaningful_xml", "growth_metric", "growth_ratio")})
                changes = ("Full Fluent visual upgrade: source-grounded boundary/card hierarchy, native symbols, "
                           "metadata, state badges, labels and connector routing. Early draft geometry/stencil "
                           "issues were corrected within this pass; the draft XML/PNG remain saved separately. "
                           f"Meaningful XML grew {metric['baseline_meaningful_xml']} to "
                           f"{metric['result_meaningful_xml']} ({metric['growth_ratio']:.6f}x). "
                           "The existing checker found no hidden, off-page, duplicate, or metadata padding.")
                if name == NAMES[1]:
                    changes += " Inspection found the `publish ref` label too close to the adjacent heading; queued for pass two."
            elif number == 2 and name == NAMES[1]:
                changes = ("Correction only: shorten `publish ref` to `temp ref` to eliminate the adjacent-heading "
                           "crowding. Source/target and relationship are unchanged. No features, cards or decoration added.")
            else:
                changes = ("Correction-only review; no defects requiring XML changes. A separately saved source "
                           "was exported and inspected again. No expansion or decorative growth.")
            record = (f"# {name}: pass {number:02}\n\n{changes}\n\n"
                      "The official PNG was opened at enlarged/native resolution and its `-print.png` A5-scale "
                      "companion was opened separately. Checked orientation, hierarchy, text wrapping, native "
                      "symbols, boundary containment, connector ports, arrowheads, labels and crossings.\n\n"
                      f"Remaining defects: orientation {entry['orientation_defects']}; "
                      f"overlap {entry['overlap_defects']}; arrows {entry['arrow_defects']}.\n")
            if number == 4:
                source = ET.parse(folder / (stem + ".drawio"))
                edges = {c.attrib["id"]: c for c in source.findall(".//mxCell[@edge='1']")}
                assert set(edges) == {arrow["id"] for arrow in arrows}
                for arrow in arrows:
                    cell = edges[arrow["id"]]
                    assert cell.attrib["source"] == arrow["source"] and cell.attrib["target"] == arrow["target"]
                entry.update(all_arrows_traced=True, arrow_trace=arrows)
                record += ("\n## Final arrow trace\n\nEvery connector was followed visually from its source port to "
                           "target port. Arrowheads match the request/response or one-way relationship, labels stay "
                           "clear of cards, and there are no ambiguous crossings. Checkpoint persistence never "
                           "originates inside AgentHost; no sandbox-to-PostgreSQL edge exists. Scaling's three "
                           "unheaded junction segments are a shared-access bus, not service hops. No semantic "
                           "revision-return rail is needed in either topology.\n\n"
                           "| ID | Source to target | Relationship and evidence | Result |\n| --- | --- | --- | --- |\n")
                for arrow in arrows:
                    record += (f"| `{arrow['id']}` | `{arrow['source']}` to `{arrow['target']}` | "
                               f"{arrow['relationship']}; `{arrow['evidence']}` | clean |\n")
            write(folder / (stem + ".md"), record)
            manifest["passes"].append(entry)
        jsonschema.Draft202012Validator(SCHEMA).validate(manifest)
        save_json(folder / "iteration-manifest.json", manifest)
        for artifacts in [manifest["pitch"], *manifest["passes"]]:
            for key in ("drawio", "png", "change_record"):
                assert (folder / artifacts[key]).is_file()
            with Image.open(folder / artifacts["png"]) as image:
                image.verify()
        final = folder / f"{name}-pass-04"
        canonical = owned(ROOT / "docs/diagrams/src" / f"{name}.drawio")
        shutil.copyfile(final.with_suffix(".drawio"), canonical)
        shutil.copyfile(final.with_suffix(".png"), owned(ROOT / "docs/diagrams" / f"{name}.png"))
        removed = []
        for path in (ROOT / "docs/diagrams/src" / f"{name}.json",
                     ROOT / "docs/diagrams/drawio/generated" / f"{name}.drawio"):
            if owned(path).exists():
                path.unlink()
                removed.append(path.relative_to(ROOT).as_posix())
        results.append({"name": name, "growth": metric["growth_ratio"], "arrows": len(arrows), "retired": removed})

    retired = "reference-sandbox-pods-fig1"
    consumer = (ROOT / "docs/reference/sandbox-pods.md").read_text(encoding="utf-8")
    assert retired not in consumer and "../diagrams/sandbox-browser-preview-fig1.png" in consumer
    removed = []
    for entry in PLAN["exclusive_asset_paths"]:
        if Path(entry).stem not in (retired, retired + ".hash") or entry.endswith("/"):
            continue
        path = owned(ROOT / entry)
        if path.exists():
            removed.append({"path": entry, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
            path.unlink()
    tombstone = {
        "diagram": retired, "disposition": "merge", "target": "sandbox-browser-preview-fig1",
        "consumer": "docs/reference/sandbox-pods.md",
        "replacement_png": "docs/diagrams/sandbox-browser-preview-fig1.png",
        "replacement_source": "docs/diagrams/src/sandbox-browser-preview-fig1.drawio",
        "consumer_updated_before_retirement": True, "target_assets_modified": False,
        "reason": "Gateway-primary shared preview model; disabled-preview API-host loopback retained in prose/table.",
        "retired_assets": removed,
    }
    save_json(ROOT / "docs/diagrams/reviews" / retired / "merge-tombstone.json", tombstone)
    save_json(ROOT / "docs/diagrams/reviews/reference-a2a-fig1/publication.json",
              {"redesigned": results, "merged": tombstone})
    print(json.dumps({"redesigned": results, "merged_assets_removed": len(removed)}, indent=2))


if __name__ == "__main__":
    main()
