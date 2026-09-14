"""Extract immutable origin/dev references only into this calibration directory."""
import hashlib
import json
from pathlib import Path
import subprocess

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[4]
revision = subprocess.check_output(["git", "rev-parse", "origin/dev"], cwd=ROOT, text=True).strip()
sources = {
    "docs/diagrams/canonical-default-workflow.png": "original-react.png",
    "docs/diagrams/src/canonical-default-workflow.json": "original-graph.json",
}
for name in ("theme.ts", "nodes.tsx", "edges.tsx", "DiagramCanvas.tsx", "icons.ts", "types.ts"):
    sources[f"docs/diagram-renderer/src/{name}"] = f"react-source/{name}"
manifest = {"git_revision": revision, "files": []}
for source, relative in sources.items():
    data = subprocess.check_output(["git", "show", f"{revision}:{source}"], cwd=ROOT)
    destination = HERE / relative
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.exists():
        assert destination.read_bytes() == data, destination
    else:
        destination.write_bytes(data)
    manifest["files"].append({"git_path": source, "local_path": str(destination), "sha256": hashlib.sha256(data).hexdigest()})
(HERE / "reference-provenance.json").write_text(json.dumps(manifest, indent=2) + "\n")
print(revision)
print(HERE / "original-react.png")
