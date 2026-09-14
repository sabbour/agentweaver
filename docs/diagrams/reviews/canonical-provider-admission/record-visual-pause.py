"""Record the user's stop and exact promoted paths; never write diagram assets."""
import hashlib
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
plan_path = ROOT / ".github/skills/docs-diagram-audit/reports/plan-shared.json"
plan = json.loads(plan_path.read_text(encoding="utf-8"))
names = plan["execution"]["canonical_files_migrated"]
assert len(names) == 27
files = []
for name in names:
    for relative in (
        f"docs/diagrams/src/{name}.drawio",
        f"docs/diagrams/{name}.png",
        f"docs/diagrams/{name}.hash.txt",
    ):
        asset = ROOT / relative
        assert asset.is_file(), asset
        files.append({
            "diagram": name, "path": str(asset.resolve()),
            "sha256": hashlib.sha256(asset.read_bytes()).hexdigest(),
        })
    if name.startswith("email-"):
        asset = ROOT / f"docs/diagrams/email-exports/{name}.png"
        assert asset.is_file(), asset
        files.append({
            "diagram": name, "path": str(asset.resolve()),
            "sha256": hashlib.sha256(asset.read_bytes()).hexdigest(),
        })
assert len(files) == 84
pause = {
    "timestamp": "2026-09-13T14:33:26.568-07:00",
    "status": "paused-user-visual-rejection",
    "approval_required": "Explicit user approval of revised shared visual calibration/template before any resumed authoring, export or promotion.",
    "feedback": [
        "Oversized title/type",
        "Excessive prose on canvas",
        "Weak information density",
        "Flat/oversized group boxes",
        "Insufficient hierarchy and whitespace discipline",
        "Generic draw.io feel",
        "Loss of the React renderer's compact polished card rhythm",
    ],
    "research_and_audit": "preserved",
    "public_assets": "frozen in place; no rollback or further overwrite",
    "canonical_families_already_promoted": 27,
    "canonical_files_already_promoted": 81,
    "derived_email_pngs_already_promoted": 3,
    "files": files,
    "execution_state": "All listed child agents idle, synchronous; no running shell sessions found. No new agents launched.",
}
(HERE / "visual-pause.json").write_text(json.dumps(pause, indent=2) + "\n", encoding="utf-8")
lines = [
    "# PAUSED: user visual rejection",
    "",
    "Authoring, rendering and promotion are stopped pending explicit approval of a",
    "revised shared calibration/template. Public assets remain frozen in place.",
    "Mechanical validation and earlier completion labels do not constitute visual acceptance.",
    "Research, audits, historical passes and existing files are preserved; no rollback was performed.",
    "",
    "## Correction recorded",
    "",
    "**Logged:** 2026-09-13T14:33:26.568-07:00",
    "**Category:** correction | **Priority:** high | **Status:** pending | **Area:** docs",
    "",
    "The user rejected oversized type, prose-heavy canvases, weak density, oversized flat",
    "groups, poor hierarchy/spacing and generic draw.io styling. The required reference is",
    "the compact polished card rhythm of the former React renderer. Native symbols, XML",
    "growth and passing validators are not proxies for achieving that visual quality.",
    "Do not mass-author or promote again before the shared calibration is explicitly approved.",
    "",
    "## Already-promoted assets: exact absolute paths",
    "",
    "**27 canonical families / 81 files, plus 3 derived email PNG copies = 84 files.**",
    "SHA-256 values at the pause are recorded alongside every path in `visual-pause.json`.",
    "",
]
for name in names:
    lines += [f"### {name}", ""]
    lines += [f"- `{f['path']}`" for f in files if f["diagram"] == name]
    lines.append("")
lines += [
    "## Operational note",
    "",
    "All 11 listed child agents were idle. A stop notification could not be delivered",
    "because those tasks were synchronous, not background sessions; none was restarted.",
    "No running shell sessions were found. A read-only status-filter probe initially",
    "used unsupported regex lookahead and was replaced by a supported explicit status filter.",
    "Only shared-owned pause/status reports were written; no render/build was run.",
    "",
]
(HERE / "visual-pause.md").write_text("\n".join(lines), encoding="utf-8")
execution = plan["execution"]
execution["status"] = pause["status"]
execution["visual_pause"] = {k: v for k, v in pause.items() if k != "files"}
execution["visual_pause"]["exact_paths_report"] = str((HERE / "visual-pause.json").resolve())
execution["publication_ready"] = False
execution["owned_canonical_artifacts_complete"] = False
execution["visual_acceptance"] = "rejected; awaiting approved shared calibration"
plan["status"] = "blocked"
plan_path.write_text(json.dumps(plan, indent=2) + "\n", encoding="utf-8")
for filename in ("shared-validation.json", "shared-concept-status.json"):
    path = HERE / filename
    data = json.loads(path.read_text(encoding="utf-8"))
    data["status"] = pause["status"]
    data["visual_acceptance"] = execution["visual_acceptance"]
    data["visual_pause_report"] = "visual-pause.json"
    path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
for item in files:
    assert hashlib.sha256(Path(item["path"]).read_bytes()).hexdigest() == item["sha256"], item["path"]
print(f"PAUSED. Recorded {len(names)} promoted families and {len(files)} exact paths; public hashes unchanged.")
print(HERE / "visual-pause.md")
