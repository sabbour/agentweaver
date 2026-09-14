import importlib.util
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).parents[1] / "scripts" / "diagram_audit.py"
SPEC = importlib.util.spec_from_file_location("diagram_audit", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader
SPEC.loader.exec_module(MODULE)


class DiagramAuditTests(unittest.TestCase):
    def test_counts_readme_and_export_provenance_but_not_historical_reviews(self):
        with tempfile.TemporaryDirectory() as directory:
            repo = Path(directory)
            diagrams = repo / "docs/diagrams"
            (diagrams / "src").mkdir(parents=True)
            (diagrams / "reviews/old").mkdir(parents=True)
            (diagrams / "email-exports").mkdir()
            for name in ("architecture", "email"):
                (diagrams / f"src/{name}.drawio").write_text("<mxfile/>", encoding="utf-8")
                (diagrams / f"{name}.png").write_bytes(b"png")
                (diagrams / f"{name}.hash.txt").write_text("hash", encoding="utf-8")
            (repo / "README.md").write_text("![Architecture](docs/diagrams/architecture.png)", encoding="utf-8")
            (diagrams / "email-exports/README.md").write_text("[Canonical](../email.png)", encoding="utf-8")
            (diagrams / "reviews/old/history.md").write_text("![Retired](../../diagrams/retired.png)", encoding="utf-8")
            report = MODULE.discover(repo, repo / "audit.json")
            self.assertEqual(["architecture", "email"], [d["name"] for d in report["diagrams"]])
            self.assertFalse(any(report["integrity"].values()))
            self.assertEqual("README.md", report["diagrams"][0]["references"][0]["path"])

    def test_discovers_complete_triplet_and_reference(self):
        with tempfile.TemporaryDirectory() as directory:
            repo = Path(directory)
            (repo / "docs/diagrams/src").mkdir(parents=True)
            (repo / "docs/guide").mkdir()
            (repo / "docs/diagrams/src/architecture.drawio").write_text("<mxfile/>", encoding="utf-8")
            (repo / "docs/diagrams/architecture.png").write_bytes(b"png")
            (repo / "docs/diagrams/architecture.hash.txt").write_text("hash", encoding="utf-8")
            (repo / "docs/guide/index.md").write_text(
                "![Architecture](../diagrams/architecture.png)\n"
                '<img src="/diagrams/architecture.png" alt="Architecture">',
                encoding="utf-8",
            )

            report = MODULE.discover(repo, repo / "audit.json")

            self.assertEqual([], report["integrity"]["orphan_sources"])
            self.assertEqual([], report["integrity"]["orphan_images"])
            self.assertEqual([], report["integrity"]["orphan_hashes"])
            self.assertEqual([], report["integrity"]["broken_references"])
            self.assertEqual("assessment-only", report["ground_truth"]["legacy_diagrams_role"])
            self.assertEqual(2, len(report["diagrams"][0]["references"]))

    def test_reports_orphans_and_broken_links(self):
        with tempfile.TemporaryDirectory() as directory:
            repo = Path(directory)
            (repo / "docs/diagrams/src").mkdir(parents=True)
            (repo / "docs/guide").mkdir()
            (repo / "docs/diagrams/src/source-only.drawio").write_text("<mxfile/>", encoding="utf-8")
            (repo / "docs/diagrams/image-only.png").write_bytes(b"png")
            (repo / "docs/diagrams/hash-only.hash.txt").write_text("hash", encoding="utf-8")
            (repo / "docs/guide/index.md").write_text(
                "![Missing](../diagrams/missing.png)",
                encoding="utf-8",
            )

            integrity = MODULE.discover(repo, repo / "audit.json")["integrity"]

            self.assertIn("source-only", integrity["orphan_sources"])
            self.assertIn("image-only", integrity["orphan_images"])
            self.assertIn("hash-only", integrity["orphan_hashes"])
            self.assertEqual(1, len(integrity["broken_references"]))

    def test_final_validation_requires_both_downstream_skills(self):
        report = {
            "version": 1,
            "scope": "docs-only",
            "ground_truth": {
                "repository_sources": ["src/example.ts:1"],
                "current_docs": ["docs/guide/example.md:1"],
                "legacy_diagrams_role": "assessment-only",
            },
            "concepts": [
                {
                    "id": "architecture",
                    "title": "Architecture",
                    "disposition": "retain",
                    "target": None,
                    "canonical_diagram": "architecture",
                    "member_diagrams": ["architecture"],
                    "owner_area": "guide",
                    "status": "done",
                    "rationale": "Current concept remains necessary",
                    "evidence": ["src/example.ts:1"],
                    "pitch_skill_completed": False,
                    "iterate_skill_completed": False,
                    "tombstone": False,
                }
            ],
            "diagrams": [
                {
                    "name": "architecture",
                    "concept_id": "architecture",
                    "source_paths": ["docs/diagrams/src/architecture.drawio"],
                    "image_path": "docs/diagrams/architecture.png",
                    "hash_path": "docs/diagrams/architecture.hash.txt",
                    "references": [],
                    "owner_area": "guide",
                    "disposition": "retain",
                    "target": None,
                    "status": "done",
                    "stable_output_path": True,
                    "path_change_reason": None,
                    "legacy_assessment": "Accurate but visually dated",
                    "tombstone": False,
                }
            ],
            "integrity": {
                "orphan_sources": [],
                "orphan_images": [],
                "orphan_hashes": [],
                "broken_references": [],
                "ambiguous_sources": [],
            },
        }

        errors = MODULE.validate(report, final=True)

        self.assertTrue(any("docs-diagram-pitch not completed" in error for error in errors))
        self.assertTrue(any("docs-diagram-iterate not completed" in error for error in errors))

    def test_summary_includes_dispositions_and_integrity(self):
        report = {
            "concepts": [
                {
                    "id": "architecture",
                    "disposition": "redesign",
                    "canonical_diagram": "architecture",
                    "target": None,
                    "owner_area": "guide",
                    "status": "done",
                    "rationale": "",
                }
            ],
            "diagrams": [{"disposition": "redesign"}],
            "integrity": {
                "orphan_sources": [],
                "orphan_images": [],
                "orphan_hashes": [],
                "broken_references": [],
                "ambiguous_sources": [],
            },
        }

        text = MODULE.summary(report)

        self.assertIn("Concept dispositions", text)
        self.assertIn("redesign: 1", text)
        self.assertIn("Area ownership", text)
        self.assertIn("Intentional path changes", text)
        self.assertIn("orphan_sources: 0", text)

    def test_refresh_preserves_removed_and_merged_tombstones(self):
        with tempfile.TemporaryDirectory() as directory:
            repo = Path(directory)
            (repo / "docs/diagrams/src").mkdir(parents=True)
            report_path = repo / "audit.json"
            report_path.write_text(
                """{
  "version": 1,
  "scope": "docs-only",
  "ground_truth": {
    "repository_sources": ["src/current.ts:1"],
    "current_docs": ["docs/guide/current.md:1"],
    "legacy_diagrams_role": "assessment-only"
  },
  "concepts": [
    {
      "id": "canonical",
      "title": "Canonical",
      "disposition": "retain",
      "target": null,
      "canonical_diagram": "canonical",
      "member_diagrams": ["canonical"],
      "owner_area": "shared",
      "status": "done",
      "rationale": "Survives",
      "evidence": ["src/current.ts:1"],
      "pitch_skill_completed": true,
      "iterate_skill_completed": true,
      "tombstone": false
    },
    {
      "id": "legacy",
      "title": "Legacy",
      "disposition": "merge",
      "target": "canonical",
      "canonical_diagram": null,
      "member_diagrams": ["legacy"],
      "owner_area": "shared",
      "status": "done",
      "rationale": "Consolidated",
      "evidence": ["docs/guide/current.md:1"],
      "pitch_skill_completed": false,
      "iterate_skill_completed": false,
      "tombstone": true
    }
  ],
  "diagrams": [
    {
      "name": "canonical",
      "concept_id": "canonical",
      "source_paths": ["docs/diagrams/src/canonical.drawio"],
      "image_path": "docs/diagrams/canonical.png",
      "hash_path": "docs/diagrams/canonical.hash.txt",
      "references": [],
      "owner_area": "shared",
      "disposition": "retain",
      "target": null,
      "status": "done",
      "stable_output_path": true,
      "path_change_reason": null,
      "legacy_assessment": "Current",
      "tombstone": false
    },
    {
      "name": "legacy",
      "concept_id": "legacy",
      "source_paths": ["docs/diagrams/src/legacy.drawio"],
      "image_path": "docs/diagrams/legacy.png",
      "hash_path": "docs/diagrams/legacy.hash.txt",
      "references": [],
      "owner_area": "shared",
      "disposition": "merge",
      "target": "canonical",
      "status": "done",
      "stable_output_path": false,
      "path_change_reason": "Merged into canonical",
      "legacy_assessment": "Duplicate",
      "tombstone": true
    }
  ],
  "integrity": {
    "orphan_sources": [],
    "orphan_images": [],
    "orphan_hashes": [],
    "broken_references": [],
    "ambiguous_sources": []
  }
}""",
                encoding="utf-8",
            )
            (repo / "docs/diagrams/src/canonical.drawio").write_text("<mxfile/>", encoding="utf-8")
            (repo / "docs/diagrams/canonical.png").write_bytes(b"png")
            (repo / "docs/diagrams/canonical.hash.txt").write_text("hash", encoding="utf-8")

            refreshed = MODULE.discover(repo, report_path)
            legacy_diagram = next(item for item in refreshed["diagrams"] if item["name"] == "legacy")
            legacy_concept = next(item for item in refreshed["concepts"] if item["id"] == "legacy")

            self.assertTrue(legacy_diagram["tombstone"])
            self.assertEqual("merge", legacy_diagram["disposition"])
            self.assertEqual("canonical", legacy_diagram["target"])
            self.assertEqual("Merged into canonical", legacy_diagram["path_change_reason"])
            self.assertEqual("Consolidated", legacy_concept["rationale"])

    def test_validation_rejects_invalid_historical_target_and_path_change(self):
        report = {
            "version": 1,
            "scope": "docs-only",
            "ground_truth": {"legacy_diagrams_role": "assessment-only"},
            "concepts": [
                {
                    "id": "legacy",
                    "disposition": "merge",
                    "target": "missing",
                    "canonical_diagram": None,
                    "status": "done",
                    "tombstone": True,
                }
            ],
            "diagrams": [
                {
                    "name": "legacy",
                    "concept_id": "legacy",
                    "disposition": "merge",
                    "target": "missing",
                    "status": "done",
                    "stable_output_path": False,
                    "path_change_reason": None,
                    "tombstone": True,
                }
            ],
        }

        errors = MODULE.validate(report)

        self.assertTrue(any("existing diagram target" in error for error in errors))
        self.assertTrue(any("existing concept target" in error for error in errors))
        self.assertTrue(any("path_change_reason" in error for error in errors))


if __name__ == "__main__":
    unittest.main()
