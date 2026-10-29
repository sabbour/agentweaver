#!/usr/bin/env python3
"""Prepare and verify reproducible Agentweaver 1.x CLI candidate archives (Python 3.10+)."""

from __future__ import annotations

import argparse
import gzip
import hashlib
import io
import json
import os
import platform
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import unittest
import uuid
import zipfile
from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path, PurePosixPath
from threading import Thread
from typing import BinaryIO, Mapping


ROOT = Path(__file__).resolve().parents[2]
PROJECT = Path("tools") / "Agentweaver.Cli" / "Agentweaver.Cli.csproj"
RIDS = ("win-x64", "linux-x64", "osx-arm64")
SOURCE_REPOSITORY = "sabbour/agentweaver"
APPHOSTS = {"win-x64": "agentweaver.exe", "linux-x64": "agentweaver", "osx-arm64": "agentweaver"}
VERSION_PATTERN = re.compile(r"1\.\d+\.\d+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\Z")
COMMIT_PATTERN = re.compile(r"[0-9a-f]{40}\Z")


class PackageError(RuntimeError):
    pass


class CommandFailure(PackageError):
    def __init__(self, returncode: int, command: list[str]):
        super().__init__(f"Command failed with exit code {returncode}: {' '.join(command)}")
        self.returncode = returncode


@dataclass(frozen=True)
class ArchiveInfo:
    name: str
    format: str


ARCHIVES = {
    "win-x64": ArchiveInfo("agentweaver-cli-win-x64.zip", "zip"),
    "linux-x64": ArchiveInfo("agentweaver-cli-linux-x64.tar.gz", "tar.gz"),
    "osx-arm64": ArchiveInfo("agentweaver-cli-osx-arm64.tar.gz", "tar.gz"),
}


def archive_info(rid: str) -> ArchiveInfo:
    try:
        return ARCHIVES[rid]
    except KeyError as error:
        raise PackageError(f"Unsupported RID: {rid}") from error


def checked_run(command: list[str], cwd: Path, *, capture: bool = False) -> str:
    result = subprocess.run(
        command,
        cwd=cwd,
        check=False,
        text=True,
        capture_output=capture,
    )
    if result.returncode:
        if capture:
            if result.stdout:
                print(result.stdout, end="", file=sys.stderr)
            if result.stderr:
                print(result.stderr, end="", file=sys.stderr)
        raise CommandFailure(result.returncode, command)
    return result.stdout.strip() if capture else ""


def publish_arguments(project: Path, rid: str, output: Path, commit: str, tree: str) -> list[str]:
    return [
        "dotnet",
        "publish",
        str(project),
        "--configuration",
        "Release",
        "--no-restore",
        "-r",
        rid,
        "--self-contained",
        "true",
        "-p:UseAppHost=true",
        "-p:PublishTrimmed=false",
        "-p:PublishAot=false",
        f"-p:AgentweaverSourceSha={commit}",
        f"-p:AgentweaverSourceTree={tree}",
        "-o",
        str(output),
    ]


def restore_arguments(project: Path, commit: str, tree: str) -> list[str]:
    return [
        "dotnet",
        "restore",
        str(project),
        "--locked-mode",
        f"-p:AgentweaverSourceSha={commit}",
        f"-p:AgentweaverSourceTree={tree}",
    ]


def git_value(root: Path, revision: str) -> str:
    return checked_run(["git", "rev-parse", revision], root, capture=True)


def assert_build_sources_clean(root: Path) -> None:
    paths = [
        "tools/Agentweaver.Cli",
        "tools/Directory.Build.props",
        "tools/Directory.Build.targets",
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "NuGet.Config",
        "global.json",
        "scripts/cli-dotnet",
        ".github/workflows/v1-cli-package.yml",
    ]
    status = checked_run(
        ["git", "status", "--porcelain", "--untracked-files=all", "--", *paths],
        root,
        capture=True,
    )
    if status:
        raise PackageError("Build from a clean committed CLI, SDK, dependency-lock, and packaging source tree.")


