import importlib.util
import json
import unittest
from pathlib import Path

from jsonschema import Draft202012Validator


SCRIPT = Path(__file__).parents[1] / "scripts" / "validate_iteration_manifest.py"
SCHEMA = Path(__file__).parents[1] / "references" / "iteration-manifest.schema.json"
SPEC = importlib.util.spec_from_file_location("validate_iteration_manifest", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader
SPEC.loader.exec_module(MODULE)


def artifacts(name):
    return {
        "drawio": f"{name}.drawio",
        "png": f"{name}.png",
        "change_record": f"{name}.md",
        "png_inspected_print": True,
        "png_inspected_enlarged": True,
    }


def manifest():
    passes = []
    for number in range(1, 5):
        entry = {
            "number": number,
            "mode": "visual-upgrade" if number == 1 else "correction-only",
            **artifacts(f"diagram-pass-{number:02d}"),
            "orientation_defects": 0,
            "overlap_defects": 0,
            "arrow_defects": 0,
        }
        if number == 1:
            entry.update(
                {
                    "baseline_meaningful_xml": 100,
                    "result_meaningful_xml": 900,
                    "growth_metric": "visible-semantic-canonical-xml-v1",
                    "growth_ratio": 9,
                }
            )
        if number == 4:
            entry.update({"all_arrows_traced": True, "arrow_trace": []})
        passes.append(entry)
    return {
        "diagram": "diagram",
        "orientation": "A5-landscape",
        "pitch": artifacts("diagram-pitch"),
        "passes": passes,
        "final_pass": 4,
    }


class IterationManifestTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.schema_validator = Draft202012Validator(
            json.loads(SCHEMA.read_text(encoding="utf-8"))
        )

    def test_accepts_valid_manifest(self):
        self.assertEqual([], MODULE.validate_manifest(manifest()))

    def test_schema_accepts_valid_manifest(self):
        self.assertEqual([], list(self.schema_validator.iter_errors(manifest())))

    def test_schema_rejects_pass_specific_contract_violations(self):
        invalid_manifests = []

        wrong_pass_one_mode = manifest()
        wrong_pass_one_mode["passes"][0]["mode"] = "correction-only"
        invalid_manifests.append(wrong_pass_one_mode)

        missing_pass_one_growth = manifest()
        del missing_pass_one_growth["passes"][0]["growth_ratio"]
        invalid_manifests.append(missing_pass_one_growth)

        wrong_correction_mode = manifest()
        wrong_correction_mode["passes"][2]["mode"] = "visual-upgrade"
        invalid_manifests.append(wrong_correction_mode)

        incomplete_pass_four_trace = manifest()
        incomplete_pass_four_trace["passes"][3]["all_arrows_traced"] = False
        invalid_manifests.append(incomplete_pass_four_trace)

        for value in invalid_manifests:
            with self.subTest(value=value):
                self.assertTrue(list(self.schema_validator.iter_errors(value)))

    def test_rejects_wrong_pass_modes_and_growth(self):
        value = manifest()
        value["passes"][0]["mode"] = "correction-only"
        value["passes"][0]["result_meaningful_xml"] = 899
        value["passes"][0]["growth_ratio"] = 8.99
        value["passes"][1]["mode"] = "visual-upgrade"

        errors = MODULE.validate_manifest(value)

        self.assertTrue(any("pass 1: mode" in error for error in errors))
        self.assertTrue(any("below 9x" in error for error in errors))
        self.assertTrue(any("pass 2: mode" in error for error in errors))

    def test_rejects_duplicate_or_unordered_pass_numbers(self):
        value = manifest()
        value["passes"][2]["number"] = 2

        errors = MODULE.validate_manifest(value)

        self.assertTrue(any("ordered and contiguous" in error for error in errors))
        self.assertTrue(any("unique" in error for error in errors))

    def test_rejects_nonlatest_or_dirty_final_pass(self):
        value = manifest()
        value["final_pass"] = 3
        value["passes"][3]["all_arrows_traced"] = False
        value["passes"][3]["arrow_defects"] = 1

        errors = MODULE.validate_manifest(value)

        self.assertTrue(any("actual latest pass" in error for error in errors))
        self.assertTrue(any("final pass must trace" in error for error in errors))
        self.assertTrue(any("arrow_defects must be zero" in error for error in errors))

    def test_rejects_duplicate_artifact_paths(self):
        value = manifest()
        value["passes"][1]["png"] = value["passes"][0]["png"]
        self.assertTrue(
            any("artifact paths must be unique" in error for error in MODULE.validate_manifest(value))
        )


if __name__ == "__main__":
    unittest.main()
