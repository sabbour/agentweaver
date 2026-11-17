from __future__ import annotations

import json
import shutil
import unittest
import uuid
from pathlib import Path
from subprocess import CompletedProcess
from unittest.mock import patch

import lint
import materialize_export


HERE = Path(__file__).resolve().parent
FIXTURE = HERE / "fixtures" / "finite-network-v1.export.json"


class MaterializeExportTests(unittest.TestCase):
    def setUp(self) -> None:
        self.scratch = HERE / f".test-output-{uuid.uuid4().hex}"
        self.scratch.mkdir()

    def tearDown(self) -> None:
        shutil.rmtree(self.scratch)

    def _export(self) -> dict:
        return json.loads(FIXTURE.read_text(encoding="utf-8"))

    def _write_export(self, value: dict, *, indent: int | None = None) -> Path:
        path = self.scratch / "export.json"
        path.write_text(json.dumps(value, indent=indent), encoding="utf-8")
        return path

    def _rehash_export(self, export: dict) -> None:
        artifact = export["artifact"]
        digest = materialize_export._content_hash(
            bytes(artifact["manifestBytes"]),
            [(entry["relativePath"], bytes(entry["bytes"])) for entry in artifact["files"]],
        )
        artifact["contentSha256"] = digest
        export["contentSha256"] = digest

    def test_materializes_exact_manifest_and_files(self) -> None:
        export = self._export()
        manifest_path = materialize_export.materialize(
            FIXTURE,
            self.scratch / "artifact",
        )

        self.assertEqual(bytes(export["artifact"]["manifestBytes"]), manifest_path.read_bytes())
        for file in export["artifact"]["files"]:
            self.assertEqual(
                bytes(file["bytes"]),
                manifest_path.parent.joinpath(*file["relativePath"].split("/")).read_bytes(),
            )

    def test_accepts_large_pretty_printed_export_within_native_bounds(self) -> None:
        manifest = [1]
        payload = [255] * materialize_export.MAX_FILE_BYTES
        files = [
            {"relativePath": f"policy/large-{index}.rego", "bytes": payload}
            for index in range(3)
        ]
        digest = materialize_export._content_hash(
            bytes(manifest),
            [(entry["relativePath"], bytes(entry["bytes"])) for entry in files],
        )
        export = {
            "artifact": {
                "manifestBytes": manifest,
                "files": files,
                "contentSha256": digest,
            },
            "contentSha256": digest,
        }
        export_path = self._write_export(export, indent=2)
        self.assertGreater(export_path.stat().st_size, materialize_export.MAX_EXPORT_BYTES // 2)
        output = self.scratch / "large-artifact"

        manifest_path = materialize_export.materialize(export_path, output)

        self.assertEqual(bytes(manifest), manifest_path.read_bytes())
        for entry in files:
            path = manifest_path.parent.joinpath(*entry["relativePath"].split("/"))
            self.assertEqual(bytes(entry["bytes"]), path.read_bytes())

    def test_rejects_manifest_descendant_path_before_creating_output(self) -> None:
        export = self._export()
        export["artifact"]["files"][0]["relativePath"] = "manifest.yaml/child.rego"
        self._rehash_export(export)
        export_path = self._write_export(export)
        output = self.scratch / "artifact"

        with self.assertRaisesRegex(ValueError, "manifest.yaml"):
            materialize_export.materialize(export_path, output)

        self.assertFalse(output.exists())

    def test_rejects_hash_mismatch_before_creating_output(self) -> None:
        export = self._export()
        export["artifact"]["contentSha256"] = "0" * 64
        export_path = self._write_export(export)
        output = self.scratch / "artifact"

        with self.assertRaisesRegex(ValueError, "contentSha256"):
            materialize_export.materialize(export_path, output)

        self.assertFalse(output.exists())

    def test_rejects_unsafe_path_before_creating_output(self) -> None:
        export = self._export()
        export["artifact"]["files"][0]["relativePath"] = "../outside"
        export_path = self._write_export(export)
        output = self.scratch / "artifact"

        with self.assertRaisesRegex(ValueError, "canonical"):
            materialize_export.materialize(export_path, output)

        self.assertFalse(output.exists())

    def test_rejects_existing_output_directory_without_overwriting(self) -> None:
        output = self.scratch / "artifact"
        output.mkdir()
        sentinel = output / "keep.txt"
        sentinel.write_text("keep", encoding="utf-8")

        with self.assertRaisesRegex(ValueError, "already exists"):
            materialize_export.materialize(FIXTURE, output)

        self.assertEqual("keep", sentinel.read_text(encoding="utf-8"))

    def test_rejects_output_outside_repository(self) -> None:
        output = materialize_export.ROOT.parent / f".acs-lint-outside-{uuid.uuid4().hex}"
        with self.assertRaisesRegex(ValueError, "inside the repository"):
            materialize_export.materialize(FIXTURE, output)


class ExplicitManifestLintTests(unittest.TestCase):
    def setUp(self) -> None:
        self.scratch = HERE / f".test-output-{uuid.uuid4().hex}"
        self.scratch.mkdir()
        self.first = self.scratch / "first.yaml"
        self.second = self.scratch / "second.json"
        self.first.write_text("{}", encoding="utf-8")
        self.second.write_text("{}", encoding="utf-8")

    def tearDown(self) -> None:
        shutil.rmtree(self.scratch)

    @patch("lint.subprocess.run")
    def test_runs_once_for_each_explicit_manifest(self, run: unittest.mock.Mock) -> None:
        run.return_value = CompletedProcess([], 0)

        result = lint.lint_manifests([self.first, self.second], "agt")

        self.assertEqual(0, result)
        self.assertEqual(2, run.call_count)
        self.assertEqual(
            ["agt", "--json", "lint-policy", "--strict", str(self.first)],
            run.call_args_list[0].args[0],
        )
        self.assertEqual(
            ["agt", "--json", "lint-policy", "--strict", str(self.second)],
            run.call_args_list[1].args[0],
        )

    @patch("lint.subprocess.run")
    def test_rejects_directory_before_running_any_lint(self, run: unittest.mock.Mock) -> None:
        result = lint.lint_manifests([self.first, self.scratch], "agt")

        self.assertEqual(1, result)
        run.assert_not_called()

    @patch("lint.subprocess.run")
    def test_rejects_symlink_manifest_before_running_lint(self, run: unittest.mock.Mock) -> None:
        with patch.object(Path, "is_symlink", return_value=True):
            result = lint.lint_manifests([self.first], "agt")

        self.assertEqual(1, result)
        run.assert_not_called()

    @patch("lint.subprocess.run")
    def test_propagates_lint_failure(self, run: unittest.mock.Mock) -> None:
        run.side_effect = [
            CompletedProcess([], 1),
            CompletedProcess([], 0),
        ]

        result = lint.lint_manifests([self.first, self.second], "agt")

        self.assertEqual(1, result)
        self.assertEqual(2, run.call_count)


if __name__ == "__main__":
    unittest.main()
