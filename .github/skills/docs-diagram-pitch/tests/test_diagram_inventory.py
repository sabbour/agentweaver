import importlib.util
import json
import tempfile
import unittest
from argparse import Namespace
from pathlib import Path


SCRIPT = Path(__file__).parents[1] / "scripts" / "diagram_inventory.py"
SPEC = importlib.util.spec_from_file_location("diagram_inventory", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader
SPEC.loader.exec_module(MODULE)


class DiagramInventoryTests(unittest.TestCase):
    def test_discovers_stable_paths_and_shared_ownership(self):
        with tempfile.TemporaryDirectory() as directory:
            repo = Path(directory)
            (repo / "docs/diagrams/src").mkdir(parents=True)
            (repo / "docs/guide").mkdir()
            (repo / "docs/deep-dive").mkdir()
            (repo / "docs/diagrams/src/shared.drawio").write_text("<mxfile/>", encoding="utf-8")
            (repo / "docs/guide/a.md").write_text("![x](../diagrams/shared.png)", encoding="utf-8")
            (repo / "docs/deep-dive/b.md").write_text("![x](../diagrams/shared.png)", encoding="utf-8")

            data = MODULE.discover(repo, repo / "inventory.json")

            self.assertEqual(1, len(data["diagrams"]))
            entry = data["diagrams"][0]
            self.assertEqual("docs/diagrams/shared.png", entry["output_path"])
            self.assertEqual(["deep-dive", "guide"], entry["referenced_areas"])
            self.assertEqual("shared", entry["owner_area"])

    def test_validation_requires_merge_target_and_stable_output(self):
        data = {
            "version": 1,
            "diagrams": [
                {
                    "name": "old",
                    "source_path": "docs/diagrams/src/old.drawio",
                    "source_kind": "drawio",
                    "output_path": "docs/diagrams/renamed.png",
                    "references": [],
                    "referenced_areas": ["guide"],
                    "owner_area": "guide",
                    "disposition": "merge",
                    "target": None,
                    "status": "queued",
                    "rationale": ""
                }
            ]
        }
        errors = MODULE.validate(data)
        self.assertTrue(any("output_path must remain" in error for error in errors))
        self.assertTrue(any("requires a different existing target" in error for error in errors))

    def test_selects_by_exact_name_and_area(self):
        data = {
            "diagrams": [
                {"name": "a", "owner_area": "guide", "disposition": "redesign", "status": "queued"},
                {"name": "b", "owner_area": "deep-dive", "disposition": "redesign", "status": "queued"}
            ]
        }
        args = Namespace(name=["a"], area=["guide"], disposition=None, status=["queued"])
        self.assertEqual(["a"], [entry["name"] for entry in MODULE.selected_entries(data, args)])

    def test_builds_repeated_spec_command(self):
        command = MODULE.build_run_command(["alpha", "beta"], "render", npm_command="npm")
        self.assertEqual(
            [
                "npm",
                "run",
                "docs:render-diagrams",
                "--",
                "--spec",
                "alpha",
                "--spec",
                "beta",
            ],
            command,
        )

    def test_discovery_preserves_review_decisions(self):
        with tempfile.TemporaryDirectory() as directory:
            repo = Path(directory)
            (repo / "docs/diagrams/src").mkdir(parents=True)
            (repo / "docs/guide").mkdir()
            (repo / "docs/diagrams/src/architecture.drawio").write_text("<mxfile/>", encoding="utf-8")
            inventory = repo / "inventory.json"
            inventory.write_text(
                json.dumps(
                    {
                        "version": 1,
                        "diagrams": [
                            {
                                "name": "architecture",
                                "owner_area": "guide",
                                "disposition": "redesign",
                                "target": None,
                                "status": "in_progress",
                                "rationale": "Needs richer native symbols"
                            }
                        ]
                    }
                ),
                encoding="utf-8",
            )

            entry = MODULE.discover(repo, inventory)["diagrams"][0]

            self.assertEqual("guide", entry["owner_area"])
            self.assertEqual("redesign", entry["disposition"])
            self.assertEqual("in_progress", entry["status"])
            self.assertEqual("Needs richer native symbols", entry["rationale"])


if __name__ == "__main__":
    unittest.main()
