import hashlib
import io
from contextlib import redirect_stderr
import sys
import tempfile
import unittest
import urllib.request
from pathlib import Path
from subprocess import CompletedProcess
from unittest.mock import patch


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import package


class PackageTests(unittest.TestCase):
    def test_archive_names_and_apphosts_match_supported_rids(self):
        self.assertEqual(package.archive_info("win-x64").name, "agentweaver-cli-win-x64.zip")
        self.assertEqual(package.archive_info("linux-x64").name, "agentweaver-cli-linux-x64.tar.gz")
        self.assertEqual(package.archive_info("osx-arm64").name, "agentweaver-cli-osx-arm64.tar.gz")
        self.assertEqual(package.APPHOSTS["win-x64"], "agentweaver.exe")
        self.assertEqual(package.APPHOSTS["linux-x64"], "agentweaver")
        with self.assertRaises(package.PackageError):
            package.archive_info("linux-arm64")

    def test_archives_are_reproducible_and_contain_only_hashed_publish_files(self):
        for rid in package.RIDS:
            with self.subTest(rid=rid):
                files = {
                    package.APPHOSTS[rid]: b"host executable",
                    "Agentweaver.Cli.runtimeconfig.json": b'{"runtimeOptions":{}}',
                    "README.txt": b"no Node or .NET runtime required",
                }
                first = io.BytesIO()
                second = io.BytesIO()
                package.write_archive(first, rid, files)
                package.write_archive(second, rid, files)
                self.assertEqual(first.getvalue(), second.getvalue())

                inventory = package.archive_inventory(io.BytesIO(first.getvalue()), rid)
                self.assertEqual([item["path"] for item in inventory], sorted(files))
                self.assertEqual(
                    inventory,
                    [package.file_metadata(name, content) for name, content in sorted(files.items())],
                )

    def test_tar_archives_preserve_apphost_executable_permissions(self):
        for rid in ("linux-x64", "osx-arm64"):
            with self.subTest(rid=rid):
                stream = io.BytesIO()
                package.write_archive(stream, rid, {package.APPHOSTS[rid]: b"executable"})
                with package.tarfile.open(fileobj=io.BytesIO(stream.getvalue()), mode="r:gz") as archive:
                    apphost = archive.getmember(package.APPHOSTS[rid])
                    self.assertEqual(apphost.mode & 0o777, 0o755)
                    self.assertEqual(apphost.mtime, 0)
                    self.assertEqual(apphost.uid, 0)
                    self.assertEqual(apphost.gid, 0)

    def test_receipt_verifier_checks_source_pin_archive_hashes_and_smoke(self):
        files = {
            "agentweaver.exe": b"private self-contained apphost",
            "README.txt": b"vault and license notice",
        }
        archive = io.BytesIO()
        package.write_archive(archive, "win-x64", files)
        archive_bytes = archive.getvalue()
        commit, tree = "a" * 40, "b" * 40
        receipt = {
            "schemaVersion": 1,
            "product": "Agentweaver 1.x CLI",
            "version": "1.0.0",
            "source": {"repository": package.SOURCE_REPOSITORY, "commit": commit, "tree": tree},
            "sdkVersion": "10.0.400",
            "rid": "win-x64",
            "archive": {
                "name": package.ARCHIVES["win-x64"].name,
                "sizeBytes": len(archive_bytes),
                "sha256": hashlib.sha256(archive_bytes).hexdigest(),
            },
            "files": [package.file_metadata(name, content) for name, content in sorted(files.items())],
            "runtimeTest": {
                "status": "passed",
                "hostRid": "win-x64",
                "tests": ["help", "version", "gateway-openapi-discovery"],
            },
        }
        self.assertEqual(
            package.verify_receipt(
                receipt,
                io.BytesIO(archive_bytes),
                "win-x64",
                source_commit=commit,
                source_tree=tree,
                require_smoke=True,
            ),
            receipt,
        )
        altered = dict(receipt, archive={**receipt["archive"], "sha256": "0" * 64})
        with self.assertRaises(package.PackageError):
            package.verify_receipt(altered, io.BytesIO(archive_bytes), "win-x64")
        altered_files = dict(receipt, files=[{**receipt["files"][0], "sha256": "0" * 64}, *receipt["files"][1:]])
        with self.assertRaises(package.PackageError):
            package.verify_receipt(altered_files, io.BytesIO(archive_bytes), "win-x64")
        with self.assertRaises(package.PackageError):
            package.verify_receipt(receipt, io.BytesIO(archive_bytes), "win-x64", source_tree="c" * 40)

    def test_archive_rejects_path_escape_and_missing_apphost(self):
        with self.assertRaises(package.PackageError):
            package.write_archive(io.BytesIO(), "win-x64", {"../escape": b"bad", "agentweaver.exe": b"app"})
        with self.assertRaises(package.PackageError):
            package.write_archive(io.BytesIO(), "win-x64", {"README.txt": b"missing apphost"})
        for name in ("node.exe", "tools/Agentweaver.Cli/Program.cs", ".env", ".env.local"):
            with self.subTest(name=name), self.assertRaises(package.PackageError):
                package.write_archive(io.BytesIO(), "win-x64", {"agentweaver.exe": b"app", name: b"forbidden"})
        with self.assertRaises(package.PackageError):
            package.write_archive(
                io.BytesIO(),
                "win-x64",
                {"agentweaver.exe": b"app", "README.txt": b"one", "readme.txt": b"two"},
            )

    def test_publish_is_self_contained_without_single_file_trimming_or_aot(self):
        command = package.publish_arguments(
            Path("tools/Agentweaver.Cli/Agentweaver.Cli.csproj"),
            "win-x64",
            Path("artifacts/cli/win-x64/publish"),
            "a" * 40,
            "b" * 40,
        )
        self.assertIn("--self-contained", command)
        self.assertIn("true", command)
        self.assertIn("-p:UseAppHost=true", command)
        self.assertIn("-p:PublishTrimmed=false", command)
        self.assertIn("-p:PublishAot=false", command)
        self.assertIn("-p:AgentweaverSourceSha=" + "a" * 40, command)
        self.assertIn("-p:AgentweaverSourceTree=" + "b" * 40, command)
        self.assertNotIn("-p:PublishSingleFile=true", command)
        self.assertNotIn("--no-self-contained", command)
        restore = package.restore_arguments(
            Path("tools/Agentweaver.Cli/Agentweaver.Cli.csproj"),
            "a" * 40,
            "b" * 40,
        )
        self.assertIn("--locked-mode", restore)
        self.assertNotIn("-r", restore)

    def test_command_exit_code_is_propagated(self):
        command = ["dotnet", "publish"]
        with patch.object(package.subprocess, "run", return_value=CompletedProcess(command, 37, "", "failure")):
            with self.assertRaises(package.CommandFailure) as failure:
                package.checked_run(command, Path.cwd())
        self.assertEqual(failure.exception.returncode, 37)
        with patch.object(package, "build_package", side_effect=failure.exception), redirect_stderr(io.StringIO()):
            self.assertEqual(package.main(["build", "--rid", "win-x64"]), 37)

    def test_build_rejects_source_changes_not_covered_by_git_pin(self):
        with patch.object(package, "checked_run", return_value=""):
            package.assert_build_sources_clean(Path.cwd())
        with patch.object(package, "checked_run", return_value=" M tools/Agentweaver.Cli/Program.cs"):
            with self.assertRaisesRegex(package.PackageError, "clean committed"):
                package.assert_build_sources_clean(Path.cwd())

    def test_build_rejects_untracked_intermediate_msbuild_imports(self):
        for path in ("tools/Directory.Build.props", "tools/Directory.Build.targets"):
            with self.subTest(path=path):
                def status_for_requested_paths(command, *_args, **_kwargs):
                    paths = command[command.index("--") + 1:]
                    return f"?? {path}" if path in paths else ""

                with patch.object(package, "checked_run", side_effect=status_for_requested_paths):
                    with self.assertRaisesRegex(package.PackageError, "clean committed"):
                        package.assert_build_sources_clean(Path.cwd())

    def test_smoke_environment_has_no_development_runtime_path(self):
        environment = package.smoke_environment("http://127.0.0.1:12345")
        self.assertEqual(environment["PATH"], "")
        self.assertEqual(environment["AGENTWEAVER_GATEWAY_URL"], "http://127.0.0.1:12345")
        self.assertNotIn("DOTNET_ROOT", environment)
        self.assertNotIn("NODE_PATH", environment)

    def test_smoke_timeout_fails_closed(self):
        timeout = package.subprocess.TimeoutExpired(["agentweaver", "--help"], 45)
        with tempfile.TemporaryDirectory(prefix="agentweaver-cli-test-") as directory:
            executable = Path(directory) / "agentweaver.exe"
            with patch.object(package, "runtime_rid", return_value="win-x64"), patch.object(package.subprocess, "run", side_effect=timeout):
                with self.assertRaisesRegex(package.PackageError, "timed out"):
                    package.run_smoke(
                        executable,
                        "win-x64",
                        "1.0.0",
                        "a" * 40,
                        "b" * 40,
                        Path(__file__).resolve().parents[3],
                    )

    def test_smoke_rejects_executable_inside_checkout(self):
        root = Path(__file__).resolve().parents[3]
        with patch.object(package, "runtime_rid", return_value="win-x64"):
            with self.assertRaisesRegex(package.PackageError, "executable must be outside"):
                package.run_smoke(root / "agentweaver.exe", "win-x64", "1.0.0", "a" * 40, "b" * 40, root)

    def test_smoke_runs_help_version_and_controlled_gateway_from_outside_checkout(self):
        calls = []

        def fake_process(command, **kwargs):
            calls.append((command, kwargs))
            if command[-2:] == ["api", "operations"]:
                with urllib.request.urlopen(kwargs["env"]["AGENTWEAVER_GATEWAY_URL"] + "/openapi/v1.json") as response:
                    self.assertEqual(response.status, 200)
                    self.assertIn("packageSmoke", response.read().decode())
                return CompletedProcess(command, 0, "packageSmoke\n", "")
            if command[-1] == "--version":
                return CompletedProcess(command, 0, "Agentweaver 1.x 1.0.0 source " + "a" * 12 + " tree " + "b" * 12, "")
            return CompletedProcess(command, 0, "api operations\nauth login\ntest", "")

        root = Path(__file__).resolve().parents[3]
        with tempfile.TemporaryDirectory(prefix="agentweaver-cli-test-") as directory:
            executable = Path(directory) / "agentweaver.exe"
            with patch.object(package, "runtime_rid", return_value="win-x64"), patch.object(package.subprocess, "run", side_effect=fake_process):
                package.run_smoke(executable, "win-x64", "1.0.0", "a" * 40, "b" * 40, root)

        self.assertEqual([call[0][1:] for call in calls], [["--help"], ["--version"], ["api", "operations"]])
        for _command, options in calls:
            self.assertEqual(options["env"]["PATH"], "")
            self.assertFalse(Path(options["cwd"]).is_relative_to(root))
            self.assertFalse(Path(_command[0]).is_relative_to(root))

    def test_distribution_readme_names_vault_requirements_and_source(self):
        text = package.readme("1.0.0", "a" * 40, "b" * 40, "linux-x64").decode()
        self.assertIn("does not require Node.js or an installed .NET runtime", text)
        self.assertIn("libsecret", text)
        self.assertIn("unlocked Secret Service", text)
        self.assertIn("fails closed", text)
        self.assertIn("a" * 40, text)
        self.assertIn("b" * 40, text)
        self.assertIn("agentweaver --help", text)


if __name__ == "__main__":
    unittest.main()
