import http.client
import json
import os
import shutil
import socket
import ssl
import subprocess
import sys
import threading
import time
import unittest
import uuid
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import verify_windows_archive_checks as checks


class HangingProcess:
    def __init__(self, pid=8123):
        self.pid = pid
        self.returncode = None

    def poll(self):
        return None

    def terminate(self):
        pass

    def kill(self):
        pass

    def wait(self, timeout):
        raise subprocess.TimeoutExpired("owned-process.exe", timeout)


class ExitedProcess:
    pid = 8234
    returncode = 0

    def poll(self):
        return 0

    def communicate(self, timeout=None):
        return "helper-result", ""


class WindowsArchiveCleanupTests(unittest.TestCase):
    def test_unverified_listener_cleanup_cannot_write_a_passing_sidecar(self):
        folder = Path(__file__).resolve().parent / f".archive-sidecar-{uuid.uuid4().hex}"
        folder.mkdir()
        output = folder / checks.SIDECAR_NAME
        context = {
            "journal": {"cleanupErrors": [], "processIdentities": []},
            "children": [],
            "fixtureState": SimpleNamespace(secrets=set()),
        }
        with (
            patch.object(checks, "persist_journal"),
            patch.object(checks, "wait_for_fixture_threads", return_value=False),
        ):
            cleanup = checks.cleanup_run(context, object(), threading.Thread())

        self.assertEqual(cleanup["status"], "failed")
        self.assertFalse(cleanup["listenerStopped"])
        result = checks.initial_sidecar("fixture-run", "a" * 40, "b" * 40, checks.utc_now(), "passed")
        try:
            result["controlledArchiveChecks"]["cleanup"] = cleanup
            checks.fail_sidecar_if_cleanup_unverified(result, cleanup)
            checks.save_sidecar(output, result)

            sidecar = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual(sidecar["controlledArchiveChecks"]["status"], "failed")
            self.assertFalse(sidecar["controlledArchiveChecks"]["cleanup"]["listenerStopped"])
        finally:
            shutil.rmtree(folder, ignore_errors=True)

    def test_unstopped_owned_process_preserves_native_resources_and_workspace(self):
        process = HangingProcess()
        identity = {"state": "running", "role": "apphost"}
        context = {
            "journal": {"cleanupProgress": "running", "cleanupErrors": [], "processIdentities": []},
            "children": [(process, identity)],
            "fixtureState": SimpleNamespace(secrets=set()),
            "certificateMaterial": {},
        }

        with patch.object(checks, "persist_journal"):
            result = checks.cleanup_run(context, None, None)

        self.assertEqual(identity["state"], "unresolved")
        self.assertEqual(result["status"], "failed")
        self.assertFalse(result["workspaceRemoved"])
        self.assertIn("owned-cli-or-helper-process-did-not-stop", result["errors"])

    def test_failed_logout_keeps_cleanup_failed_even_when_credential_is_removed(self):
        executable = Path(__file__).resolve()
        workspace = Path(__file__).resolve().parent / "absent-archive-cleanup-workspace"
        self.assertFalse(workspace.exists())
        context = {
            "journal": {
                "credentialTarget": "exact-fixture-target",
                "credentialSeedAttempted": True,
                "nativeProbeAcquisitionAttempted": False,
                "nativeProbeTarget": "exact-probe-target",
                "cleanupErrors": [],
                "processIdentities": [],
            },
            "children": [],
            "fixtureState": SimpleNamespace(secrets=set()),
            "certificateMaterial": {},
            "executable": executable,
            "executableHash": checks.sha256_file(executable),
            "markerPath": workspace / "account-state" / "refresh-marker",
            "caPath": workspace / "archive-ca.cer",
            "lockPath": workspace / "account-state" / "product.lock",
            "journalPath": workspace / "journal.json",
            "journalTemporaryPath": workspace / "journal.json.new",
            "workspace": workspace,
        }

        with (
            patch.object(checks, "persist_journal"),
            patch.object(checks, "credential_read", side_effect=[object(), None, None]),
            patch.object(checks, "credential_delete") as credential_delete,
            patch.object(checks, "run_cli", return_value=(1, "", "")) as run_cli,
            patch.object(checks, "cleanup_marker"),
            patch.object(checks, "remove_owned_trust"),
            patch.object(checks, "delete_workspace_paths", return_value=(True, [])),
        ):
            result = checks.cleanup_run(context, None, None)

        self.assertEqual(result["status"], "failed")
        self.assertIn("exact-apphost-auth-logout-failed", result["errors"])
        self.assertTrue(result["productionFixtureCredentialAbsent"])
        credential_delete.assert_called_once_with("exact-fixture-target")
        run_cli.assert_called_once_with(
            context,
            ["auth", "logout"],
            "cleanup-auth-logout",
            timeout=45,
        )

    def test_cleanup_reaps_a_logout_child_before_removing_owned_state(self):
        workspace = Path(__file__).resolve().parent / "absent-logout-child-workspace"
        process = HangingProcess()
        context = {
            "journal": {
                "credentialTarget": "exact-fixture-target",
                "credentialSeedAttempted": True,
                "nativeProbeAcquisitionAttempted": False,
                "nativeProbeTarget": "exact-probe-target",
                "cleanupErrors": [],
                "processIdentities": [],
            },
            "children": [],
            "fixtureState": SimpleNamespace(secrets=set()),
            "certificateMaterial": {},
            "executable": Path(__file__).resolve(),
            "executableHash": checks.sha256_file(Path(__file__).resolve()),
            "markerPath": workspace / "account-state" / "refresh-marker",
            "caPath": workspace / "archive-ca.cer",
            "lockPath": workspace / "account-state" / "product.lock",
            "journalPath": workspace / "journal.json",
            "journalTemporaryPath": workspace / "journal.json.new",
            "workspace": workspace,
        }

        def logout_child(*_args, **_kwargs):
            identity = {
                "pid": process.pid,
                "startTimeFiletime": 123,
                "role": "cleanup-auth-logout",
                "state": "running",
            }
            context["children"].append((process, identity))
            context["journal"]["processIdentities"].append(identity)
            return 0, "", ""

        with (
            patch.object(checks, "persist_journal"),
            patch.object(checks, "credential_read", return_value="synthetic-refresh"),
            patch.object(checks, "credential_delete") as credential_delete,
            patch.object(checks, "run_cli", side_effect=logout_child),
            patch.object(checks, "remove_owned_trust") as remove_owned_trust,
            patch.object(checks, "delete_workspace_paths") as delete_workspace_paths,
        ):
            result = checks.cleanup_run(context, None, None)

        self.assertEqual(result["status"], "failed")
        self.assertIn("owned-cli-or-helper-process-did-not-stop", result["errors"])
        credential_delete.assert_not_called()
        remove_owned_trust.assert_not_called()
        delete_workspace_paths.assert_not_called()

    def test_cleanup_records_apphost_hash_before_removing_extraction(self):
        folder = Path(__file__).resolve().parent / f".archive-hash-{uuid.uuid4().hex}"
        folder.mkdir()
        workspace = folder / "workspace"
        workspace.mkdir()
        executable = workspace / "agentweaver.exe"
        executable.write_bytes(b"exact extracted apphost")
        original_hash = checks.sha256_file(executable)
        journal_path = workspace / "journal.json"
        journal_path.write_text("{}", encoding="utf-8")
        context = {
            "journal": {
                "credentialTarget": "exact-fixture-target",
                "credentialSeedAttempted": False,
                "nativeProbeAcquisitionAttempted": False,
                "nativeProbeTarget": "exact-probe-target",
                "trustImportAttempted": False,
                "cleanupErrors": [],
                "createdPaths": [
                    str(executable),
                    str(journal_path),
                    str(workspace / "journal.json.new"),
                ],
                "processIdentities": [],
            },
            "children": [],
            "fixtureState": SimpleNamespace(secrets=set()),
            "certificateMaterial": {},
            "executable": executable,
            "executableHash": original_hash,
            "workspace": workspace,
            "journalPath": journal_path,
            "journalTemporaryPath": workspace / "journal.json.new",
            "markerPath": workspace / "refreshing",
            "lockPath": workspace / "stable.lock",
            "caPath": workspace / "ca.cer",
            "childWorkingDirectory": workspace,
        }
        try:
            with (
                patch.object(checks, "persist_journal"),
                patch.object(checks, "credential_read", return_value=None),
            ):
                result = checks.cleanup_run(context, None, None)

            self.assertEqual(result["status"], "passed")
            self.assertEqual(result["executableSha256After"], original_hash)
            self.assertFalse(executable.exists())
            self.assertTrue(result["workspaceRemoved"])
        finally:
            shutil.rmtree(folder, ignore_errors=True)