def project_version(project: Path, root: Path) -> str:
    result = checked_run(["dotnet", "msbuild", str(project), "-getProperty:Version"], root, capture=True)
    version = result.splitlines()[-1].strip() if result else ""
    if not VERSION_PATTERN.fullmatch(version):
        raise PackageError(f"Expected an explicit Agentweaver 1.x project Version, got {version!r}.")
    return version


def output_directory(root: Path, rid: str) -> Path:
    candidate = root / "artifacts" / "cli" / rid
    relative = candidate.relative_to(root)
    cursor = root
    for part in relative.parts:
        cursor = cursor / part
        if cursor.is_symlink():
            raise PackageError(f"Refusing a symlink in the artifact output path: {cursor}")
    cursor.mkdir(parents=True, exist_ok=True)
    resolved = cursor.resolve()
    if not resolved.is_relative_to(root.resolve()):
        raise PackageError("Artifact output must stay under the repository artifacts/cli directory.")
    return resolved


def sha256_stream(stream: BinaryIO) -> tuple[int, str]:
    digest = hashlib.sha256()
    length = 0
    while chunk := stream.read(1024 * 1024):
        length += len(chunk)
        digest.update(chunk)
    return length, digest.hexdigest()


def file_metadata(name: str, source: Path | bytes) -> dict[str, object]:
    content = source if isinstance(source, bytes) else source.read_bytes()
    return {
        "path": name,
        "sizeBytes": len(content),
        "sha256": hashlib.sha256(content).hexdigest(),
    }


def archive_path(name: str) -> str:
    if "\\" in name:
        raise PackageError(f"Archive paths must use forward slashes: {name!r}")
    path = PurePosixPath(name)
    if path.is_absolute() or not path.parts or any(part in ("", ".", "..") or ":" in part for part in path.parts):
        raise PackageError(f"Unsafe archive path: {name!r}")
    return path.as_posix()


def validate_distribution_path(name: str) -> str:
    normalized = archive_path(name)
    parts = PurePosixPath(normalized).parts
    lower_parts = {part.lower() for part in parts}
    basename = parts[-1].lower()
    if lower_parts & {".git", "node_modules", "scripts", "tests", "tools"}:
        raise PackageError(f"Archive contains repository or Node tooling content: {normalized}")
    if basename in {"node", "node.exe", "npm", "npm.cmd", "dotnet", "dotnet.exe", "git", "git.exe", ".env"}:
        raise PackageError(f"Archive contains a development tool or environment file: {normalized}")
    if basename.startswith(".env.") or PurePosixPath(normalized).suffix.lower() in {".cs", ".csproj", ".slnx"}:
        raise PackageError(f"Archive contains source or environment content: {normalized}")
    return normalized


def file_mode(name: str, source: Path | bytes, rid: str) -> int:
    if name == APPHOSTS[rid]:
        return 0o755
    if isinstance(source, Path):
        mode = stat.S_IMODE(source.stat().st_mode) & 0o777
        return mode or 0o644
    return 0o644


def source_bytes(source: Path | bytes) -> bytes:
    return source if isinstance(source, bytes) else source.read_bytes()


