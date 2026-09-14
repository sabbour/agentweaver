"""Inventory existing owned assets without changing any diagram or public file."""
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

here = Path(__file__).resolve().parent
repo = here.parents[3]
plan = json.loads((repo / ".github/skills/docs-diagram-audit/reports/plan-deep-dive-core.json").read_text())
validation = json.loads((here / "shard-validation.json").read_text())
allowed = set(plan["exclusive_asset_paths"])

def snapshot(relative, kind):
    if relative not in allowed:
        raise ValueError(f"Not an owned asset: {relative}")
    file = repo / relative
    exists = file.is_file()
    return {
        "kind": kind,
        "absolute_path": str(file),
        "exists_at_freeze_snapshot": exists,
        "sha256": hashlib.sha256(file.read_bytes()).hexdigest() if exists else None,
    }

promoted = []
legacy_sources = []
for item in validation["diagrams"]:
    name = item["diagram"]
    files = [
        snapshot(f"docs/diagrams/src/{name}.drawio", "editable_source"),
        snapshot(f"docs/diagrams/drawio/generated/{name}.drawio", "generated_drawio"),
        snapshot(f"docs/diagrams/{name}.png", "public_png"),
        snapshot(f"docs/diagrams/{name}.hash.txt", "provenance_hash"),
    ]
    if not all(file["exists_at_freeze_snapshot"] for file in files):
        raise ValueError(f"Previously promoted asset missing: {name}")
    promoted.append({"diagram": name, "approval": "withheld-after-user-visual-rejection", "files": files})
    legacy_sources.append(snapshot(f"docs/diagrams/src/{name}.json", "previously_retired_json_source"))

retired = []
for name in validation["retired"]:
    retired.append({
        "diagram": name,
        "previously_retired_paths": [
            snapshot(f"docs/diagrams/src/{name}.json", "legacy_json_source"),
            snapshot(f"docs/diagrams/drawio/generated/{name}.drawio", "generated_drawio"),
            snapshot(f"docs/diagrams/{name}.png", "public_png"),
            snapshot(f"docs/diagrams/{name}.hash.txt", "provenance_hash"),
        ],
        "preserved_legacy_copies": [
            str(file) for file in sorted((repo / f"docs/diagrams/reviews/{name}").glob("legacy-*"))
            if file.is_file()
        ],
    })

report = {
    "area": plan["area"],
    "status": "paused-awaiting-approved-shared-visual-calibration",
    "user_rejection_timestamp": plan["execution_report"]["visual_rejection"]["user_message_timestamp"],
    "snapshot_recorded_at": datetime.now(timezone.utc).isoformat(),
    "public_files_written_by_this_inventory": 0,
    "authoring_or_renderer_process_started": False,
    "active_work_at_pause": "No running child agents or shell commands observed. Prior author agents are idle synchronous tasks and cannot receive follow-up messages; no replacement tasks launched.",
    "freeze_and_feedback": plan["execution_report"]["visual_rejection"],
    "already_promoted_diagram_count": len(promoted),
    "already_promoted_file_count": sum(len(item["files"]) for item in promoted),
    "already_promoted": promoted,
    "previously_retired_json_sources": legacy_sources,
    "previously_retired_redundant_diagrams": retired,
    "preserved_research": [
        str(here / name) for name in ("research-boundaries.md", "research-flows.md", "research-assurance.md")
    ],
    "preserved_owned_documents": [str(repo / relative) for relative in plan["document_paths"]],
    "note": "These files were written before the rejection; no commit or deployment was performed. Existing files are preserved, not rolled back or replaced. This snapshot is scoped to this shard, not other sessions.",
}
target = here / "visual-rejection-assets.json"
target.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
print(f"PAUSED: {len(promoted)} previously promoted diagrams, {report['already_promoted_file_count']} exact file paths.")
print(f"Report: {target}")