class WindowsArchivePreflightTests(unittest.TestCase):
    port = 43123

    def _profile(self, proxy_target=None, proxy_bypassed=False, ps_major=7):
        return {
            "sid": "S-1-5-21-1-2-3-1001",
            "localAppData": "C:\\Users\\fixture\\AppData\\Local",
            "userProfile": "C:\\Users\\fixture",
            "appData": "C:\\Users\\fixture\\AppData\\Roaming",
            "systemRoot": "C:\\Windows",
            "psMajor": ps_major,
            "proxyBypassed": proxy_bypassed,
            "proxyTarget": proxy_target,
        }

    def _expected_profile(self, payload):
        output = payload if isinstance(payload, str) else json.dumps(payload)
        with patch.object(checks, "run_powershell", return_value=output) as run_powershell:
            profile = checks.expected_profile(Path("pwsh.exe"), self.port)
        run_powershell.assert_called_once_with(
            Path("pwsh.exe"),
            checks.PREFLIGHT_SCRIPT,
            {"ARCHIVE_FIXTURE_URI": f"https://127.0.0.1:{self.port}/"},
            timeout=5,
            preserve_proxy_env=True,
        )
        return profile

    def test_expected_profile_accepts_direct_and_fixture_echo_for_either_diagnostic(self):
        fixture_uri = f"https://127.0.0.1:{self.port}/"
        for proxy_bypassed in (False, True):
            for proxy_target in (None, fixture_uri):
                with self.subTest(proxy_bypassed=proxy_bypassed, proxy_target=proxy_target):
                    profile = self._expected_profile(
                        self._profile(proxy_target=proxy_target, proxy_bypassed=proxy_bypassed)
                    )
                    self.assertIs(profile["proxyBypassed"], proxy_bypassed)
                    self.assertEqual(profile["proxyTarget"], proxy_target)

    def test_expected_profile_blocks_redirects_and_invalid_metadata(self):
        invalid_targets = (
            f"http://127.0.0.1:{self.port}/",
            f"https://localhost:{self.port}/",
            f"https://127.0.0.1:{self.port + 1}/",
            f"https://127.0.0.1:{self.port}/other",
            f"https://127.0.0.1:{self.port}/?query",
            f"https://127.0.0.1:{self.port}/#fragment",
            "",
            7,
            [],
        )
        invalid_profiles = [
            ("malformed JSON", "{"),
            ("non-object JSON", "[]"),
            ("missing target", {key: value for key, value in self._profile().items() if key != "proxyTarget"}),
            ("missing bypass diagnostic", {key: value for key, value in self._profile().items() if key != "proxyBypassed"}),
            ("non-boolean bypass diagnostic", self._profile(proxy_bypassed=1)),
            ("missing version", {key: value for key, value in self._profile().items() if key != "psMajor"}),
            ("malformed version", self._profile(ps_major="7")),
            ("boolean version", self._profile(ps_major=True)),
            ("fractional version", self._profile(ps_major=7.0)),
            ("old version", self._profile(ps_major=6)),
        ]
        invalid_profiles.extend(
            (
                f"redirect target {index}, bypassed={proxy_bypassed}",
                self._profile(proxy_target=target, proxy_bypassed=proxy_bypassed),
            )
            for index, target in enumerate(invalid_targets)
            for proxy_bypassed in (False, True)
        )

        for name, payload in invalid_profiles:
            with self.subTest(name=name), self.assertRaises(checks.BlockedError):
                self._expected_profile(payload)

    def test_expected_profile_preserves_only_inherited_proxy_inputs(self):
        proxy_names = {
            "hTtP_PrOxY": "http://proxy.example.invalid:1234",
            "https_proxy": "",
            "ALL_PROXY": "socks5://proxy.example.invalid:1080",
            "no_Proxy": "127.0.0.1",
        }
        sanitized = {
            "PATH": "",
            "NO_PROXY": "127.0.0.1,localhost",
            "no_proxy": "127.0.0.1,localhost",
            "SANITIZED_VALUE": "retained",
        }
        profile_json = json.dumps(self._profile())
        process = SimpleNamespace(
            pid=8124,
            returncode=0,
            poll=lambda: 0,
            communicate=lambda timeout=None: (profile_json, ""),
        )
        inherited = {**proxy_names, "UNRELATED_ENV": "not inherited"}
        with (
            patch.object(checks.os, "environ", inherited),
            patch.object(checks, "powershell_env", return_value=sanitized),
            patch.object(checks.subprocess, "Popen", return_value=process) as popen,
            patch.object(checks.subprocess, "CREATE_NEW_PROCESS_GROUP", 0, create=True),
        ):
            profile = checks.expected_profile(Path("pwsh.exe"), self.port)

        child_env = popen.call_args.kwargs["env"]
        self.assertEqual(profile["proxyTarget"], None)
        self.assertEqual({name: child_env[name] for name in proxy_names}, proxy_names)
        self.assertNotIn("UNRELATED_ENV", child_env)
        self.assertEqual(child_env["SANITIZED_VALUE"], "retained")
        self.assertEqual(child_env["PATH"], "")

        process = SimpleNamespace(
            pid=8125,
            returncode=0,
            poll=lambda: 0,
            communicate=lambda timeout=None: (profile_json, ""),
        )
        with (
            patch.object(checks.os, "environ", {"UNRELATED_ENV": "not inherited"}),
            patch.object(checks, "powershell_env", return_value=sanitized),
            patch.object(checks.subprocess, "Popen", return_value=process) as popen,
            patch.object(checks.subprocess, "CREATE_NEW_PROCESS_GROUP", 0, create=True),
        ):
            checks.expected_profile(Path("pwsh.exe"), self.port)

        child_env = popen.call_args.kwargs["env"]
        self.assertNotIn("NO_PROXY", child_env)
        self.assertNotIn("no_proxy", child_env)
        self.assertNotIn("UNRELATED_ENV", child_env)

    def test_run_powershell_default_environment_does_not_inherit_proxy_inputs(self):
        process = ExitedProcess()
        sanitized = {"PATH": "", "NO_PROXY": "127.0.0.1,localhost", "no_proxy": "127.0.0.1,localhost"}
        with (
            patch.object(checks.os, "environ", {"HTTP_PROXY": "http://proxy.example.invalid"}),
            patch.object(checks, "powershell_env", return_value=sanitized),
            patch.object(checks.subprocess, "Popen", return_value=process) as popen,
            patch.object(checks.subprocess, "CREATE_NEW_PROCESS_GROUP", 0, create=True),
        ):
            checks.run_powershell(Path("pwsh.exe"), "safe command")

        self.assertEqual(popen.call_args.kwargs["env"], sanitized)

    def test_preflight_script_emits_one_explicit_nullable_proxy_target(self):
        self.assertEqual(checks.PREFLIGHT_SCRIPT.count("$proxy.GetProxy($uri)"), 1)
        self.assertIn("$proxyTargetUri = $null", checks.PREFLIGHT_SCRIPT)
        self.assertIn("proxyTarget = $proxyTargetUri", checks.PREFLIGHT_SCRIPT)
        self.assertIn("$proxy.IsBypassed($uri)", checks.PREFLIGHT_SCRIPT)

    def test_preflight_does_not_reject_false_bypass_diagnostic_again(self):
        folder = Path(__file__).resolve().parent / f".archive-preflight-{uuid.uuid4().hex}"
        folder.mkdir()
        output_dir = folder / "output"
        output_dir.mkdir()
        workspace = folder / "workspace"
        directory = folder / "candidate"
        source = folder / "source"
        powershell = folder / "pwsh.exe"
        try:
            with (
                patch.object(checks.os, "name", "nt"),
                patch.object(checks, "Path", type(folder)),
                patch.object(checks.package, "runtime_rid", return_value="win-x64"),
                patch.object(checks.ctypes, "sizeof", return_value=8),
                patch.object(checks, "verify_windows_path", side_effect=lambda path, **_kwargs: path),
                patch.object(
                    checks,
                    "check_candidate",
                    return_value=(
                        {},
                        directory / "candidate.zip",
                        directory / "candidate.receipt.json",
                        "b" * 40,
                    ),
                ),
                patch.object(checks, "git_worktree_roots", return_value=[]),
                patch.object(checks, "reject_source_checkout_path"),
                patch.object(checks, "assert_no_reparse_components"),
                patch.object(checks, "expected_profile", return_value={"proxyBypassed": False}),
                patch.object(checks, "powershell_executable", return_value=powershell),
                patch.object(checks, "credential_read", return_value="occupied"),
            ):
                with self.assertRaisesRegex(checks.BlockedError, "Credential Manager"):
                    checks.preflight(
                        source,
                        "a" * 40,
                        directory,
                        workspace,
                        output_dir / checks.SIDECAR_NAME,
                    )
        finally:
            shutil.rmtree(folder, ignore_errors=True)