def write_archive(stream: BinaryIO, rid: str, files: Mapping[str, Path | bytes]) -> None:
    info = archive_info(rid)
    normalized = {validate_distribution_path(name): source for name, source in files.items()}
    if len(normalized) != len(files) or len({name.casefold() for name in normalized}) != len(normalized) or APPHOSTS[rid] not in normalized:
        raise PackageError(f"Archive must include exactly one root apphost for {rid}.")
    if info.format == "zip":
        with zipfile.ZipFile(stream, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for name in sorted(normalized):
                entry = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
                entry.compress_type = zipfile.ZIP_DEFLATED
                entry.create_system = 3
                entry.external_attr = (stat.S_IFREG | file_mode(name, normalized[name], rid)) << 16
                archive.writestr(entry, source_bytes(normalized[name]), compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
        return

    with gzip.GzipFile(filename="", mode="wb", fileobj=stream, mtime=0, compresslevel=9) as compressed:
        with tarfile.open(fileobj=compressed, mode="w", format=tarfile.USTAR_FORMAT) as archive:
            for name in sorted(normalized):
                content = source_bytes(normalized[name])
                entry = tarfile.TarInfo(name)
                entry.size = len(content)
                entry.mode = file_mode(name, normalized[name], rid)
                entry.mtime = 0
                entry.uid = 0
                entry.gid = 0
                entry.uname = ""
                entry.gname = ""
                archive.addfile(entry, io.BytesIO(content))


def archive_inventory(stream: BinaryIO, rid: str) -> list[dict[str, object]]:
    entries: list[dict[str, object]] = []
    seen: set[str] = set()
    folded_names: set[str] = set()

    def add(name: str, content: BinaryIO, mode: int, regular: bool) -> None:
        name = validate_distribution_path(name)
        folded = name.casefold()
        if name in seen or folded in folded_names or not regular:
            raise PackageError(f"Archive contains duplicate or non-file entry: {name}")
        if name == APPHOSTS[rid] and mode & 0o111 != 0o111:
            raise PackageError(f"Archive apphost is not executable: {name}")
        size, digest = sha256_stream(content)
        entries.append({"path": name, "sizeBytes": size, "sha256": digest})
        seen.add(name)
        folded_names.add(folded)

    info = archive_info(rid)
    if info.format == "zip":
        with zipfile.ZipFile(stream) as archive:
            for item in archive.infolist():
                filemode = (item.external_attr >> 16) & 0xFFFF
                mode = filemode & 0o777
                with archive.open(item) as content:
                    add(item.filename, content, mode, not item.is_dir() and stat.S_ISREG(filemode))
    else:
        with tarfile.open(fileobj=stream, mode="r:gz") as archive:
            for item in archive.getmembers():
                content = archive.extractfile(item)
                if content is None:
                    raise PackageError(f"Archive entry has no file content: {item.name}")
                with content:
                    add(item.name, content, item.mode, item.isfile())
    entries.sort(key=lambda item: str(item["path"]))
    if APPHOSTS[rid] not in seen:
        raise PackageError(f"Archive is missing its root apphost: {APPHOSTS[rid]}")
    return entries


def readme(version: str, commit: str, tree: str, rid: str) -> bytes:
    return (
        f"""Agentweaver 1.x CLI {version}

Source commit: {commit}
Source tree: {tree}

Extract all archive files together and run {APPHOSTS[rid]} --help.
This self-contained CLI does not require Node.js or an installed .NET runtime.

Credential vault prerequisites:
- Windows: Windows Credential Manager.
- macOS: Keychain.
- Linux: glibc (not Alpine/musl), libsecret, and an available unlocked Secret Service session.
Authentication fails closed when the platform vault is unavailable; there is no plaintext fallback.

License and third-party notices:
See the Agentweaver source at https://github.com/{SOURCE_REPOSITORY}/tree/{commit} for its license.
Bundled runtime or dependency notices emitted by the .NET publish are included in this archive.
"""
    ).encode("utf-8")


def publish_files(publish_dir: Path, rid: str, version: str, commit: str, tree: str, root: Path) -> dict[str, Path | bytes]:
    files: dict[str, Path | bytes] = {}
    for path in sorted(publish_dir.rglob("*")):
        if path.is_symlink():
            raise PackageError(f"Publish output contains a symlink: {path}")
        if path.is_file():
            relative = path.relative_to(publish_dir).as_posix()
            files[archive_path(relative)] = path
    if APPHOSTS[rid] not in files:
        raise PackageError(f"Self-contained publish did not produce {APPHOSTS[rid]}.")

    files["README.txt"] = readme(version, commit, tree, rid)
    for name in ("LICENSE", "LICENSE.txt", "NOTICE", "THIRD-PARTY-NOTICES.txt"):
        source = root / name
        if source.is_file() and not source.is_symlink():
            if name in files:
                raise PackageError(f"License notice collides with publish output: {name}")
            files[name] = source
    return files


def runtime_rid() -> str | None:
    machine = platform.machine().lower()
    if os.name == "nt" and machine in ("amd64", "x86_64"):
        return "win-x64"
    if sys.platform.startswith("linux") and machine in ("amd64", "x86_64"):
        return "linux-x64"
    if sys.platform == "darwin" and machine in ("arm64", "aarch64"):
        return "osx-arm64"
    return None


class OpenApiFixture(ThreadingHTTPServer):
    def __init__(self) -> None:
        self.requests: list[str] = []
        super().__init__(("127.0.0.1", 0), OpenApiHandler)
        self.daemon_threads = True


class OpenApiHandler(BaseHTTPRequestHandler):
    def do_GET(self) -> None:
        self.server.requests.append(self.path)  # type: ignore[attr-defined]
        body = json.dumps({
            "openapi": "3.1.0",
            "info": {"title": "CLI package smoke fixture", "version": "1.0.0"},
            "components": {"securitySchemes": {"Bearer": {"type": "http", "scheme": "bearer"}}},
            "security": [{"Bearer": []}],
            "paths": {
                "/api/v1/package-smoke": {
                    "get": {
                        "operationId": "packageSmoke",
                        "responses": {"200": {"description": "controlled response"}},
                    }
                }
            },
        }).encode("utf-8")
        if self.path != "/openapi/v1.json":
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, _format: str, *_args: object) -> None:
        return


def smoke_environment(gateway_url: str) -> dict[str, str]:
    environment = {"PATH": "", "AGENTWEAVER_GATEWAY_URL": gateway_url}
    for name in ("SYSTEMROOT", "WINDIR", "HOME"):
        value = os.environ.get(name)
        if value:
            environment[name] = value
    return environment


def external_working_directory(root: Path) -> Path:
    candidate = Path.home().resolve()
    if candidate == root.resolve() or root.resolve() in candidate.parents:
        raise PackageError("CLI smoke working directory must be outside the source checkout.")
    return candidate


def external_smoke_executable(executable: Path, root: Path) -> Path:
    candidate = executable.resolve()
    if candidate.is_relative_to(root.resolve()):
        raise PackageError("CLI smoke executable must be outside the source checkout.")
    return candidate


def run_smoke(executable: Path, rid: str, version: str, commit: str, tree: str, root: Path) -> None:
    if runtime_rid() != rid:
        raise PackageError(f"Runtime smoke for {rid} requires a native {rid} runner.")
    executable = external_smoke_executable(executable, root)
    cwd = external_working_directory(root)
    with OpenApiFixture() as server:
        worker = Thread(target=server.serve_forever, daemon=True)
        worker.start()
        gateway_url = f"http://127.0.0.1:{server.server_port}"
        environment = smoke_environment(gateway_url)
        try:
            for arguments, expected in (
                (["--help"], ("api operations", "auth login", "test")),
                (["--version"], ("Agentweaver 1.x", version, commit[:12], tree[:12])),
                (["api", "operations"], ("packageSmoke",)),
            ):
                try:
                    result = subprocess.run(
                        [str(executable), *arguments],
                        cwd=cwd,
                        env=environment,
                        check=False,
                        text=True,
                        capture_output=True,
                        timeout=45,
                    )
                except subprocess.TimeoutExpired as error:
                    raise PackageError(f"CLI smoke {arguments!r} timed out after 45 seconds.") from error
                output = result.stdout + result.stderr
                if result.returncode:
                    raise PackageError(f"CLI smoke {arguments!r} exited {result.returncode}: {output}")
                missing = [part for part in expected if part not in output]
                if missing:
                    raise PackageError(f"CLI smoke {arguments!r} omitted {missing!r}: {output}")
        finally:
            server.shutdown()
            worker.join(timeout=5)
        if "/openapi/v1.json" not in server.requests:
            raise PackageError("CLI smoke did not discover the controlled Gateway OpenAPI document.")


def extract_archive(archive_path: Path, rid: str, target: Path) -> Path:
    target.mkdir(parents=True, exist_ok=False)
    info = archive_info(rid)
    with archive_path.open("rb") as stream:
        if info.format == "zip":
            with zipfile.ZipFile(stream) as archive:
                for item in archive.infolist():
                    name = validate_distribution_path(item.filename)
                    if item.is_dir():
                        continue
                    destination = target.joinpath(*PurePosixPath(name).parts)
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    with archive.open(item) as source, destination.open("wb") as output:
                        shutil.copyfileobj(source, output)
                    destination.chmod((item.external_attr >> 16) & 0o777)
        else:
            with tarfile.open(fileobj=stream, mode="r:gz") as archive:
                for item in archive.getmembers():
                    name = validate_distribution_path(item.name)
                    if item.isdir():
                        continue
                    if not item.isfile():
                        raise PackageError(f"Refusing non-file archive entry: {name}")
                    source = archive.extractfile(item)
                    if source is None:
                        raise PackageError(f"Archive entry has no file content: {name}")
                    destination = target.joinpath(*PurePosixPath(name).parts)
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    with source, destination.open("wb") as output:
                        shutil.copyfileobj(source, output)
                    destination.chmod(item.mode & 0o777)
    executable = target / APPHOSTS[rid]
    if not executable.is_file():
        raise PackageError(f"Extracted archive is missing {APPHOSTS[rid]}.")
    return executable


def write_bytes(path: Path, content: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("wb") as stream:
        stream.write(content)


def checksum_lines(archive: Path, receipt: Path) -> str:
    lines = []
    for path in (archive, receipt):
        with path.open("rb") as stream:
            _, digest = sha256_stream(stream)
        lines.append(f"{digest}  {path.name}")
    return "\n".join(lines) + "\n"


def build_package(rid: str, smoke: bool = False, root: Path = ROOT) -> dict[str, object]:
    info = archive_info(rid)
    project = root / PROJECT
    if project.is_symlink() or not project.resolve().is_relative_to(root.resolve()) or not project.is_file():
        raise PackageError(f"CLI project is not present at {project}.")
    out = output_directory(root, rid)
    work = out / f".work-{uuid.uuid4().hex}"
    work.mkdir()
    try:
        assert_build_sources_clean(root)
        commit = git_value(root, "HEAD")
        tree = git_value(root, "HEAD^{tree}")
        if not COMMIT_PATTERN.fullmatch(commit) or not COMMIT_PATTERN.fullmatch(tree):
            raise PackageError("Git did not return valid source commit/tree hashes.")
        version = project_version(project, root)
        sdk_version = checked_run(["dotnet", "--version"], root, capture=True)
        if not sdk_version.startswith("10."):
            raise PackageError(f"Packaging requires the .NET 10 SDK selected by global.json; found {sdk_version}.")

        publish_dir = work / "publish"
        checked_run(restore_arguments(project, commit, tree), root)
        checked_run(publish_arguments(project, rid, publish_dir, commit, tree), root)
        if git_value(root, "HEAD") != commit or git_value(root, "HEAD^{tree}") != tree:
            raise PackageError("The source commit/tree changed during publish.")
        assert_build_sources_clean(root)
        files = publish_files(publish_dir, rid, version, commit, tree, root)
        staged_archive = work / info.name
        with staged_archive.open("wb") as stream:
            write_archive(stream, rid, files)
        expected_files = [file_metadata(name, source) for name, source in sorted(files.items())]
        with staged_archive.open("rb") as stream:
            if archive_inventory(stream, rid) != expected_files:
                raise PackageError("The prepared archive does not match the publish-file inventory.")
        runtime_test: dict[str, object] = {"status": "not-run", "hostRid": runtime_rid(), "tests": []}
        if smoke:
            with tempfile.TemporaryDirectory(prefix="agentweaver-cli-smoke-") as smoke_workspace:
                extracted = Path(smoke_workspace).resolve()
                if extracted.is_relative_to(root.resolve()):
                    raise PackageError("CLI smoke extraction directory must be outside the source checkout.")
                executable = extract_archive(staged_archive, rid, extracted / "archive")
                run_smoke(executable, rid, version, commit, tree, root)
            runtime_test = {
                "status": "passed",
                "hostRid": rid,
                "tests": ["help", "version", "gateway-openapi-discovery"],
            }

        file_records = expected_files
        with staged_archive.open("rb") as stream:
            archive_length, archive_hash = sha256_stream(stream)
        receipt = {
            "schemaVersion": 1,
            "product": "Agentweaver 1.x CLI",
            "version": version,
            "source": {"repository": SOURCE_REPOSITORY, "commit": commit, "tree": tree},
            "sdkVersion": sdk_version,
            "rid": rid,
            "archive": {"name": info.name, "sizeBytes": archive_length, "sha256": archive_hash},
            "files": file_records,
            "runtimeTest": runtime_test,
        }
        archive = out / info.name
        receipt_path = out / f"{info.name}.receipt.json"
        sums_path = out / "SHA256SUMS"
        staged_receipt = work / receipt_path.name
        staged_sums = work / "SHA256SUMS"
        write_bytes(staged_receipt, (json.dumps(receipt, indent=2, sort_keys=True) + "\n").encode())
        os.replace(staged_archive, archive)
        os.replace(staged_receipt, receipt_path)
        write_bytes(staged_sums, checksum_lines(archive, receipt_path).encode())
        os.replace(staged_sums, sums_path)
        return receipt
    finally:
        if work.exists():
            resolved = work.resolve()
            if resolved.parent != out.resolve() or not resolved.name.startswith(".work-"):
                raise PackageError("Refusing to remove a workspace not created by this invocation.")
            shutil.rmtree(resolved)


def verify_receipt(
    receipt: object,
    stream: BinaryIO,
    rid: str,
    *,
    source_commit: str | None = None,
    source_tree: str | None = None,
    require_smoke: bool = False,
) -> dict[str, object]:
    if not isinstance(receipt, dict) or receipt.get("schemaVersion") != 1 or receipt.get("rid") != rid or receipt.get("product") != "Agentweaver 1.x CLI":
        raise PackageError(f"Receipt identity mismatch for {rid}.")
    version = receipt.get("version")
    source = receipt.get("source")
    if not isinstance(source, dict):
        raise PackageError(f"Missing source pin in the {rid} receipt.")
    commit, tree = source.get("commit"), source.get("tree")
    if not isinstance(version, str) or not VERSION_PATTERN.fullmatch(version):
        raise PackageError(f"Invalid CLI version in the {rid} receipt.")
    if not isinstance(commit, str) or not COMMIT_PATTERN.fullmatch(commit) or not isinstance(tree, str) or not COMMIT_PATTERN.fullmatch(tree):
        raise PackageError(f"Invalid source pin in the {rid} receipt.")
    if source.get("repository") != SOURCE_REPOSITORY or (source_commit and commit != source_commit):
        raise PackageError(f"Source repository/commit mismatch for {rid}.")
    if source_tree and tree != source_tree:
        raise PackageError(f"Source tree mismatch for {rid}.")
    sdk_version = receipt.get("sdkVersion")
    if not isinstance(sdk_version, str) or not sdk_version.startswith("10."):
        raise PackageError(f"Receipt does not identify a .NET 10 SDK for {rid}.")
    metadata = receipt.get("archive")
    if not isinstance(metadata, dict):
        raise PackageError(f"Missing archive metadata for {rid}.")
    stream.seek(0)
    size, digest = sha256_stream(stream)
    info = archive_info(rid)
    if metadata != {"name": info.name, "sizeBytes": size, "sha256": digest}:
        raise PackageError(f"Archive size or SHA-256 mismatch for {rid}.")
    stream.seek(0)
    try:
        actual_files = archive_inventory(stream, rid)
    except (OSError, EOFError, tarfile.TarError, zipfile.BadZipFile) as error:
        raise PackageError(f"Invalid {rid} archive: {error}") from error
    files = receipt.get("files")
    if not isinstance(files, list) or files != actual_files:
        raise PackageError(f"Archive content does not match the contained-file hashes for {rid}.")
    runtime_test = receipt.get("runtimeTest", {})
    if require_smoke:
        expected_tests = ["help", "version", "gateway-openapi-discovery"]
        if (
            not isinstance(runtime_test, dict)
            or runtime_test.get("status") != "passed"
            or runtime_test.get("hostRid") != rid
            or runtime_test.get("tests") != expected_tests
        ):
            raise PackageError(f"{rid} has no complete passing native executable smoke result.")
    return receipt


def verify_one(directory: Path, rid: str, *, source_commit: str | None = None, require_smoke: bool = False) -> dict[str, object]:
    info = archive_info(rid)
    archive = directory / info.name
    receipt_path = directory / f"{info.name}.receipt.json"
    sums_path = directory / "SHA256SUMS"
    if not archive.is_file() or not receipt_path.is_file() or not sums_path.is_file():
        raise PackageError(f"Incomplete {rid} candidate in {directory}.")
    try:
        receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise PackageError(f"Invalid receipt for {rid}: {error}") from error
    expected_tree = None
    if source_commit:
        if not COMMIT_PATTERN.fullmatch(source_commit):
            raise PackageError("Expected source commit must be a full Git SHA.")
        expected_tree = git_value(ROOT, f"{source_commit}^{{tree}}")
    with archive.open("rb") as stream:
        verify_receipt(
            receipt,
            stream,
            rid,
            source_commit=source_commit,
            source_tree=expected_tree,
            require_smoke=require_smoke,
        )
    expected_sums = checksum_lines(archive, receipt_path)
    if sums_path.read_text(encoding="utf-8") != expected_sums:
        raise PackageError(f"SHA256SUMS does not match the {rid} candidate files.")
    return receipt


def verify_all(root: Path, *, source_commit: str | None = None, require_smoke: bool = False, write_checksums: bool = False) -> list[dict[str, object]]:
    receipts = [verify_one(root / rid, rid, source_commit=source_commit, require_smoke=require_smoke) for rid in RIDS]
    source_pins = {(item["source"]["commit"], item["source"]["tree"], item["version"], item["sdkVersion"]) for item in receipts}
    if len(source_pins) != 1:
        raise PackageError("Candidate archives do not share one source pin, product version, and SDK.")
    if write_checksums:
        lines = []
        for rid in RIDS:
            directory = root / rid
            for name in (ARCHIVES[rid].name, f"{ARCHIVES[rid].name}.receipt.json"):
                path = directory / name
                with path.open("rb") as stream:
                    _, digest = sha256_stream(stream)
                lines.append(f"{digest}  {rid}/{name}")
        write_bytes(root / "SHA256SUMS", ("\n".join(lines) + "\n").encode())
    return receipts


def run_tests() -> int:
    sys.dont_write_bytecode = True
    suite = unittest.defaultTestLoader.discover(str(Path(__file__).parent / "tests"), pattern="test_*.py")
    return 0 if unittest.TextTestRunner(verbosity=2).run(suite).wasSuccessful() else 1


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    build = commands.add_parser("build", help="restore, publish, and archive one RID")
    build.add_argument("--rid", choices=RIDS, required=True)
    build.add_argument("--smoke", action="store_true", help="execute the archive on a matching native host")
    verify = commands.add_parser("verify", help="verify an archive or the full candidate set")
    group = verify.add_mutually_exclusive_group(required=True)
    group.add_argument("--rid", choices=RIDS)
    group.add_argument("--all", action="store_true")
    verify.add_argument("--source-commit")
    verify.add_argument("--require-smoke", action="store_true")
    verify.add_argument("--write-checksums", action="store_true")
    commands.add_parser("test", help="run the packaging helper's focused tests")
    args = parser.parse_args(argv)
    try:
        if args.command == "build":
            receipt = build_package(args.rid, smoke=args.smoke)
            print(f"Prepared {receipt['archive']['name']} ({receipt['archive']['sha256']})")
            return 0
        if args.command == "verify":
            if args.all:
                receipts = verify_all(ROOT / "artifacts" / "cli", source_commit=args.source_commit, require_smoke=args.require_smoke, write_checksums=args.write_checksums)
                print(f"Verified {len(receipts)} candidate archives.")
            else:
                verify_one(ROOT / "artifacts" / "cli" / args.rid, args.rid, source_commit=args.source_commit, require_smoke=args.require_smoke)
                print(f"Verified {args.rid} candidate archive.")
            return 0
        return run_tests()
    except CommandFailure as error:
        print(str(error), file=sys.stderr)
        return error.returncode
    except (PackageError, OSError, ValueError) as error:
        print(f"Packaging error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