class WindowsArchiveRegressionTests(unittest.TestCase):
    def _powershell(self):
        if os.name != "nt":
            self.skipTest("Native Windows coverage is required.")
        return checks.powershell_executable()

    def _current_sid(self, executable):
        output = checks.run_powershell(
            executable,
            "[System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value",
            timeout=5,
        )
        return output.strip()

    def _start_tls_fixture(self):
        executable = self._powershell()
        sid = self._current_sid(executable)
        workspace = Path(__file__).resolve().parent / f".archive-tls-{uuid.uuid4().hex}"
        checks.secure_workspace(workspace, sid, executable)
        certificate = {}
        server = None
        worker = None
        try:
            certificate = checks.make_certificates(executable, uuid.uuid4().hex)
            ca_path = workspace / "test-root.pem"
            cert_path = workspace / "server.cer.pem"
            key_path = workspace / "server.key.pem"
            checks.write_new(ca_path, ssl.DER_cert_to_PEM_cert(bytes(certificate["caDer"])).encode("ascii"))
            checks.write_new(cert_path, bytes(certificate["leafCertificatePem"]))
            checks.write_new(key_path, bytes(certificate["leafPrivateKeyPem"]))
            server_tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            server_tls.minimum_version = ssl.TLSVersion.TLSv1_2
            server_tls.load_cert_chain(str(cert_path), str(key_path))
            state = checks.FIXTURE_STATE(
                "https://127.0.0.1",
                "fixture-tenant",
                "fixture-actor",
                "agentweaver-cli",
            )
            server = checks.FixtureServer(0, state, server_tls)
            self.assertNotIsInstance(server.socket, ssl.SSLSocket)
            worker = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.1}, daemon=True)
            worker.start()
            return workspace, certificate, ca_path, server, worker, state
        except BaseException:
            stopped = True
            if server is not None:
                stopped = (
                    checks.wait_for_fixture_threads(server, worker, state)
                    if worker is not None and worker.ident is not None
                    else server.server_close()
                )
            for value in certificate.values():
                if isinstance(value, bytearray):
                    value[:] = b"\0" * len(value)
            if stopped:
                shutil.rmtree(workspace, ignore_errors=True)
            raise

    def _wait_for_request_workers(self, server, count=1):
        deadline = time.monotonic() + 2
        while len(server.owned_workers()) < count and time.monotonic() < deadline:
            time.sleep(0.01)
        self.assertGreaterEqual(len(server.owned_workers()), count)

    def _assert_fixture_stopped(self, server, worker, state):
        request_workers = server.owned_workers()
        started = time.monotonic()
        self.assertTrue(checks.wait_for_fixture_threads(server, worker, state))
        self.assertLess(time.monotonic() - started, 5)
        self.assertFalse(worker.is_alive())
        self.assertTrue(all(not request_worker.is_alive() for request_worker in request_workers))
        self.assertEqual(server.owned_workers(), ())
        self.assertEqual(server.owned_sockets(), ())
        self.assertEqual(server.socket.fileno(), -1)
        with state.lock:
            self.assertFalse(state.active_threads)

    def _dispose_tls_fixture(self, workspace, certificate, server, worker, state):
        if server is not None and (
            server.socket.fileno() >= 0
            or server.owned_workers()
            or server.owned_sockets()
            or worker.is_alive()
        ):
            self.assertTrue(checks.wait_for_fixture_threads(server, worker, state))
        for value in certificate.values():
            if isinstance(value, bytearray):
                value[:] = b"\0" * len(value)
        if not server.owned_workers() and not server.owned_sockets() and not worker.is_alive():
            shutil.rmtree(workspace, ignore_errors=True)

    def test_workspace_acl_setup_creates_and_verifies_a_restricted_directory(self):
        executable = self._powershell()
        sid = self._current_sid(executable)
        workspace = Path(__file__).resolve().parent / f".archive-acl-{uuid.uuid4().hex}"
        try:
            checks.secure_workspace(workspace, sid, executable)
            output = checks.run_powershell(
                executable,
                r"""
$acl = Get-Acl -LiteralPath $env:ARCHIVE_WORKSPACE
$sid = [System.Security.Principal.SecurityIdentifier]::new($env:ARCHIVE_SID)
$actual = @($acl.Access | ForEach-Object {
  $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
})
[ordered]@{
  protected = $acl.AreAccessRulesProtected
  owner = $acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
  rules = $acl.Access.Count
  hasUser = $actual -contains $sid.Value
  hasSystem = $actual -contains 'S-1-5-18'
} | ConvertTo-Json -Compress
""",
                {"ARCHIVE_WORKSPACE": str(workspace), "ARCHIVE_SID": sid},
                timeout=5,
            )
            acl = json.loads(output)
            self.assertTrue(acl["protected"])
            self.assertEqual(acl["owner"], sid)
            self.assertEqual(acl["rules"], 2)
            self.assertTrue(acl["hasUser"])
            self.assertTrue(acl["hasSystem"])
        finally:
            if workspace.exists():
                workspace.rmdir()

    def test_zero_byte_tls_peer_does_not_block_fixture_shutdown(self):
        workspace, certificate, _, server, worker, state = self._start_tls_fixture()
        client = None
        try:
            client = socket.create_connection(server.server_address, timeout=2)
            self._wait_for_request_workers(server)
            self._assert_fixture_stopped(server, worker, state)
        finally:
            if client is not None:
                client.close()
            self._dispose_tls_fixture(workspace, certificate, server, worker, state)

    def test_partial_tls_handshake_does_not_block_fixture_shutdown(self):
        workspace, certificate, _, server, worker, state = self._start_tls_fixture()
        client = None
        try:
            client = socket.create_connection(server.server_address, timeout=2)
            client.sendall(b"\x16\x03\x03\x00\x10\x01")
            self._wait_for_request_workers(server)
            self._assert_fixture_stopped(server, worker, state)
        finally:
            if client is not None:
                client.close()
            self._dispose_tls_fixture(workspace, certificate, server, worker, state)

    def test_stalled_peer_does_not_block_verified_tls_peer_or_shutdown(self):
        workspace, certificate, ca_path, server, worker, state = self._start_tls_fixture()
        stalled = None
        client = None
        try:
            stalled = socket.create_connection(server.server_address, timeout=2)
            self._wait_for_request_workers(server)
            client_tls = ssl.create_default_context(cafile=str(ca_path))
            client = http.client.HTTPSConnection(
                "127.0.0.1",
                server.server_address[1],
                context=client_tls,
                timeout=3,
            )
            client.request("GET", "/openapi/v1.json")
            response = client.getresponse()
            self.assertEqual(response.status, 200)
            self.assertIn(b"openapi", response.read())
            self._assert_fixture_stopped(server, worker, state)
        finally:
            if client is not None:
                client.close()
            if stalled is not None:
                stalled.close()
            self._dispose_tls_fixture(workspace, certificate, server, worker, state)

    def test_normal_verified_tls_request_still_works(self):
        workspace, certificate, ca_path, server, worker, state = self._start_tls_fixture()
        client = None
        try:
            client_tls = ssl.create_default_context(cafile=str(ca_path))
            client = http.client.HTTPSConnection(
                "127.0.0.1",
                server.server_address[1],
                context=client_tls,
                timeout=3,
            )
            client.request("GET", "/openapi/v1.json")
            response = client.getresponse()
            self.assertEqual(response.status, 200)
            self.assertIn(b"openapi", response.read())
            self._assert_fixture_stopped(server, worker, state)
        finally:
            if client is not None:
                client.close()
            self._dispose_tls_fixture(workspace, certificate, server, worker, state)

    def test_powershell_helper_identity_is_journaled_for_recovery(self):
        context = {
            "children": [],
            "journal": {"processIdentities": []},
            "journalPath": Path("journal.json"),
            "journalTemporaryPath": Path("journal.json.new"),
        }
        process = ExitedProcess()

        with (
            patch.object(checks.subprocess, "Popen", return_value=process),
            patch.object(checks.subprocess, "CREATE_NEW_PROCESS_GROUP", 0, create=True),
            patch.object(checks, "process_start_ticks", return_value=456),
            patch.object(checks, "persist_journal") as persist_journal,
        ):
            output = checks.run_powershell(
                Path("pwsh.exe"),
                "safe command",
                context=context,
                role="recovery-root-removal",
            )

        self.assertEqual(output, "helper-result")
        self.assertEqual(len(context["journal"]["processIdentities"]), 1)
        self.assertEqual(context["journal"]["processIdentities"][0]["pid"], process.pid)
        self.assertEqual(context["journal"]["processIdentities"][0]["startTimeFiletime"], 456)
        self.assertEqual(context["journal"]["processIdentities"][0]["role"], "recovery-root-removal")
        self.assertEqual(context["journal"]["processIdentities"][0]["state"], "exited")
        persist_journal.assert_called()

    def test_cleanup_only_stops_recorded_powershell_helpers(self):
        if os.name != "nt":
            self.skipTest("Native Windows recovery is required.")
        folder = Path(__file__).resolve().parent / f".archive-recovery-{uuid.uuid4().hex}"
        workspace = folder / "workspace"
        workspace.mkdir(parents=True)
        output = folder / checks.SIDECAR_NAME
        account = checks.account_key(
            "https://127.0.0.1:43123/",
            "agentweaver-cli",
            "archive-audience",
            "https://127.0.0.1:43123",
            "archive-tenant",
        )
        target = checks.credential_target(account)
        local_app_data = folder / "local-app-data"
        state_directory, lock_path, marker_path = checks.account_state_paths(str(local_app_data), account)
        run_id = uuid.uuid4().hex
        helper_identity = {
            "pid": 91234,
            "startTimeFiletime": 998877,
            "role": "current-user-root-removal",
            "executable": "pwsh.exe",
            "state": "running",
        }
        paths = [
            workspace / "journal.json",
            workspace / "journal.json.new",
            workspace / "child-temp",
            workspace / "extracted",
            workspace / "fixtures",
            workspace / "certificates",
            workspace / "certificates" / "test-root.cer",
            workspace / "certificates" / "server.cer.pem",
            workspace / "certificates" / "server.key.pem",
        ]
        for path in paths[2:6]:
            path.mkdir()
        journal = {
            "schemaVersion": 1,
            "runId": run_id,
            "workspace": str(workspace),
            "workspaceIdentity": checks.file_identity(workspace),
            "logonSid": "S-1-5-21-1-2-3-1001",
            "controllerProcess": {"pid": 99999, "startTimeFiletime": 12345},
            "identity": {
                "issuer": "https://127.0.0.1:43123/",
                "clientId": "agentweaver-cli",
                "audience": "archive-audience",
                "gateway": "https://127.0.0.1:43123",
                "tenant": "archive-tenant",
                "scopes": ["openid", "api.read", "offline_access"],
                "projectId": None,
                "runId": None,
                "account": account,
                "credentialTarget": target,
                "lockPath": str(lock_path),
                "refreshMarkerPath": str(marker_path),
            },
            "credentialTarget": target,
            "nativeProbeTarget": "Agentweaver.Cli.ArchiveProbe.v1:" + run_id,
            "nativeProbeAcquisitionAttempted": False,
            "certificateThumbprint": None,
            "certificatePath": str(workspace / "certificates" / "test-root.cer"),
            "leafCertificatePath": str(workspace / "certificates" / "server.cer.pem"),
            "leafPrivateKeyPath": str(workspace / "certificates" / "server.key.pem"),
            "trustImportAttempted": False,
            "credentialSeedAttempted": False,
            "stableLockIdentity": None,
            "createdPaths": [str(path) for path in paths],
            "processIdentities": [helper_identity],
            "startedAt": "2026-11-02T00:00:00+00:00",
            "sourceCommit": "a" * 40,
            "sourceTree": "b" * 40,
        }
        (workspace / "journal.json").write_text(json.dumps(journal), encoding="utf-8")
        output.parent.mkdir(exist_ok=True)
        profile = json.dumps({
            "sid": journal["logonSid"],
            "localAppData": str(local_app_data),
        })
        terminated = []
        try:
            with (
                patch.object(checks, "powershell_executable", return_value=Path("pwsh.exe")),
                patch.object(checks, "run_powershell", return_value=profile),
                patch.object(checks, "process_is_alive", return_value=False),
                patch.object(
                    checks,
                    "terminate_owned_process",
                    side_effect=lambda pid, start: terminated.append((pid, start)) or True,
                ),
                patch.object(checks, "credential_read", return_value=None),
                patch.object(checks, "save_sidecar"),
            ):
                result = checks.cleanup_only(workspace, output)

            self.assertEqual(result, 1)
            self.assertIn((helper_identity["pid"], helper_identity["startTimeFiletime"]), terminated)
            self.assertFalse(workspace.exists())
        finally:
            shutil.rmtree(folder, ignore_errors=True)

    def test_extraction_failure_still_writes_a_failed_sidecar(self):
        folder = Path(__file__).resolve().parent / f".archive-extract-{uuid.uuid4().hex}"
        folder.mkdir()
        workspace = folder / "workspace"
        archive = folder / "candidate.zip"
        receipt_path = folder / "candidate.receipt.json"
        output_dir = folder / "output"
        output_dir.mkdir()
        output = output_dir / checks.SIDECAR_NAME
        archive.write_bytes(b"archive")
        receipt_path.write_bytes(b"receipt")
        source_commit = "c" * 40
        receipt = {
            "files": [{"path": "agentweaver.exe", "sha256": "d" * 64}],
            "version": "1.1.0",
            "source": {"commit": source_commit, "tree": "e" * 40},
            "runtimeTest": {"status": "passed"},
        }
        profile = {
            "sid": "S-1-5-21-1-2-3-1001",
            "localAppData": str(folder / "local-app-data"),
            "userProfile": str(folder),
            "appData": str(folder / "app-data"),
            "systemRoot": "C:\\Windows",
            "home": str(folder),
            "username": "fixture-user",
            "userDomain": "fixture-domain",
        }
        args = SimpleNamespace(
            output=output,
            source_commit=source_commit,
            directory=folder,
            workspace=workspace,
        )
        try:
            with (
                patch.object(
                    checks,
                    "preflight",
                    return_value={
                        "receipt": receipt,
                        "archive": archive,
                        "receiptPath": receipt_path,
                        "sourceTree": receipt["source"]["tree"],
                        "profile": profile,
                        "powershell": Path("pwsh.exe"),
                    },
                ),
                patch.object(checks, "reserve_loopback_port", return_value=43123),
                patch.object(checks, "assert_no_reparse_components"),
                patch.object(checks, "process_start_ticks", return_value=12345),
                patch.object(checks, "credential_read", return_value=None),
                patch.object(checks, "account_state_paths", side_effect=lambda _local, account: (
                    folder / "state",
                    folder / "state" / f"{account}.lock",
                    folder / "state" / f"{account}.refreshing",
                )),
                patch.object(checks, "secure_workspace", side_effect=lambda path, *_args, **_kwargs: path.mkdir()),
                patch.object(checks.package, "extract_archive", side_effect=checks.CheckError("simulated extraction failure")),
            ):
                result = checks.verify_and_run(args)

            sidecar = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual(result, 1)
            self.assertEqual(sidecar["controlledArchiveChecks"]["status"], "failed")
            self.assertIn("simulated extraction failure", sidecar["controlledArchiveChecks"]["reason"])
            self.assertIsNone(sidecar["executable"]["sha256Before"])
            self.assertIsNone(sidecar["executable"]["sha256After"])
            self.assertEqual(sidecar["nativeJourneyProof"]["status"], "not-run")
            self.assertEqual(sidecar["issue2449Acceptance"], "incomplete")
        finally:
            shutil.rmtree(folder, ignore_errors=True)


if __name__ == "__main__":
    unittest.main()
