#!/usr/bin/env python3
"""Run controlled checks against the exact extracted Windows CLI archive."""

from __future__ import annotations

import argparse
import base64
import ctypes
import datetime as dt
import hashlib
import hmac
import http.server
import json
import os
import platform
import secrets
import shutil
import socket
import ssl
import subprocess
import sys
import threading
import time
import urllib.parse
import uuid
from pathlib import Path
from typing import Any


sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
import package


SIDECAR_NAME = "windows-controlled-archive-checks.json"
HELP_EXPECTED = ("api operations", "auth login|status|logout", "test")
CREDENTIAL_SERVICE = "Agentweaver.Cli.OAuth.v1"
CREDENTIAL_TYPE_GENERIC = 1
CREDENTIAL_PERSIST_LOCAL_MACHINE = 2
ERROR_NOT_FOUND = 1168
MAXIMUM_BLOB_BYTES = 5 * 512
FILE_ATTRIBUTE_REPARSE_POINT = 0x400
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
PROCESS_TERMINATE = 0x0001
SYNCHRONIZE = 0x00100000
WAIT_OBJECT_0 = 0
WAIT_TIMEOUT = 258
POWERSHELL_DIAGNOSTIC_ROLES = frozenset({
    "ephemeral-certificate-generation",
    "workspace-acl",
    "current-user-root-removal",
    "current-user-root-precheck",
    "current-user-root-import",
    "recovery-profile-check",
    "recovery-current-user-root-removal",
})


class CheckError(RuntimeError):
    pass


class BlockedError(CheckError):
    pass


class FILETIME(ctypes.Structure):
    _fields_ = [("low", ctypes.c_uint32), ("high", ctypes.c_uint32)]


class CREDENTIALW(ctypes.Structure):
    _fields_ = [
        ("flags", ctypes.c_uint32),
        ("type", ctypes.c_uint32),
        ("target_name", ctypes.c_wchar_p),
        ("comment", ctypes.c_wchar_p),
        ("last_written", FILETIME),
        ("credential_blob_size", ctypes.c_uint32),
        ("credential_blob", ctypes.POINTER(ctypes.c_ubyte)),
        ("persist", ctypes.c_uint32),
        ("attribute_count", ctypes.c_uint32),
        ("attributes", ctypes.c_void_p),
        ("target_alias", ctypes.c_wchar_p),
        ("user_name", ctypes.c_wchar_p),
    ]


class FIXTURE_STATE:
    def __init__(self, origin: str, tenant: str, actor: str, client_id: str) -> None:
        self.origin = origin
        self.issuer = origin + "/"
        self.tenant = tenant
        self.actor = actor
        self.client_id = client_id
        self.current_refresh: str | None = None
        self.current_access: str | None = None
        self.secrets: set[str] = set()
        self.calls: list[dict[str, Any]] = []
        self.assertions: dict[str, bool] = {
            "refreshGrantAndClientAccepted": True,
            "refreshTokensRotatedOnce": True,
            "authorizationContextAuthenticated": True,
            "operationRequestsAuthenticated": True,
            "noAuthorizationBrowserRequest": True,
            "unexpectedFixtureRequest": True,
        }
        self.active_threads: set[int] = set()
        self.lock = threading.Lock()
        self.thread_changed = threading.Condition(self.lock)

    def record(self, method: str, path: str, status: int) -> None:
        safe_path = urllib.parse.urlsplit(path).path
        with self.lock:
            self.calls.append({"method": method, "path": safe_path, "status": status})

    def update_assertion(self, name: str, value: bool) -> None:
        with self.lock:
            self.assertions[name] = self.assertions[name] and value

    def record_thread(self, ident: int, active: bool) -> None:
        with self.thread_changed:
            if active:
                self.active_threads.add(ident)
            else:
                self.active_threads.discard(ident)
                self.thread_changed.notify_all()

    def calls_snapshot(self) -> list[dict[str, Any]]:
        with self.lock:
            return [dict(call) for call in self.calls]

    def assertions_snapshot(self) -> dict[str, bool]:
        with self.lock:
            return dict(self.assertions)


def _advapi() -> Any:
    if os.name != "nt":
        raise BlockedError("Windows Credential Manager is available only on Windows.")
    dll = ctypes.WinDLL("Advapi32.dll", use_last_error=True)
    dll.CredReadW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.POINTER(ctypes.c_void_p)]
    dll.CredReadW.restype = ctypes.c_int
    dll.CredWriteW.argtypes = [ctypes.POINTER(CREDENTIALW), ctypes.c_uint32]
    dll.CredWriteW.restype = ctypes.c_int
    dll.CredDeleteW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32]
    dll.CredDeleteW.restype = ctypes.c_int
    dll.CredFree.argtypes = [ctypes.c_void_p]
    dll.CredFree.restype = None
    return dll


def credential_read(target: str) -> str | None:
    api = _advapi()
    native = ctypes.c_void_p()
    if not api.CredReadW(target, CREDENTIAL_TYPE_GENERIC, 0, ctypes.byref(native)):
        error = ctypes.get_last_error()
        if error == ERROR_NOT_FOUND:
            return None
        raise CheckError(f"Credential Manager read failed with Win32 error {error}.")
    try:
        value = ctypes.cast(native, ctypes.POINTER(CREDENTIALW)).contents
        if (
            value.credential_blob is None
            or value.credential_blob_size == 0
            or value.credential_blob_size > MAXIMUM_BLOB_BYTES
        ):
            raise CheckError("Credential Manager returned an invalid fixture entry.")
        raw = bytearray(ctypes.string_at(value.credential_blob, value.credential_blob_size))
        try:
            return raw.decode("utf-8", errors="strict")
        except UnicodeDecodeError as error:
            raise CheckError("Credential Manager returned a non-UTF-8 fixture entry.") from error
        finally:
            raw[:] = b"\0" * len(raw)
    finally:
        api.CredFree(native)


def credential_write(target: str, value: str) -> None:
    raw = bytearray(value.encode("utf-8", errors="strict"))
    if not raw or len(raw) > MAXIMUM_BLOB_BYTES:
        raw[:] = b"\0" * len(raw)
        raise CheckError("Synthetic credential exceeds the Windows vault limit.")
    target_buffer = ctypes.create_unicode_buffer(target)
    blob = ctypes.create_string_buffer(len(raw))
    ctypes.memmove(blob, bytes(raw), len(raw))
    native = CREDENTIALW(
        0,
        CREDENTIAL_TYPE_GENERIC,
        ctypes.cast(target_buffer, ctypes.c_wchar_p),
        None,
        FILETIME(0, 0),
        len(raw),
        ctypes.cast(blob, ctypes.POINTER(ctypes.c_ubyte)),
        CREDENTIAL_PERSIST_LOCAL_MACHINE,
        0,
        None,
        None,
        None,
    )
    try:
        if not _advapi().CredWriteW(ctypes.byref(native), 0):
            raise CheckError(f"Credential Manager write failed with Win32 error {ctypes.get_last_error()}.")
    finally:
        ctypes.memset(blob, 0, len(raw))
        raw[:] = b"\0" * len(raw)


def credential_delete(target: str) -> None:
    api = _advapi()
    if api.CredDeleteW(target, CREDENTIAL_TYPE_GENERIC, 0):
        return
    error = ctypes.get_last_error()
    if error != ERROR_NOT_FOUND:
        raise CheckError(f"Credential Manager delete failed with Win32 error {error}.")


def has_reparse_attribute(path: Path) -> bool:
    if os.name != "nt":
        return path.is_symlink()
    kernel = ctypes.WinDLL("Kernel32.dll", use_last_error=True)
    kernel.GetFileAttributesW.argtypes = [ctypes.c_wchar_p]
    kernel.GetFileAttributesW.restype = ctypes.c_uint32
    attributes = kernel.GetFileAttributesW(str(path))
    return attributes != 0xFFFFFFFF and bool(attributes & FILE_ATTRIBUTE_REPARSE_POINT)


def assert_no_reparse_components(path: Path, *, include_missing: bool = True) -> None:
    full = Path(os.path.abspath(path))
    chain = [full, *full.parents]
    for candidate in reversed(chain):
        if candidate.exists() or candidate.is_symlink():
            if has_reparse_attribute(candidate):
                raise BlockedError(f"Refusing a path with a reparse point: {candidate}")
        elif not include_missing:
            continue


def reject_source_checkout_path(path: Path, roots: list[Path]) -> None:
    candidate = path.resolve(strict=False)
    for root in roots:
        base = root.resolve()
        if candidate == base or base in candidate.parents:
            raise BlockedError("The workspace must be outside every source checkout.")


def git_worktree_roots(root: Path) -> list[Path]:
    result = subprocess.run(
        ["git", "-C", str(root), "worktree", "list", "--porcelain"],
        check=False,
        capture_output=True,
        text=True,
        timeout=5,
    )
    if result.returncode:
        raise BlockedError("Git could not enumerate the source worktrees.")
    return [
        Path(line.removeprefix("worktree ")).resolve()
        for line in result.stdout.splitlines()
        if line.startswith("worktree ")
    ]


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds")


def process_start_ticks(pid: int) -> int:
    if os.name != "nt":
        raise BlockedError("Owned-process identity is supported only on Windows.")
    kernel = ctypes.WinDLL("Kernel32.dll", use_last_error=True)
    kernel.OpenProcess.argtypes = [ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32]
    kernel.OpenProcess.restype = ctypes.c_void_p
    kernel.GetProcessTimes.argtypes = [
        ctypes.c_void_p,
        ctypes.POINTER(FILETIME),
        ctypes.POINTER(FILETIME),
        ctypes.POINTER(FILETIME),
        ctypes.POINTER(FILETIME),
    ]
    kernel.GetProcessTimes.restype = ctypes.c_int
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    kernel.CloseHandle.restype = ctypes.c_int
    handle = kernel.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid)
    if not handle:
        raise CheckError(f"Could not inspect owned process {pid}.")
    try:
        created, exited, kernel_time, user_time = FILETIME(), FILETIME(), FILETIME(), FILETIME()
        if not kernel.GetProcessTimes(
            handle,
            ctypes.byref(created),
            ctypes.byref(exited),
            ctypes.byref(kernel_time),
            ctypes.byref(user_time),
        ):
            raise CheckError(f"Could not identify owned process {pid}.")
        return (created.high << 32) | created.low
    finally:
        kernel.CloseHandle(handle)


def track_child(context: dict[str, Any], process: subprocess.Popen[str], role: str, executable: str) -> dict[str, Any]:
    identity = {
        "pid": process.pid,
        "startTimeFiletime": process_start_ticks(process.pid),
        "role": role,
        "executable": executable,
        "state": "running",
    }
    context["children"].append((process, identity))
    context["journal"]["processIdentities"].append(identity)
    persist_journal(context)
    return identity


def stop_child(process: subprocess.Popen[str], label: str) -> None:
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired as error:
                raise CheckError(f"Owned process {process.pid} ({label}) could not be stopped.") from error
    if process.poll() is None:
        raise CheckError(f"Owned process {process.pid} ({label}) could not be stopped.")


def stop_owned_children(context: dict[str, Any], *, persist: bool = True) -> list[str]:
    errors: list[str] = []
    for process, identity in reversed(context.get("children", [])):
        try:
            stop_child(process, str(identity["role"]))
            identity["state"] = "exited"
            identity["exitCode"] = process.returncode
        except Exception:
            identity["state"] = "unresolved"
            errors.append("owned-cli-or-helper-process-did-not-stop")
    if persist:
        try:
            persist_journal(context)
        except Exception:
            errors.append("recovery-journal-update-failed")
    return list(dict.fromkeys(errors))


def file_identity(path: Path) -> dict[str, int]:
    info = path.stat(follow_symlinks=False)
    return {"device": int(info.st_dev), "fileId": int(info.st_ino)}


def process_is_alive(pid: int, start_ticks: int) -> bool:
    try:
        return process_start_ticks(pid) == start_ticks
    except CheckError:
        error = ctypes.get_last_error()
        if error in (87, 1168):
            return False
        if error == 5:
            raise CheckError(f"Could not verify process identity {pid}; recovery is unresolved.")
        return False


def terminate_owned_process(pid: int, start_ticks: int) -> bool:
    kernel = ctypes.WinDLL("Kernel32.dll", use_last_error=True)
    kernel.OpenProcess.argtypes = [ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32]
    kernel.OpenProcess.restype = ctypes.c_void_p
    kernel.GetProcessTimes.argtypes = [
        ctypes.c_void_p,
        ctypes.POINTER(FILETIME),
        ctypes.POINTER(FILETIME),
        ctypes.POINTER(FILETIME),
        ctypes.POINTER(FILETIME),
    ]
    kernel.GetProcessTimes.restype = ctypes.c_int
    kernel.TerminateProcess.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
    kernel.TerminateProcess.restype = ctypes.c_int
    kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
    kernel.WaitForSingleObject.restype = ctypes.c_uint32
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    kernel.CloseHandle.restype = ctypes.c_int
    handle = kernel.OpenProcess(
        PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE | SYNCHRONIZE,
        0,
        pid,
    )
    if not handle:
        error = ctypes.get_last_error()
        if error == 87:
            return False
        raise CheckError(f"Could not open owned process {pid} for recovery (Win32 error {error}).")
    try:
        created, exited, kernel_time, user_time = FILETIME(), FILETIME(), FILETIME(), FILETIME()
        if not kernel.GetProcessTimes(
            handle,
            ctypes.byref(created),
            ctypes.byref(exited),
            ctypes.byref(kernel_time),
            ctypes.byref(user_time),
        ):
            raise CheckError(f"Could not verify process identity {pid} during recovery.")
        actual = (created.high << 32) | created.low
        if actual != start_ticks:
            return False
        if not kernel.TerminateProcess(handle, 1):
            error = ctypes.get_last_error()
            if error not in (5, 87):
                raise CheckError(f"Could not stop owned process {pid} (Win32 error {error}).")
        wait = kernel.WaitForSingleObject(handle, 5000)
        if wait != WAIT_OBJECT_0:
            raise CheckError(f"Owned process {pid} did not stop within five seconds.")
        return True
    finally:
        kernel.CloseHandle(handle)


def account_key(issuer: str, client_id: str, audience: str, gateway: str, tenant: str) -> str:
    material = json.dumps(
        [issuer, client_id, audience, gateway, tenant, "api.read offline_access openid", None, None],
        ensure_ascii=True,
        separators=(",", ":"),
    ).encode("utf-8")
    return "agentweaver-v1-" + hashlib.sha256(material).hexdigest()


def credential_target(account: str) -> str:
    return f"{CREDENTIAL_SERVICE}:{account}"


def account_state_paths(local_app_data: str, account: str) -> tuple[Path, Path, Path]:
    directory = Path(local_app_data) / "Agentweaver" / "cli-auth-locks"
    return directory, directory / f"{account}.lock", directory / f"{account}.refreshing"


def powershell_executable() -> Path:
    for name in ("pwsh.exe", "powershell.exe"):
        located = shutil.which(name)
        if located:
            return Path(located).resolve()
    system_root = os.environ.get("SystemRoot", r"C:\Windows")
    for candidate in (
        Path(system_root) / "System32" / "WindowsPowerShell" / "v1.0" / "powershell.exe",
        Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "PowerShell" / "7" / "pwsh.exe",
    ):
        if candidate.is_file():
            return candidate.resolve()
    raise BlockedError("PowerShell with .NET certificate support is unavailable.")


def powershell_env(temp_root: Path | None = None) -> dict[str, str]:
    names = (
        "SystemRoot", "WINDIR", "USERPROFILE", "LOCALAPPDATA", "APPDATA",
        "HOMEDRIVE", "HOMEPATH", "USERNAME", "USERDOMAIN", "COMPUTERNAME",
        "PSModulePath",
    )
    environment = {name: os.environ[name] for name in names if os.environ.get(name)}
    environment["PATH"] = ""
    environment["NO_PROXY"] = "127.0.0.1,localhost"
    environment["no_proxy"] = "127.0.0.1,localhost"
    if temp_root is not None:
        environment["TEMP"] = str(temp_root)
        environment["TMP"] = str(temp_root)
    elif os.environ.get("TEMP"):
        environment["TEMP"] = os.environ["TEMP"]
        environment["TMP"] = os.environ.get("TMP", os.environ["TEMP"])
    return environment


def _powershell_command(executable: Path, script: str) -> list[str]:
    encoded = base64.b64encode(script.encode("utf-16le")).decode("ascii")
    return [str(executable), "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded]


PREFLIGHT_SCRIPT = r"""
$ErrorActionPreference = 'Stop'
$root = [System.Security.Cryptography.X509Certificates.X509Store]::new(
  [System.Security.Cryptography.X509Certificates.StoreName]::Root,
  [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
$root.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
$root.Close()
$uri = [Uri]$env:ARCHIVE_FIXTURE_URI
$proxy = [System.Net.Http.HttpClient]::DefaultProxy
$proxyBypassed = $proxy.IsBypassed($uri)
$proxyTarget = $proxy.GetProxy($uri)
$proxyTargetUri = $null
if ($null -ne $proxyTarget) { $proxyTargetUri = $proxyTarget.AbsoluteUri }
$output = [ordered]@{
  sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
  psMajor = $PSVersionTable.PSVersion.Major
  osBuild = [Environment]::OSVersion.Version.ToString()
  localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
  userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
  appData = [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)
  systemRoot = [Environment]::GetEnvironmentVariable('SystemRoot')
  home = [Environment]::GetEnvironmentVariable('USERPROFILE')
  username = [Environment]::GetEnvironmentVariable('USERNAME')
  userDomain = [Environment]::GetEnvironmentVariable('USERDOMAIN')
  computerName = [Environment]::GetEnvironmentVariable('COMPUTERNAME')
  proxyBypassed = $proxyBypassed
  proxyTarget = $proxyTargetUri
}
$output | ConvertTo-Json -Compress
"""


CERTIFICATE_SCRIPT = r"""
$ErrorActionPreference = 'Stop'
$caKey = $null
$leafKey = $null
$caCertificate = $null
$leafCertificate = $null
try {
  $now = [DateTimeOffset]::UtcNow
  $caKey = [System.Security.Cryptography.RSA]::Create(2048)
  $caRequest = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
    "CN=Agentweaver Archive Check CA $env:ARCHIVE_RUN_ID",
    $caKey,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
  $caRequest.CertificateExtensions.Add(
    [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))
  $caRequest.CertificateExtensions.Add(
    [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
      [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign -bor
      [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::CrlSign, $true))
  $caRequest.CertificateExtensions.Add(
    [System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($caRequest.PublicKey, $false))
  $caCertificate = $caRequest.CreateSelfSigned($now.AddMinutes(-1), $now.AddMinutes(10))
  $caDer = $caCertificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)

  $leafKey = [System.Security.Cryptography.RSA]::Create(2048)
  $leafRequest = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
    'CN=127.0.0.1',
    $leafKey,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
  $leafRequest.CertificateExtensions.Add(
    [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
  $leafRequest.CertificateExtensions.Add(
    [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
      [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor
      [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment, $true))
  $eku = [System.Security.Cryptography.OidCollection]::new()
  [void]$eku.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
  $leafRequest.CertificateExtensions.Add(
    [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku, $true))
  $san = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
  $san.AddIpAddress([System.Net.IPAddress]::Parse('127.0.0.1'))
  $leafRequest.CertificateExtensions.Add($san.Build($true))
  $serial = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
  $serial[0] = $serial[0] -band 0x7f
  $unsignedLeaf = $leafRequest.Create($caCertificate, $now.AddMinutes(-1), $now.AddMinutes(9), $serial)
  $leafCertificate = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey($unsignedLeaf, $leafKey)
  $thumbprint = $caCertificate.Thumbprint
  $notAfter = $leafCertificate.NotAfter.ToUniversalTime().ToString('o')

  $caCertificate.Dispose()
  $caCertificate = $null
  $caKey.Dispose()
  $caKey = $null
  [ordered]@{
    caDer = [Convert]::ToBase64String($caDer)
    leafCertificatePem = $leafCertificate.ExportCertificatePem()
    leafPrivateKeyPem = $leafKey.ExportPkcs8PrivateKeyPem()
    thumbprint = $thumbprint
    notAfter = $notAfter
  } | ConvertTo-Json -Compress
}
finally {
  if ($leafCertificate) { $leafCertificate.Dispose() }
  if ($leafKey) { $leafKey.Dispose() }
  if ($caCertificate) { $caCertificate.Dispose() }
  if ($caKey) { $caKey.Dispose() }
}
"""


SECURE_WORKSPACE_SCRIPT = r"""
$ErrorActionPreference = 'Stop'
$path = $env:ARCHIVE_WORKSPACE
if (Test-Path -LiteralPath $path) { throw 'The verifier workspace already exists.' }
$sid = [System.Security.Principal.SecurityIdentifier]::new($env:ARCHIVE_SID)
$system = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$acl = [System.Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
$acl.SetOwner($sid)
$inherit = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
           [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
$allow = [System.Security.AccessControl.AccessControlType]::Allow
$full = [System.Security.AccessControl.FileSystemRights]::FullControl
$acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($sid, $full, $inherit, [System.Security.AccessControl.PropagationFlags]::None, $allow))
$acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($system, $full, $inherit, [System.Security.AccessControl.PropagationFlags]::None, $allow))
New-Item -ItemType Directory -Path $path -ErrorAction Stop | Out-Null
Set-Acl -LiteralPath $path -AclObject $acl
$check = Get-Acl -LiteralPath $path
$rules = @($check.Access | Where-Object { -not $_.IsInherited })
if (-not $check.AreAccessRulesProtected -or $rules.Count -ne 2 -or
    $check.GetOwner([System.Security.Principal.SecurityIdentifier]).Value -ne $sid.Value) {
  throw 'Workspace ACL did not remain restricted to its owner and SYSTEM.'
}
$actual = @($rules | ForEach-Object {
  $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
})
if (-not ($actual -contains $sid.Value) -or -not ($actual -contains $system.Value)) { throw 'Workspace ACL principals differ.' }
if (@($rules | Where-Object {
  $_.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow -or
  $_.FileSystemRights -ne [System.Security.AccessControl.FileSystemRights]::FullControl
}).Count -ne 0) { throw 'Workspace ACL grants unexpected access.' }
"""


TRUST_CHECK_SCRIPT = r"""
$ErrorActionPreference = 'Stop'
$store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
  [System.Security.Cryptography.X509Certificates.StoreName]::Root,
  [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
$store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
$matches = @($store.Certificates | Where-Object { $_.Thumbprint -eq $env:ARCHIVE_THUMBPRINT })
$store.Close()
if ($matches.Count -ne 0) { throw 'The unique test root already exists.' }
"""


TRUST_IMPORT_SCRIPT = r"""
$ErrorActionPreference = 'Stop'
$expected = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($env:ARCHIVE_CA_FILE)
if ($expected.Thumbprint -ne $env:ARCHIVE_THUMBPRINT) { throw 'The CA file thumbprint changed.' }
$store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
  [System.Security.Cryptography.X509Certificates.StoreName]::Root,
  [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
$store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
try {
  $matches = @($store.Certificates | Where-Object { $_.Thumbprint -eq $env:ARCHIVE_THUMBPRINT })
  if ($matches.Count -ne 0) { throw 'The unique test root appeared before import.' }
  $store.Add($expected)
  $after = @($store.Certificates | Where-Object { $_.Thumbprint -eq $env:ARCHIVE_THUMBPRINT })
  if ($after.Count -ne 1 -or -not [Convert]::ToBase64String($after[0].RawData).Equals([Convert]::ToBase64String($expected.RawData))) {
    throw 'The exact test root was not present after import.'
  }
}
finally { $store.Close(); $expected.Dispose() }
"""


TRUST_REMOVE_SCRIPT = r"""
$ErrorActionPreference = 'Stop'
$expected = $null
if (Test-Path -LiteralPath $env:ARCHIVE_CA_FILE -PathType Leaf) {
  $expected = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($env:ARCHIVE_CA_FILE)
  if ($expected.Thumbprint -ne $env:ARCHIVE_THUMBPRINT) { throw 'The recorded CA file thumbprint differs.' }
}
$store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
  [System.Security.Cryptography.X509Certificates.StoreName]::Root,
  [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
$store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
try {
  $matches = @($store.Certificates | Where-Object { $_.Thumbprint -eq $env:ARCHIVE_THUMBPRINT })
  if ($matches.Count -gt 1) { throw 'More than one exact test-root thumbprint is present.' }
  if ($matches.Count -eq 1) {
    if ($null -eq $expected -or
        -not [Convert]::ToBase64String($matches[0].RawData).Equals([Convert]::ToBase64String($expected.RawData))) {
      throw 'The root certificate does not match the recorded public CA.'
    }
    $store.Remove($matches[0])
  }
}
finally { $store.Close(); if ($expected) { $expected.Dispose() } }
$verify = [System.Security.Cryptography.X509Certificates.X509Store]::new(
  [System.Security.Cryptography.X509Certificates.StoreName]::Root,
  [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
$verify.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
$remaining = @($verify.Certificates | Where-Object { $_.Thumbprint -eq $env:ARCHIVE_THUMBPRINT })
$verify.Close()
if ($remaining.Count -ne 0) { throw 'The test root remains in CurrentUser\\Root.' }
"""


def run_powershell(
    executable: Path,
    script: str,
    extra_env: dict[str, str] | None = None,
    *,
    timeout: int = 5,
    workspace: Path | None = None,
    context: dict[str, Any] | None = None,
    persist_identity: bool = True,
    role: str = "powershell-helper",
    preserve_proxy_env: bool = False,
) -> str:
    environment = powershell_env(workspace)
    if preserve_proxy_env:
        proxy_names = {"http_proxy", "https_proxy", "all_proxy", "no_proxy"}
        environment = {
            name: value
            for name, value in environment.items()
            if name.casefold() not in proxy_names
        }
        environment.update(
            (name, value)
            for name, value in os.environ.items()
            if name.casefold() in proxy_names
        )
    environment.update(extra_env or {})
    process: subprocess.Popen[str] | None = None
    identity: dict[str, Any] | None = None
    try:
        process = subprocess.Popen(
            _powershell_command(executable, script),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            env=environment,
            creationflags=subprocess.CREATE_NEW_PROCESS_GROUP,
        )
        if context is not None:
            identity = {
                "pid": process.pid,
                "startTimeFiletime": process_start_ticks(process.pid),
                "role": role,
                "executable": str(executable),
                "state": "running",
            }
            context["children"].append((process, identity))
            context["journal"]["processIdentities"].append(identity)
            if persist_identity:
                persist_journal(context)
        try:
            communication_started = time.monotonic()
            stdout, _ = process.communicate(timeout=timeout)
        except subprocess.TimeoutExpired as error:
            elapsed_seconds = time.monotonic() - communication_started
            try:
                stop_child(process, "powershell-helper")
            except CheckError:
                if identity is not None:
                    identity["state"] = "unresolved"
                    if persist_identity:
                        persist_journal(context)
                raise
            process.communicate()
            if identity is not None:
                identity["state"] = "stopped-after-timeout"
                identity["exitCode"] = process.returncode
                if persist_identity:
                    persist_journal(context)
            diagnostic_role = role if role in POWERSHELL_DIAGNOSTIC_ROLES else "powershell-helper"
            raise CheckError(
                "A bounded Windows PowerShell helper timed out "
                f"(role={diagnostic_role}; timeoutSeconds={timeout}; elapsedSeconds={elapsed_seconds:.3f})."
            ) from error
        if identity is not None:
            identity["state"] = "exited"
            identity["exitCode"] = process.returncode
            if persist_identity:
                persist_journal(context)
    except BaseException:
        if process is not None and process.poll() is None:
            try:
                stop_child(process, "powershell-helper")
                process.communicate()
            except Exception:
                if identity is not None:
                    identity["state"] = "unresolved"
                    if persist_identity:
                        try:
                            persist_journal(context)
                        except Exception:
                            pass
        raise
    if process.returncode:
        raise CheckError("A Windows PowerShell helper failed.")
    return stdout.strip()


def make_certificates(
    executable: Path,
    run_id: str,
    context: dict[str, Any] | None = None,
) -> dict[str, Any]:
    output = run_powershell(
        executable,
        CERTIFICATE_SCRIPT,
        {"ARCHIVE_RUN_ID": run_id},
        timeout=10,
        context=context,
        role="ephemeral-certificate-generation",
    )
    try:
        result = json.loads(output)
        ca_der = base64.b64decode(result["caDer"], validate=True)
        leaf_certificate = result["leafCertificatePem"].encode("ascii")
        leaf_key = result["leafPrivateKeyPem"].encode("ascii")
        thumbprint = result["thumbprint"]
        if not isinstance(thumbprint, str) or len(thumbprint) != 40:
            raise ValueError("invalid thumbprint")
        if hashlib.sha1(ca_der).hexdigest().upper() != thumbprint.upper():
            raise ValueError("CA thumbprint mismatch")
        return {
            "caDer": bytearray(ca_der),
            "leafCertificatePem": bytearray(leaf_certificate),
            "leafPrivateKeyPem": bytearray(leaf_key),
            "thumbprint": thumbprint.upper(),
            "notAfter": result["notAfter"],
        }
    except (KeyError, TypeError, ValueError, json.JSONDecodeError) as error:
        raise CheckError("Windows PowerShell did not return a valid ephemeral certificate fixture.") from error


def secure_workspace(
    path: Path,
    sid: str,
    executable: Path,
    context: dict[str, Any] | None = None,
) -> None:
    if path.exists() or path.is_symlink():
        raise BlockedError("The verifier workspace must be new before ACL setup.")
    try:
        run_powershell(
            executable,
            SECURE_WORKSPACE_SCRIPT,
            {"ARCHIVE_WORKSPACE": str(path), "ARCHIVE_SID": sid},
            timeout=5,
            context=context,
            persist_identity=False,
            role="workspace-acl",
        )
    except BaseException:
        if path.exists():
            try:
                path.rmdir()
            except OSError:
                pass
        raise


def write_new(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("xb") as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())


def fsync_directory(path: Path) -> None:
    if os.name != "nt":
        descriptor = os.open(path, os.O_RDONLY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)


def write_journal(journal_path: Path, temporary_path: Path, journal: dict[str, Any]) -> None:
    encoded = (json.dumps(journal, indent=2, sort_keys=True) + "\n").encode("utf-8")
    with temporary_path.open("wb") as stream:
        stream.write(encoded)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary_path, journal_path)
    fsync_directory(journal_path.parent)


def workspace_plan(workspace: Path, receipt: dict[str, Any]) -> list[str]:
    paths = {workspace / "journal.json", workspace / "journal.json.new"}
    for directory in ("extracted", "fixtures", "child-temp"):
        paths.add(workspace / directory)
    for item in receipt["files"]:
        relative = Path(str(item["path"]))
        current = workspace / "extracted"
        for segment in relative.parts[:-1]:
            current = current / segment
            paths.add(current)
        paths.add(workspace / "extracted" / relative)
    for name in (
        "fixtures/foundation.json",
        "fixtures/images.json",
        "fixtures/valid-manifest.json",
        "fixtures/invalid-manifest.json",
        "fixtures/invalid-result.jsonl",
        "certificates/test-root.cer",
        "certificates/server.cer.pem",
        "certificates/server.key.pem",
    ):
        path = workspace / name
        current = path.parent
        while current != workspace:
            paths.add(current)
            current = current.parent
        paths.add(path)
    return sorted(str(path) for path in paths)


def persist_journal(context: dict[str, Any]) -> None:
    write_journal(
        context["journalPath"],
        context["journalTemporaryPath"],
        context["journal"],
    )


def target_environment(
    profile: dict[str, Any],
    workspace: Path,
    gateway: str,
    issuer: str,
    tenant: str,
    audience: str,
) -> dict[str, str]:
    environment: dict[str, str] = {"PATH": ""}
    for name, key in (
        ("SYSTEMROOT", "systemRoot"),
        ("WINDIR", "systemRoot"),
        ("USERPROFILE", "userProfile"),
        ("HOME", "home"),
        ("LOCALAPPDATA", "localAppData"),
        ("APPDATA", "appData"),
        ("USERNAME", "username"),
        ("USERDOMAIN", "userDomain"),
    ):
        value = profile.get(key)
        if isinstance(value, str) and value:
            environment[name] = value
    environment.update({
        "TEMP": str(workspace / "child-temp"),
        "TMP": str(workspace / "child-temp"),
        "AGENTWEAVER_GATEWAY_URL": gateway,
        "AGENTWEAVER_BROKER_ISSUER": issuer,
        "AGENTWEAVER_CLIENT_ID": "agentweaver-cli",
        "AGENTWEAVER_API_AUDIENCE": audience,
        "AGENTWEAVER_TENANT_ID": tenant,
        "NO_PROXY": "127.0.0.1,localhost",
        "no_proxy": "127.0.0.1,localhost",
    })
    allowed = {
        "PATH", "SYSTEMROOT", "WINDIR", "USERPROFILE", "HOME", "LOCALAPPDATA",
        "APPDATA", "USERNAME", "USERDOMAIN", "TEMP", "TMP",
        "AGENTWEAVER_GATEWAY_URL", "AGENTWEAVER_BROKER_ISSUER",
        "AGENTWEAVER_CLIENT_ID", "AGENTWEAVER_API_AUDIENCE",
        "AGENTWEAVER_TENANT_ID", "NO_PROXY", "no_proxy",
    }
    if set(environment) - allowed or environment["PATH"] != "":
        raise CheckError("The extracted apphost environment is not allowlisted.")
    if any(name.startswith("AGENTWEAVER_") and name not in allowed for name in environment):
        raise CheckError("An unapproved Agentweaver selector reached the extracted apphost.")
    return environment


def make_openapi() -> bytes:
    def response(description: str, media_type: str = "application/json") -> dict[str, Any]:
        return {
            "description": description,
            "content": {media_type: {"schema": {"type": "object"}}},
        }

    document = {
        "openapi": "3.1.0",
        "info": {"title": "Controlled Windows archive checks", "version": "1.0"},
        "components": {"securitySchemes": {"Bearer": {"type": "http", "scheme": "bearer"}}},
        "security": [{"Bearer": []}],
        "paths": {
            "/api/v1/authorization/context": {
                "get": {
                    "operationId": "getAuthorizationContext",
                    "responses": {"200": response("Synthetic current authorization context")},
                }
            },
            "/api/v1/package-smoke": {
                "get": {
                    "operationId": "packageSmoke",
                    "responses": {"200": response("Controlled packaging response")},
                }
            },
            "/api/v1/package-smoke-denied": {
                "get": {
                    "operationId": "packageSmokeDenied",
                    "responses": {
                        "200": response("Controlled packaging response"),
                        "403": response("Controlled denial", "application/problem+json"),
                    },
                }
            },
        },
    }
    return json.dumps(document, separators=(",", ":")).encode("utf-8")


class FixtureServer(http.server.ThreadingHTTPServer):
    daemon_threads = True
    block_on_close = False
    allow_reuse_address = False

    def __init__(self, port: int, state: FIXTURE_STATE, tls: ssl.SSLContext) -> None:
        self.state = state
        self.tls = tls
        self._owned_sockets: set[socket.socket] = set()
        self._owned_workers: set[threading.Thread] = set()
        self._owned_lock = threading.Lock()
        self._closing = False
        super().__init__(("127.0.0.1", port), FixtureHandler, bind_and_activate=True)
        self.timeout = 1

    def get_request(self) -> tuple[socket.socket, Any]:
        request, client_address = super().get_request()
        request.settimeout(1)
        with self._owned_lock:
            self._owned_sockets.add(request)
        return request, client_address

    def process_request(self, request: socket.socket, client_address: Any) -> None:
        worker = threading.Thread(
            target=self.process_request_thread,
            args=(request, client_address),
            daemon=True,
        )
        with self._owned_lock:
            self._owned_workers.add(worker)
        try:
            worker.start()
        except BaseException:
            with self._owned_lock:
                self._owned_workers.discard(worker)
            self.shutdown_request(request)
            raise

    def process_request_thread(self, request: socket.socket, client_address: Any) -> None:
        connection: socket.socket = request
        worker = threading.current_thread()
        try:
            with self._owned_lock:
                if self._closing:
                    return
                connection.settimeout(1)
                tls_connection = self.tls.wrap_socket(
                    connection,
                    server_side=True,
                    do_handshake_on_connect=False,
                )
                self._owned_sockets.discard(connection)
                self._owned_sockets.add(tls_connection)
                connection = tls_connection

            connection.settimeout(1)
            connection.do_handshake()
            self.finish_request(connection, client_address)
        except (OSError, ssl.SSLError):
            pass
        except Exception:
            self.handle_error(connection, client_address)
        finally:
            self.shutdown_request(connection)
            with self._owned_lock:
                self._owned_sockets.discard(request)
                self._owned_sockets.discard(connection)
                self._owned_workers.discard(worker)

    def owned_sockets(self) -> tuple[socket.socket, ...]:
        with self._owned_lock:
            return tuple(self._owned_sockets)

    def owned_workers(self) -> tuple[threading.Thread, ...]:
        with self._owned_lock:
            return tuple(self._owned_workers)

    def server_close(self) -> bool:
        errors = False
        with self._owned_lock:
            self._closing = True
            for connection in tuple(self._owned_sockets):
                try:
                    connection.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
                try:
                    connection.close()
                except OSError:
                    errors = True
        super().server_close()
        return not errors


class FixtureHandler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.0"

    @property
    def state(self) -> FIXTURE_STATE:
        return self.server.state  # type: ignore[attr-defined]

    def setup(self) -> None:
        super().setup()
        self.connection.settimeout(1)
        self.state.record_thread(threading.get_ident(), True)

    def handle(self) -> None:
        try:
            super().handle()
        except OSError:
            return

    def finish(self) -> None:
        try:
            super().finish()
        finally:
            self.state.record_thread(threading.get_ident(), False)

    def do_GET(self) -> None:
        path = urllib.parse.urlsplit(self.path).path
        if path == "/openapi/v1.json":
            self._json(200, json.loads(make_openapi()), method="GET")
        elif path == "/.well-known/openid-configuration":
            self._json(200, {
                "issuer": self.state.issuer,
                "authorization_endpoint": self.state.origin + "/connect/authorize",
                "token_endpoint": self.state.origin + "/connect/token",
                "jwks_uri": self.state.origin + "/.well-known/jwks.json",
            }, method="GET")
        elif path == "/.well-known/jwks.json":
            self._json(200, {"keys": []}, method="GET")
        elif path == "/api/v1/authorization/context":
            authorization = self.headers.get("Authorization", "")
            tenant = self.headers.get("X-Agentweaver-Tenant", "")
            authenticated = (
                self.state.current_access is not None
                and authorization.startswith("Bearer ")
                and hmac.compare_digest(authorization[7:], self.state.current_access)
                and tenant == self.state.tenant
            )
            self.state.update_assertion("authorizationContextAuthenticated", authenticated)
            if authenticated:
                self._json(200, {
                    "contractVersion": 1,
                    "issuer": self.state.issuer,
                    "actorId": self.state.actor,
                    "tenantId": self.state.tenant,
                    "membershipRevision": 1,
                    "boundProjectId": None,
                    "boundRunId": None,
                    "effectiveAuthority": [],
                }, method="GET")
            else:
                self._json(403, {"title": "Denied"}, "application/problem+json", "GET")
        elif path in ("/api/v1/package-smoke", "/api/v1/package-smoke-denied"):
            authorization = self.headers.get("Authorization", "")
            tenant = self.headers.get("X-Agentweaver-Tenant", "")
            authenticated = (
                self.state.current_access is not None
                and authorization.startswith("Bearer ")
                and hmac.compare_digest(authorization[7:], self.state.current_access)
                and tenant == self.state.tenant
            )
            self.state.update_assertion("operationRequestsAuthenticated", authenticated)
            if not authenticated:
                self._json(403, {"title": "Denied"}, "application/problem+json", "GET")
            elif path.endswith("-denied"):
                self._json(403, {"title": "Controlled denial"}, "application/problem+json", "GET")
            else:
                self._json(200, {"accepted": True, "operationId": "packageSmoke"}, method="GET")
        elif path == "/connect/authorize":
            self.state.update_assertion("noAuthorizationBrowserRequest", False)
            self._json(400, {"error": "authorization-not-used"}, method="GET")
        else:
            self.state.update_assertion("unexpectedFixtureRequest", False)
            self._json(404, {"title": "Not found"}, method="GET")

    def do_POST(self) -> None:
        path = urllib.parse.urlsplit(self.path).path
        length = self.headers.get("Content-Length", "")
        try:
            size = int(length)
        except ValueError:
            size = -1
        if size < 0 or size > 65536:
            self.state.update_assertion("unexpectedFixtureRequest", False)
            self._json(400, {"title": "Invalid request"}, method="POST")
            return
        body = self.rfile.read(size)
        if path != "/connect/token":
            self.state.update_assertion("unexpectedFixtureRequest", False)
            self._json(404, {"title": "Not found"}, method="POST")
            return
        try:
            fields = urllib.parse.parse_qs(body.decode("ascii"), strict_parsing=True)
            grant = fields.get("grant_type", [])
            client = fields.get("client_id", [])
            refresh = fields.get("refresh_token", [])
            valid = (
                self.headers.get_content_type() == "application/x-www-form-urlencoded"
                and grant == ["refresh_token"]
                and client == [self.state.client_id]
                and len(refresh) == 1
                and self.state.current_refresh is not None
                and refresh[0] == self.state.current_refresh
            )
        except (UnicodeDecodeError, ValueError):
            valid = False
        self.state.update_assertion("refreshGrantAndClientAccepted", valid)
        if not valid:
            self.state.update_assertion("refreshTokensRotatedOnce", False)
            self._json(400, {"error": "invalid_grant"}, method="POST")
            return
        replacement = "synthetic-refresh-" + secrets.token_urlsafe(32)
        access = "synthetic-access-" + secrets.token_urlsafe(32)
        previous = self.state.current_refresh
        self.state.current_refresh = replacement
        self.state.current_access = access
        self.state.secrets.update((previous, replacement, access))
        self._json(200, {
            "access_token": access,
            "token_type": "Bearer",
            "expires_in": 300,
            "refresh_token": replacement,
        }, method="POST")

    def _json(
        self,
        status: int,
        payload: dict[str, Any],
        content_type: str = "application/json",
        method: str = "GET",
    ) -> None:
        body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Connection", "close")
        self.end_headers()
        self.state.record(method, self.path, status)
        try:
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError, OSError):
            return

    def log_message(self, _format: str, *_args: object) -> None:
        return


def reserve_loopback_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.bind(("127.0.0.1", 0))
        address = listener.getsockname()
        if address[0] != "127.0.0.1":
            raise BlockedError("The fixture listener did not bind to IPv4 loopback.")
        return int(address[1])


def create_valid_manifest(workspace: Path, gateway: str, issuer: str, tenant: str, audience: str) -> bytes:
    fixtures = workspace / "fixtures"
    source_sha = "a" * 40
    source_tree = "b" * 40
    write_new(fixtures / "foundation.json", b"{}\n")
    write_new(
        fixtures / "images.json",
        json.dumps({"images": {"agentweaver-agenthost": "sha-" + source_sha}}).encode("utf-8") + b"\n",
    )
    manifest = {
        "executionIntent": "execute",
        "journey": "message",
        "environment": {
            "baseUrl": gateway + "/",
            "issuer": issuer,
            "audience": audience,
            "actorId": "archive-fixture-actor",
            "tenantId": tenant,
        },
        "pins": {
            "context": "archive-check-validation-only",
            "namespace": "agentweaver",
            "providerReceipt": {
                "configMapName": "provider-catalog",
                "sourceCommit": source_sha,
                "uid": "provider-fixture",
                "resourceVersion": "1",
            },
            "runtimeReceipt": {"revision": 1},
            "storageReceipt": {"shareId": "archive-storage-fixture"},
            "sourceReceipt": {"sourceSha": source_sha, "sourceTree": source_tree},
        },
        "deploymentEvidence": {
            "manifestPath": "foundation.json",
            "imageManifestPath": "images.json",
        },
        "fixturePlan": {
            "expectedPlatformRuntimeRevision": 1,
            "selectionContext": {
                "revision": "selection-v1",
                "availableModelSelectionReferences": [],
                "providerRequirements": [],
                "requiredEgress": [],
            },
            "gate": None,
            "message": {"text": "Controlled archive validation input."},
            "deniedActor": {"actorId": "archive-denied", "tenantId": "archive-denied-tenant"},
        },
    }
    return json.dumps(manifest, separators=(",", ":")).encode("utf-8")


def create_invalid_manifest(valid: bytes) -> bytes:
    manifest = json.loads(valid)
    manifest["journey"] = "unsupported-archive-validation"
    return json.dumps(manifest, separators=(",", ":")).encode("utf-8")


def run_cli(
    context: dict[str, Any],
    arguments: list[str],
    label: str,
    *,
    timeout: int = 45,
) -> tuple[int, str, str]:
    clock = context.get("cleanupDeadline") if label == "cleanup-auth-logout" else context["deadline"]
    remaining = clock - time.monotonic()
    if remaining <= 0:
        raise CheckError("The ten-minute archive-check deadline expired.")
    limit = min(timeout, max(1, int(remaining)))
    command = [str(context["executable"]), *arguments]
    if Path(command[0]).resolve() != context["executable"]:
        raise CheckError("The child executable is not the exact extracted apphost.")
    if not context["executable"].is_relative_to(context["workspace"]):
        raise CheckError("The child executable escaped its owned extraction directory.")
    process = subprocess.Popen(
        command,
        cwd=context["childWorkingDirectory"],
        env=context["childEnvironment"],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        creationflags=subprocess.CREATE_NEW_PROCESS_GROUP,
    )
    try:
        identity = track_child(context, process, label, str(context["executable"]))
    except BaseException:
        if process.poll() is None:
            stop_child(process, label)
        raise
    context["commands"].append({
        "name": label,
        "arguments": arguments,
        "exitCode": None,
        "timedOut": False,
        "syntheticSecretObserved": False,
    })
    try:
        stdout, stderr = process.communicate(timeout=limit)
    except subprocess.TimeoutExpired as error:
        context["commands"][-1]["timedOut"] = True
        try:
            stop_child(process, label)
        except CheckError as stop_error:
            identity["state"] = "unresolved"
            persist_journal(context)
            raise stop_error from error
        stdout, stderr = process.communicate()
        identity["state"] = "stopped-after-timeout"
        persist_journal(context)
        raise CheckError(f"Extracted apphost command {label} timed out; rotating credentials were not retried.") from error
    except BaseException:
        if process.poll() is None:
            stop_child(process, label)
        raise

    combined = stdout + stderr
    leaked = any(secret and secret in combined for secret in context["fixtureState"].secrets)
    context["commands"][-1]["exitCode"] = process.returncode
    context["commands"][-1]["syntheticSecretObserved"] = leaked
    identity["state"] = "exited"
    identity["exitCode"] = process.returncode
    persist_journal(context)
    if leaked:
        raise CheckError(f"Extracted apphost command {label} exposed a synthetic secret.")
    return process.returncode, stdout, stderr


def parse_json_output(stdout: str, label: str) -> dict[str, Any]:
    try:
        value = json.loads(stdout)
    except json.JSONDecodeError as error:
        raise CheckError(f"{label} did not emit its expected JSON result.") from error
    if not isinstance(value, dict):
        raise CheckError(f"{label} did not emit a JSON object.")
    return value


def assert_cli_result(
    context: dict[str, Any],
    arguments: list[str],
    label: str,
    expected_exit: int,
    *,
    output_contains: tuple[str, ...] = (),
) -> tuple[str, str]:
    exit_code, stdout, stderr = run_cli(context, arguments, label)
    if exit_code != expected_exit:
        raise CheckError(f"Extracted apphost command {label} exited {exit_code}, expected {expected_exit}.")
    if expected_exit == 0 and stderr:
        raise CheckError(f"Extracted apphost command {label} wrote unexpected standard error.")
    for expected in output_contains:
        if expected not in stdout:
            raise CheckError(f"Extracted apphost command {label} omitted an expected result.")
    return stdout, stderr


def native_vault_probe(context: dict[str, Any]) -> dict[str, Any]:
    target = context["journal"]["nativeProbeTarget"]
    if credential_read(target) is not None:
        raise BlockedError("The unique native vault probe target already exists.")
    context["journal"]["nativeProbeAcquisitionAttempted"] = True
    persist_journal(context)
    first = "synthetic-native-probe-" + secrets.token_urlsafe(32)
    second = "synthetic-native-probe-" + secrets.token_urlsafe(32)
    context["fixtureState"].secrets.update((first, second))
    try:
        credential_write(target, first)
        if credential_read(target) != first:
            raise CheckError("The native vault probe could not read its newly written value.")
        credential_write(target, second)
        if credential_read(target) != second:
            raise CheckError("The native vault probe could not read its updated value.")
        credential_delete(target)
        if credential_read(target) is not None:
            raise CheckError("The native vault probe entry remained after deletion.")
    finally:
        first = ""
        second = ""
    return {
        "kind": "native-windows-credential-manager-api-probe",
        "status": "passed",
        "operations": ["create", "read", "update", "read", "delete", "not-found"],
        "productionAdapterProof": False,
    }


def check_response(
    response: tuple[int, str, str],
    label: str,
    expected_status: int,
    expected_cli_exit: int,
) -> None:
    exit_code, stdout, stderr = response
    if exit_code != expected_cli_exit:
        raise CheckError(f"Extracted apphost command {label} exited {exit_code}, expected {expected_cli_exit}.")
    if expected_cli_exit == 0 and stderr:
        raise CheckError(f"Extracted apphost command {label} wrote unexpected standard error.")
    document = parse_json_output(stdout, label)
    if document.get("status") != expected_status:
        raise CheckError(f"Extracted apphost command {label} returned an unexpected HTTP status.")


def write_fixture_files(context: dict[str, Any], gateway: str, issuer: str, tenant: str, audience: str) -> None:
    fixtures = context["workspace"] / "fixtures"
    valid = create_valid_manifest(context["workspace"], gateway, issuer, tenant, audience)
    write_new(fixtures / "valid-manifest.json", valid)
    write_new(fixtures / "invalid-manifest.json", create_invalid_manifest(valid))


def run_controlled_sequence(context: dict[str, Any], gateway: str, issuer: str, tenant: str, audience: str) -> dict[str, Any]:
    state: FIXTURE_STATE = context["fixtureState"]
    target = context["journal"]["credentialTarget"]
    lock_path: Path = context["lockPath"]
    marker_path: Path = context["markerPath"]
    assertions: dict[str, bool] = {}

    stdout, _ = assert_cli_result(context, ["--help"], "help", 0, output_contains=HELP_EXPECTED)
    assertions["help"] = bool(stdout)
    stdout, _ = assert_cli_result(context, ["--version"], "version", 0)
    receipt = context["receipt"]
    version = str(receipt["version"])
    commit = str(receipt["source"]["commit"])
    tree = str(receipt["source"]["tree"])
    if version not in stdout or commit[:12] not in stdout or tree[:12] not in stdout:
        raise CheckError("The extracted apphost version does not match its source-pinned receipt.")
    assertions["versionMatchesReceipt"] = True

    stdout, _ = assert_cli_result(
        context,
        ["api", "operations"],
        "api-operations",
        0,
        output_contains=("packageSmoke", "packageSmokeDenied", "getAuthorizationContext"),
    )
    assertions["discoveryIncludesControlledOperations"] = True
    if not any(call["path"] == "/openapi/v1.json" and call["method"] == "GET" for call in state.calls_snapshot()):
        raise CheckError("The extracted apphost did not request the controlled OpenAPI document.")

    status_text, _ = assert_cli_result(context, ["auth", "status"], "auth-status-before-seed", 0)
    status = parse_json_output(status_text, "auth-status-before-seed")
    if status.get("isSignedIn") is not False:
        raise CheckError("The extracted apphost reported a sign-in before the synthetic seed.")
    if credential_read(target) is not None or marker_path.exists():
        raise CheckError("The fixture credential or refresh-recovery marker changed before seeding.")
    if not lock_path.is_file() or has_reparse_attribute(lock_path) or lock_path.stat().st_size != 0:
        raise CheckError("The extracted apphost did not create its expected stable account lock.")
    context["journal"]["stableLockIdentity"] = file_identity(lock_path)
    persist_journal(context)
    assertions["statusBeforeSeedIsSignedOut"] = True

    seed = "synthetic-refresh-" + secrets.token_urlsafe(32)
    state.current_refresh = seed
    state.secrets.add(seed)
    context["journal"]["credentialSeedAttempted"] = True
    persist_journal(context)
    credential_write(target, seed)
    if credential_read(target) != seed:
        raise CheckError("Credential Manager did not retain the synthetic saved-session seed.")
    status_text, _ = assert_cli_result(context, ["auth", "status"], "auth-status-after-seed", 0)
    status = parse_json_output(status_text, "auth-status-after-seed")
    if status.get("isSignedIn") is not True:
        raise CheckError("The extracted apphost did not recognize the synthetic saved session.")
    assertions["statusAfterSeedIsSignedIn"] = True

    before_refresh = len([call for call in state.calls_snapshot() if call["path"] == "/connect/token"])
    before_context = len([call for call in state.calls_snapshot() if call["path"] == "/api/v1/authorization/context"])
    before_operation = len([call for call in state.calls_snapshot() if call["path"] == "/api/v1/package-smoke"])
    result = run_cli(context, ["api", "invoke", "packageSmoke"], "invoke-success")
    check_response(result, "invoke-success", 200, 0)
    if len([call for call in state.calls_snapshot() if call["path"] == "/connect/token"]) != before_refresh + 1:
        raise CheckError("The first invocation did not perform exactly one saved-session refresh.")
    if len([call for call in state.calls_snapshot() if call["path"] == "/api/v1/authorization/context"]) != before_context + 1:
        raise CheckError("The first invocation did not preflight its authorization context.")
    if len([call for call in state.calls_snapshot() if call["path"] == "/api/v1/package-smoke"]) != before_operation + 1:
        raise CheckError("The first invocation did not call its controlled packaging operation.")
    if credential_read(target) != state.current_refresh or marker_path.exists():
        raise CheckError("The production Windows vault did not contain the rotated credential with no recovery marker.")
    assertions["savedSessionRefreshAndAuthenticatedInvoke"] = True

    before_refresh = len([call for call in state.calls_snapshot() if call["path"] == "/connect/token"])
    before_context = len([call for call in state.calls_snapshot() if call["path"] == "/api/v1/authorization/context"])
    before_denied = len([call for call in state.calls_snapshot() if call["path"] == "/api/v1/package-smoke-denied"])
    result = run_cli(context, ["api", "invoke", "packageSmokeDenied"], "invoke-denied")
    check_response(result, "invoke-denied", 403, 1)
    if len([call for call in state.calls_snapshot() if call["path"] == "/connect/token"]) != before_refresh + 1:
        raise CheckError("The denied invocation did not perform exactly one saved-session refresh.")
    if len([call for call in state.calls_snapshot() if call["path"] == "/api/v1/authorization/context"]) != before_context + 1:
        raise CheckError("The denied invocation did not preflight its authorization context.")
    if len([call for call in state.calls_snapshot() if call["path"] == "/api/v1/package-smoke-denied"]) != before_denied + 1:
        raise CheckError("The denied invocation did not reach its controlled operation.")
    if credential_read(target) != state.current_refresh or marker_path.exists():
        raise CheckError("The production Windows vault did not retain the second rotated credential safely.")
    assertions["httpDenialIsInvocationOutcome"] = True

    if state.current_refresh is None or seed == state.current_refresh:
        raise CheckError("The saved-session refresh fixture did not rotate its synthetic credential.")
    history = [call for call in state.calls_snapshot() if call["path"] == "/connect/token"]
    if len(history) != 2 or not state.assertions_snapshot()["refreshTokensRotatedOnce"]:
        raise CheckError("The fixture accepted a replay or an unexpected number of refresh exchanges.")
    if not all(state.assertions_snapshot()[name] for name in (
        "refreshGrantAndClientAccepted",
        "authorizationContextAuthenticated",
        "operationRequestsAuthenticated",
    )):
        raise CheckError("The loopback fixture rejected a controlled authentication assertion.")

    before_calls = len(state.calls_snapshot())
    token_before_logout = len([call for call in state.calls_snapshot() if call["path"] == "/connect/token"])
    assert_cli_result(context, ["auth", "logout"], "auth-logout", 0)
    if credential_read(target) is not None or marker_path.exists():
        raise CheckError("The extracted apphost did not remove the selected credential and refresh marker.")
    assertions["logoutDeletesSelectedCredentialAndMarker"] = True

    status_text, _ = assert_cli_result(context, ["auth", "status"], "auth-status-after-logout", 0)
    status = parse_json_output(status_text, "auth-status-after-logout")
    if status.get("isSignedIn") is not False:
        raise CheckError("The extracted apphost reported a sign-in after logout.")
    before_unauthenticated = len(state.calls_snapshot())
    result = run_cli(context, ["api", "invoke", "packageSmoke"], "invoke-after-logout")
    if result[0] == 0:
        raise CheckError("An unauthenticated operation unexpectedly succeeded.")
    if credential_read(target) is not None:
        raise CheckError("The unauthenticated operation recreated a saved credential.")
    after_calls = state.calls_snapshot()
    new_calls = after_calls[before_unauthenticated:]
    if any(call["path"] in ("/connect/token", "/api/v1/authorization/context", "/api/v1/package-smoke") for call in new_calls):
        raise CheckError("The unauthenticated operation attempted token refresh, context, or operation access.")
    if len([call for call in after_calls if call["path"] == "/connect/token"]) != token_before_logout:
        raise CheckError("The unauthenticated operation attempted a token refresh.")
    assertions["unauthenticatedInvokeStopsBeforeAuthenticationAndOperation"] = True

    write_fixture_files(context, gateway, issuer, tenant, audience)
    fixture_before = len(state.calls_snapshot())
    lock_before = file_identity(lock_path)
    valid_input = context["workspace"] / "fixtures" / "valid-manifest.json"
    valid_output = context["workspace"] / "fixtures" / "dry-run-result.jsonl"
    result = run_cli(
        context,
        ["test", "--input", str(valid_input), "--output", str(valid_output), "--dry-run", "--interactive"],
        "valid-dry-run",
    )
    if result[0] != 0 or "Dry run only" not in result[1] or result[2] or valid_output.exists():
        raise CheckError("The valid exact-apphost dry-run did not remain a no-evidence validation.")
    if len(state.calls_snapshot()) != fixture_before or credential_read(target) is not None or marker_path.exists():
        raise CheckError("The valid dry-run contacted the fixture or changed sign-in state.")
    if file_identity(lock_path) != lock_before:
        raise CheckError("The valid dry-run changed native credential-lock state.")
    assertions["validDryRunHasNoNetworkAuthBrowserOrEvidence"] = True

    invalid_input = context["workspace"] / "fixtures" / "invalid-manifest.json"
    invalid_output = context["workspace"] / "fixtures" / "invalid-result.jsonl"
    if invalid_output.exists():
        raise CheckError("The invalid dry-run result path was not fresh.")
    invalid_calls = len(state.calls_snapshot())
    result = run_cli(
        context,
        ["test", "--input", str(invalid_input), "--output", str(invalid_output), "--dry-run"],
        "invalid-dry-run",
    )
    if result[0] != 2 or not invalid_output.is_file():
        raise CheckError("The invalid dry-run did not return a new blocked validation result.")
    try:
        lines = invalid_output.read_text(encoding="utf-8").splitlines()
        document = json.loads(lines[-1])
    except (OSError, IndexError, json.JSONDecodeError) as error:
        raise CheckError("The invalid dry-run output was not valid JSON evidence.") from error
    if (
        document.get("kind") != "p1-acceptance-result"
        or document.get("surfaceStatus") != "blocked"
        or document.get("fullP1Status") != "blocked"
        or document.get("exitCode") != 2
    ):
        raise CheckError("The invalid input result was not an explicit blocked validation outcome.")
    if len(state.calls_snapshot()) != invalid_calls or credential_read(target) is not None or marker_path.exists():
        raise CheckError("The invalid dry-run contacted the fixture or changed sign-in state.")
    assertions["invalidDryRunIsBlockedValidationNotJourney"] = True

    if before_calls >= len(state.calls_snapshot()):
        raise CheckError("The controlled fixture did not observe the authenticated operation checks.")
    assertions.update(state.assertions_snapshot())
    return assertions


def expected_profile(executable: Path, port: int) -> dict[str, Any]:
    fixture_uri = f"https://127.0.0.1:{port}/"
    output = run_powershell(
        executable,
        PREFLIGHT_SCRIPT,
        {"ARCHIVE_FIXTURE_URI": fixture_uri},
        timeout=5,
        preserve_proxy_env=True,
    )
    try:
        profile = json.loads(output)
    except json.JSONDecodeError as error:
        raise BlockedError("Windows profile or proxy preflight returned invalid metadata.") from error
    if not isinstance(profile, dict):
        raise BlockedError("Windows profile preflight returned invalid metadata.")
    if type(profile.get("psMajor")) is not int or profile["psMajor"] < 7:
        raise BlockedError("PowerShell 7 or later is required for ephemeral .NET certificate support.")
    if type(profile.get("proxyBypassed")) is not bool:
        raise BlockedError("Windows profile preflight returned invalid proxy diagnostics.")
    if "proxyTarget" not in profile:
        raise BlockedError("Windows profile preflight did not report a proxy target.")
    if profile["proxyTarget"] is not None and profile["proxyTarget"] != fixture_uri:
        raise BlockedError("The configured system proxy would redirect loopback fixture traffic.")
    for key in ("sid", "localAppData", "userProfile", "appData", "systemRoot"):
        if not isinstance(profile.get(key), str) or not profile[key]:
            raise BlockedError("The current Windows profile is incomplete for native archive checks.")
    return profile


def check_candidate(directory: Path, source_commit: str, root: Path) -> tuple[dict[str, Any], Path, Path, str]:
    if package.runtime_rid() != "win-x64" or os.name != "nt" or ctypes.sizeof(ctypes.c_void_p) != 8:
        raise BlockedError("The Windows archive verifier requires a native 64-bit Windows x64 process.")
    if not package.COMMIT_PATTERN.fullmatch(source_commit):
        raise BlockedError("The expected source commit must be a full Git SHA.")
    actual_head = package.git_value(root, "HEAD")
    if actual_head != source_commit:
        raise BlockedError("The requested archive source commit does not match this worktree HEAD.")
    package.assert_build_sources_clean(root)
    receipt = package.verify_one(directory, "win-x64", source_commit=source_commit)
    info = package.archive_info("win-x64")
    archive = directory / info.name
    receipt_path = directory / f"{info.name}.receipt.json"
    current_tree = package.git_value(root, f"{source_commit}^{{tree}}")
    if receipt["source"]["tree"] != current_tree:
        raise CheckError("The archive receipt tree does not match the source commit tree.")
    return receipt, archive, receipt_path, current_tree


def planned_path_list(workspace: Path, receipt: dict[str, Any]) -> list[str]:
    return workspace_plan(workspace, receipt)


def verify_windows_path(path: Path, *, must_exist: bool) -> Path:
    full = Path(os.path.abspath(path))
    if must_exist and not full.exists():
        raise BlockedError(f"Required path is missing: {full}")
    assert_no_reparse_components(full)
    return full


def save_sidecar(output: Path, result: dict[str, Any]) -> None:
    if not output.parent.is_dir():
        raise CheckError("The sidecar output directory must already exist.")
    assert_no_reparse_components(output.parent)
    if output.exists() or output.is_symlink():
        raise CheckError("The sidecar output path already exists; output is never overwritten.")
    write_new(output, (json.dumps(result, indent=2, sort_keys=True) + "\n").encode("utf-8"))


def initial_sidecar(
    run_id: str,
    source_commit: str | None,
    source_tree: str | None,
    started: str,
    status: str,
    reason: str | None = None,
) -> dict[str, Any]:
    checks: dict[str, Any] = {"status": status, "commands": [], "assertions": {}, "fixtureCalls": []}
    if reason:
        checks["reason"] = reason
    return {
        "schemaVersion": 1,
        "runId": run_id,
        "startedAt": started,
        "completedAt": None,
        "host": {
            "os": platform.platform(),
            "osBuild": platform.version(),
            "processArchitecture": platform.machine(),
            "processBits": ctypes.sizeof(ctypes.c_void_p) * 8,
            "logonSid": None,
        },
        "rid": "win-x64",
        "source": {"commit": source_commit, "tree": source_tree},
        "archive": {},
        "executable": {},
        "existingRuntimeTest": None,
        "controlledArchiveChecks": checks,
        "nativeVaultApiProbe": {"status": "not-run", "kind": "native-windows-credential-manager-api-probe"},
        "nativeJourneyProof": {
            "status": "not-run",
            "reason": "Controlled packaging checks are not a native API journey or the required controlled journey triplet.",
        },
        "issue2449Acceptance": "incomplete",
    }


def fail_sidecar_if_cleanup_unverified(result: dict[str, Any], cleanup: dict[str, Any] | None) -> None:
    if cleanup is not None and cleanup.get("status") != "passed":
        result["controlledArchiveChecks"]["status"] = "failed"


def cleanup_marker(path: Path) -> None:
    if not path.exists() and not path.is_symlink():
        return
    if has_reparse_attribute(path):
        raise CheckError("The owned refresh marker became a reparse point.")
    if not path.is_file() or path.read_bytes() != b"\x01":
        raise CheckError("The owned refresh marker contents are not safely reconcilable.")
    path.unlink()
    if path.exists():
        raise CheckError("The owned refresh marker remains after exact cleanup.")


def remove_owned_trust(context: dict[str, Any], errors: list[str]) -> None:
    journal = context["journal"]
    if not journal.get("trustImportAttempted"):
        return
    try:
        env = {
            "ARCHIVE_CA_FILE": str(context["caPath"]),
            "ARCHIVE_THUMBPRINT": journal["certificateThumbprint"],
        }
        run_powershell(
            context["powershell"],
            TRUST_REMOVE_SCRIPT,
            env,
            timeout=5,
            workspace=context["childWorkingDirectory"],
            context=context,
            role="current-user-root-removal",
        )
        journal["trustRemovedAndVerified"] = True
        persist_journal(context)
    except Exception:
        errors.append("current-user-root-removal-unverified")


def wait_for_fixture_threads(server: FixtureServer, worker: threading.Thread, state: FIXTURE_STATE) -> bool:
    deadline = time.monotonic() + 5
    try:
        if worker.ident is not None:
            server.shutdown()
        sockets_closed = server.server_close()
        if worker.ident is not None:
            worker.join(timeout=max(0, deadline - time.monotonic()))
        for request_worker in server.owned_workers():
            request_worker.join(timeout=max(0, deadline - time.monotonic()))
        with state.thread_changed:
            while state.active_threads and time.monotonic() < deadline:
                state.thread_changed.wait(timeout=max(0, deadline - time.monotonic()))
        return (
            sockets_closed
            and not worker.is_alive()
            and not server.owned_workers()
            and not server.owned_sockets()
            and not state.active_threads
        )
    except Exception:
        try:
            server.server_close()
        except Exception:
            pass
        return False


def cleanup_unjournaled_workspace(context: dict[str, Any]) -> dict[str, Any]:
    errors = stop_owned_children(context, persist=False)
    workspace: Path = context["workspace"]
    if not errors and workspace.exists():
        for path in (context["journalTemporaryPath"], context["journalPath"]):
            if not path.exists() and not path.is_symlink():
                continue
            try:
                if has_reparse_attribute(path) or not path.is_file():
                    raise CheckError("The bootstrap journal path is not a regular owned file.")
                path.unlink()
            except Exception:
                errors.append("bootstrap-journal-cleanup-unverified")
        if not errors:
            try:
                workspace.rmdir()
            except OSError:
                errors.append("bootstrap-workspace-not-empty")
    return {
        "status": "passed" if not errors else "failed",
        "errors": list(dict.fromkeys(errors)),
        "listenerStopped": True,
        "productionFixtureCredentialAbsent": True,
        "refreshMarkerAbsent": True,
        "nativeProbeCredentialAbsent": True,
        "currentUserRootCertificateAbsent": True,
        "stableLockResidual": None,
        "workspaceRemoved": not workspace.exists(),
        "executableSha256After": None,
    }


def delete_workspace_paths(
    context: dict[str, Any],
    *,
    keep_journal: bool,
    preserve_paths: set[Path] | None = None,
) -> tuple[bool, list[str]]:
    workspace: Path = context["workspace"]
    journal = context["journal"]
    expected = {Path(value) for value in journal["createdPaths"]}
    errors: list[str] = []
    if not workspace.exists():
        return False, ["owned-workspace-missing"]
    try:
        assert_no_reparse_components(workspace)
        for root, dirs, files in os.walk(workspace, topdown=True, followlinks=False):
            current = Path(root)
            for name in dirs + files:
                candidate = current / name
                if has_reparse_attribute(candidate):
                    raise CheckError("An owned workspace path became a reparse point.")
                if candidate not in expected:
                    raise CheckError("An unjournaled path exists inside the owned workspace.")
    except Exception:
        errors.append("workspace-ownership-or-reparse-check-failed")
        return False, errors

    journal_path: Path = context["journalPath"]
    temporary_path: Path = context["journalTemporaryPath"]
    preserve = preserve_paths or set()
    entries = sorted(
        (path for path in expected if path != workspace and path not in preserve),
        key=lambda item: len(item.parts),
        reverse=True,
    )
    for path in entries:
        if keep_journal and path == journal_path:
            continue
        if not path.exists() and not path.is_symlink():
            continue
        try:
            if has_reparse_attribute(path):
                raise CheckError("Refusing to delete an owned reparse point.")
            if path.is_dir():
                path.rmdir()
            else:
                path.unlink()
            if path.exists() or path.is_symlink():
                raise CheckError("An owned workspace path remains after deletion.")
        except Exception:
            errors.append("workspace-path-cleanup-failed")
    if not errors and not keep_journal:
        try:
            workspace.rmdir()
        except OSError:
            errors.append("owned-workspace-not-empty")
    if errors and not keep_journal:
        return False, errors
    return not errors, errors


def cleanup_run(context: dict[str, Any], server: FixtureServer | None, worker: threading.Thread | None) -> dict[str, Any]:
    errors: list[str] = []
    journal = context["journal"]
    context["cleanupDeadline"] = time.monotonic() + 60
    journal["cleanupStartedAt"] = utc_now()
    journal["cleanupProgress"] = "stopping-owned-processes"
    persist_journal(context)

    errors.extend(stop_owned_children(context))

    listener_stopped = True
    if server is not None and worker is not None:
        listener_stopped = wait_for_fixture_threads(server, worker, context["fixtureState"])
        if not listener_stopped:
            errors.append("loopback-listener-or-handler-shutdown-unverified")
    context.pop("serverTls", None)
    import gc

    gc.collect()
    journal["cleanupProgress"] = "listener-stopped" if listener_stopped else "listener-shutdown-failed"
    persist_journal(context)

    if errors:
        journal["cleanupErrors"] = list(dict.fromkeys(errors))
        journal["cleanupProgress"] = "unresolved"
        persist_journal(context)
        return {
            "status": "failed",
            "errors": list(dict.fromkeys(errors)),
            "listenerStopped": listener_stopped,
            "productionFixtureCredentialAbsent": False,
            "refreshMarkerAbsent": False,
            "nativeProbeCredentialAbsent": False,
            "currentUserRootCertificateAbsent": False,
            "stableLockResidual": None,
            "workspaceRemoved": False,
            "executableSha256After": None,
        }

    account_target = journal["credentialTarget"]
    credential_present = False
    try:
        credential_present = credential_read(account_target) is not None
    except Exception:
        errors.append("production-fixture-credential-read-unverified")

    seed_attempted = bool(journal.get("credentialSeedAttempted"))
    marker_present = context["markerPath"].exists()
    if credential_present or seed_attempted:
        try:
            executable = context.get("executable")
            if executable is not None and executable.is_file() and sha256_file(executable) == context.get("executableHash"):
                logout = run_cli(context, ["auth", "logout"], "cleanup-auth-logout", timeout=45)
                if logout[0] != 0:
                    errors.append("exact-apphost-auth-logout-failed")
            else:
                errors.append("exact-apphost-unavailable-for-logout")
        except Exception:
            errors.append("exact-apphost-auth-logout-failed")

    process_errors = stop_owned_children(context)
    errors.extend(process_errors)
    if process_errors:
        journal["cleanupErrors"] = list(dict.fromkeys(errors))
        journal["cleanupProgress"] = "unresolved"
        persist_journal(context)
        return {
            "status": "failed",
            "errors": list(dict.fromkeys(errors)),
            "listenerStopped": listener_stopped,
            "productionFixtureCredentialAbsent": False,
            "refreshMarkerAbsent": False,
            "nativeProbeCredentialAbsent": False,
            "currentUserRootCertificateAbsent": False,
            "stableLockResidual": None,
            "workspaceRemoved": False,
            "executableSha256After": None,
        }

    if seed_attempted:
        try:
            credential_delete(account_target)
            if credential_read(account_target) is not None:
                raise CheckError("The exact fixture credential remains.")
            journal["productionFixtureCredentialDeletedAndVerified"] = True
        except Exception:
            errors.append("production-fixture-credential-cleanup-unverified")
    elif credential_present:
        errors.append("unexpected-credential-appeared-at-unowned-target")

    if seed_attempted:
        try:
            cleanup_marker(context["markerPath"])
            journal["refreshMarkerDeletedAndVerified"] = True
        except Exception:
            errors.append("refresh-marker-cleanup-unverified")
    elif marker_present:
        errors.append("unexpected-marker-appeared-at-unowned-state-path")

    if journal.get("nativeProbeAcquisitionAttempted"):
        try:
            credential_delete(journal["nativeProbeTarget"])
            if credential_read(journal["nativeProbeTarget"]) is not None:
                raise CheckError("The separate native probe credential remains.")
            journal["nativeProbeDeletedAndVerified"] = True
        except Exception:
            errors.append("native-probe-credential-cleanup-unverified")
    else:
        try:
            if credential_read(journal["nativeProbeTarget"]) is not None:
                errors.append("unexpected-credential-appeared-at-native-probe-target")
        except Exception:
            errors.append("native-probe-target-absence-unverified")

    remove_owned_trust(context, errors)

    process_errors = stop_owned_children(context)
    errors.extend(process_errors)
    if process_errors:
        journal["cleanupErrors"] = list(dict.fromkeys(errors))
        journal["cleanupProgress"] = "unresolved"
        try:
            persist_journal(context)
        except Exception:
            errors.append("recovery-journal-update-failed")
        return {
            "status": "failed",
            "errors": list(dict.fromkeys(errors)),
            "listenerStopped": listener_stopped,
            "productionFixtureCredentialAbsent": journal.get("productionFixtureCredentialDeletedAndVerified", False),
            "refreshMarkerAbsent": journal.get("refreshMarkerDeletedAndVerified", False),
            "nativeProbeCredentialAbsent": journal.get("nativeProbeDeletedAndVerified", False),
            "currentUserRootCertificateAbsent": journal.get("trustRemovedAndVerified", False)
                if journal.get("trustImportAttempted") else True,
            "stableLockResidual": None,
            "workspaceRemoved": False,
            "executableSha256After": None,
        }

    lock_path: Path = context["lockPath"]
    residual: dict[str, Any] | None = None
    try:
        if lock_path.exists() or lock_path.is_symlink():
            if has_reparse_attribute(lock_path) or not lock_path.is_file() or lock_path.stat().st_size != 0:
                raise CheckError("The persistent CLI lock is not a stable empty file.")
            expected_identity = journal.get("stableLockIdentity")
            actual_identity = file_identity(lock_path)
            if expected_identity is not None and actual_identity != expected_identity:
                raise CheckError("The persistent CLI lock identity changed.")
            residual = {
                "path": str(lock_path),
                "kind": "intentional-nonsecret-stable-product-lock",
                "removed": False,
                "identity": actual_identity,
            }
        elif journal.get("stableLockIdentity") is not None:
            raise CheckError("The persistent CLI lock disappeared and cannot be verified.")
    except Exception:
        errors.append("stable-lock-residual-unverified")

    executable_hash_after = None
    executable = context.get("executable")
    if executable is not None and executable.exists():
        try:
            executable_hash_after = sha256_file(executable)
            if executable_hash_after != context.get("executableHash"):
                errors.append("extracted-apphost-hash-changed-after-execution")
        except Exception:
            errors.append("extracted-apphost-hash-after-execution-unverified")
    elif context.get("executableHash") is not None:
        errors.append("extracted-apphost-missing-after-execution")

    for value in context["certificateMaterial"].values():
        if isinstance(value, bytearray):
            value[:] = b"\0" * len(value)
    context["certificateMaterial"].clear()
    context["fixtureState"].secrets.clear()

    journal["cleanupProgress"] = "removing-owned-workspace-files"
    journal["cleanupErrors"] = list(dict.fromkeys(errors))
    persist_journal(context)
    preserve_paths = {context["caPath"]} if "current-user-root-removal-unverified" in errors else set()
    workspace_ok, workspace_errors = delete_workspace_paths(
        context,
        keep_journal=True,
        preserve_paths=preserve_paths,
    )
    errors.extend(workspace_errors)
    if not errors and workspace_ok:
        try:
            context["journalPath"].unlink()
            context["workspace"].rmdir()
        except OSError:
            errors.append("owned-workspace-final-removal-failed")

    journal["cleanupCompletedAt"] = utc_now()
    journal["cleanupErrors"] = list(dict.fromkeys(errors))
    journal["cleanupProgress"] = "verified" if not errors else "unresolved"
    if errors and context["workspace"].exists():
        try:
            persist_journal(context)
        except Exception:
            errors.append("recovery-journal-update-failed")
    return {
        "status": "passed" if not errors else "failed",
        "errors": list(dict.fromkeys(errors)),
        "listenerStopped": listener_stopped,
        "productionFixtureCredentialAbsent": journal.get("productionFixtureCredentialDeletedAndVerified", False),
        "refreshMarkerAbsent": journal.get("refreshMarkerDeletedAndVerified", False),
        "nativeProbeCredentialAbsent": journal.get("nativeProbeDeletedAndVerified", False),
        "currentUserRootCertificateAbsent": journal.get("trustRemovedAndVerified", False)
            if journal.get("trustImportAttempted") else True,
        "stableLockResidual": residual,
        "workspaceRemoved": not context["workspace"].exists(),
        "executableSha256After": executable_hash_after,
    }


def preflight(root: Path, source_commit: str, directory: Path, workspace: Path, output: Path) -> dict[str, Any]:
    if os.name != "nt" or package.runtime_rid() != "win-x64" or ctypes.sizeof(ctypes.c_void_p) != 8:
        raise BlockedError("The verifier requires a native Windows x64 host.")
    directory = verify_windows_path(directory, must_exist=True)
    receipt, archive, receipt_path, source_tree = check_candidate(directory, source_commit, root)
    archive = verify_windows_path(archive, must_exist=True)
    receipt_path = verify_windows_path(receipt_path, must_exist=True)
    workspace = Path(os.path.abspath(workspace))
    output = Path(os.path.abspath(output))
    if not workspace.parent.is_dir():
        raise BlockedError("The durable workspace parent must already exist.")
    roots = git_worktree_roots(root)
    reject_source_checkout_path(workspace, roots)
    if workspace.exists() or workspace.is_symlink():
        raise BlockedError("The verifier workspace must be a new, non-reparse path.")
    assert_no_reparse_components(workspace)
    if output == workspace or workspace in output.parents or output in workspace.parents:
        raise BlockedError("The sidecar output and verifier workspace must be separate paths.")
    if not output.parent.is_dir():
        raise BlockedError("The sidecar output directory must already exist.")
    assert_no_reparse_components(output.parent)
    if output.exists() or output.is_symlink():
        raise BlockedError("The sidecar output path must be new.")
    profile = expected_profile(powershell_executable(), reserve_loopback_port())
    account = account_key(
        "https://127.0.0.1:1/",
        "agentweaver-cli",
        "archive-audience-preflight",
        "https://127.0.0.1:1",
        "archive-tenant-preflight",
    )
    if credential_read(credential_target(account)) is not None:
        raise BlockedError("Credential Manager is unavailable or the preflight target unexpectedly exists.")
    return {
        "receipt": receipt,
        "archive": archive,
        "receiptPath": receipt_path,
        "sourceTree": source_tree,
        "profile": profile,
        "powershell": powershell_executable(),
    }


def verify_and_run(args: argparse.Namespace) -> int:
    run_id = str(uuid.uuid4())
    started = utc_now()
    output = Path(os.path.abspath(args.output))
    if output.name != SIDECAR_NAME:
        print(f"Output file must be named {SIDECAR_NAME}.", file=sys.stderr)
        return 1
    source_commit = args.source_commit
    result = initial_sidecar(run_id, source_commit, None, started, "failed")
    context: dict[str, Any] | None = None
    server: FixtureServer | None = None
    worker: threading.Thread | None = None
    failure: str | None = None
    blocked = False
    cleanup: dict[str, Any] | None = None

    try:
        root = package.ROOT.resolve()
        directory = Path(os.path.abspath(args.directory))
        workspace = Path(os.path.abspath(args.workspace))
        pre = preflight(root, source_commit, directory, workspace, output)
        receipt: dict[str, Any] = pre["receipt"]
        archive: Path = pre["archive"]
        receipt_path: Path = pre["receiptPath"]
        profile: dict[str, Any] = pre["profile"]
        powershell: Path = pre["powershell"]
        source_tree = pre["sourceTree"]
        result["source"]["tree"] = source_tree
        result["host"]["logonSid"] = profile["sid"]

        port = reserve_loopback_port()
        origin = f"https://127.0.0.1:{port}"
        issuer = origin + "/"
        tenant = "archive-tenant-" + secrets.token_hex(8)
        audience = "archive-audience-" + secrets.token_hex(8)
        actor = "archive-fixture-actor"
        account = account_key(issuer, "agentweaver-cli", audience, origin, tenant)
        account_target = credential_target(account)
        state_directory, lock_path, marker_path = account_state_paths(profile["localAppData"], account)
        assert_no_reparse_components(lock_path)
        assert_no_reparse_components(marker_path)
        if (
            credential_read(account_target) is not None
            or lock_path.exists()
            or lock_path.is_symlink()
            or marker_path.exists()
            or marker_path.is_symlink()
        ):
            raise BlockedError("The unique production credential target or exact state paths already exist.")

        native_probe_target = "Agentweaver.Cli.ArchiveProbe.v1:" + run_id.replace("-", "")
        if credential_read(native_probe_target) is not None:
            raise BlockedError("The unique native vault probe target already exists.")

        certificate_path = workspace / "certificates" / "test-root.cer"
        identity = {
            "issuer": issuer,
            "clientId": "agentweaver-cli",
            "audience": audience,
            "gateway": origin,
            "tenant": tenant,
            "scopes": ["openid", "api.read", "offline_access"],
            "projectId": None,
            "runId": None,
            "account": account,
            "credentialTarget": account_target,
            "lockPath": str(lock_path),
            "refreshMarkerPath": str(marker_path),
        }
        journal = {
            "schemaVersion": 1,
            "runId": run_id,
            "workspace": str(workspace),
            "workspaceIdentity": None,
            "logonSid": profile["sid"],
            "controllerProcess": {
                "pid": os.getpid(),
                "startTimeFiletime": process_start_ticks(os.getpid()),
            },
            "identity": identity,
            "credentialTarget": account_target,
            "nativeProbeTarget": native_probe_target,
            "nativeProbeAcquisitionAttempted": False,
            "certificateThumbprint": None,
            "certificateNotAfter": None,
            "certificatePath": str(certificate_path),
            "leafCertificatePath": str(workspace / "certificates" / "server.cer.pem"),
            "leafPrivateKeyPath": str(workspace / "certificates" / "server.key.pem"),
            "archive": str(archive),
            "archiveSha256": sha256_file(archive),
            "receipt": str(receipt_path),
            "receiptSha256": sha256_file(receipt_path),
            "sourceCommit": source_commit,
            "sourceTree": source_tree,
            "executablePath": str(workspace / "extracted" / "agentweaver.exe"),
            "childWorkingDirectory": str(workspace / "child-temp"),
            "createdPaths": planned_path_list(workspace, receipt),
            "processIdentities": [],
            "trustImportAttempted": False,
            "credentialSeedAttempted": False,
            "cleanupProgress": "journal-created",
        }
        context = {
            "runId": run_id,
            "startedAt": started,
            "deadline": time.monotonic() + 600,
            "workspace": workspace,
            "journal": journal,
            "journalPath": workspace / "journal.json",
            "journalTemporaryPath": workspace / "journal.json.new",
            "powershell": powershell,
            "receipt": receipt,
            "archive": archive,
            "receiptPath": receipt_path,
            "sourceCommit": source_commit,
            "sourceTree": source_tree,
            "profile": profile,
            "origin": origin,
            "issuer": issuer,
            "tenant": tenant,
            "audience": audience,
            "account": account,
            "credentialTarget": account_target,
            "stateDirectory": state_directory,
            "lockPath": lock_path,
            "markerPath": marker_path,
            "probeTarget": native_probe_target,
            "caPath": certificate_path,
            "leafCertificatePath": Path(journal["leafCertificatePath"]),
            "leafPrivateKeyPath": Path(journal["leafPrivateKeyPath"]),
            "childWorkingDirectory": Path(journal["childWorkingDirectory"]),
            "children": [],
            "commands": [],
            "assertions": {},
            "certificateMaterial": {},
            "fixtureState": FIXTURE_STATE(origin, tenant, actor, "agentweaver-cli"),
            "runtimeTest": receipt.get("runtimeTest"),
            "runStarted": False,
            "journalReady": False,
        }
        secure_workspace(workspace, profile["sid"], powershell, context)
        journal["workspaceIdentity"] = file_identity(workspace)
        context["journalReady"] = True
        persist_journal(context)
        context["childWorkingDirectory"].mkdir()
        (workspace / "fixtures").mkdir()
        (workspace / "certificates").mkdir()
        persist_journal(context)

        executable = package.extract_archive(archive, "win-x64", workspace / "extracted")
        executable = executable.resolve()
        expected_apphost = next(item for item in receipt["files"] if item["path"] == "agentweaver.exe")
        executable_hash = sha256_file(executable)
        if executable_hash != expected_apphost["sha256"]:
            raise CheckError("The extracted apphost hash does not match the verified archive inventory.")
        context["executable"] = executable
        context["executableHash"] = executable_hash
        context["journal"]["executablePath"] = str(executable)
        context["journal"]["executableSha256"] = executable_hash
        persist_journal(context)

        certificate = make_certificates(powershell, run_id, context)
        context["certificateMaterial"] = certificate
        journal["certificateThumbprint"] = certificate["thumbprint"]
        journal["certificateNotAfter"] = certificate["notAfter"]
        persist_journal(context)
        try:
            run_powershell(
                powershell,
                TRUST_CHECK_SCRIPT,
                {"ARCHIVE_THUMBPRINT": certificate["thumbprint"]},
                timeout=5,
                workspace=context["childWorkingDirectory"],
                context=context,
                role="current-user-root-precheck",
            )
        except CheckError as error:
            raise BlockedError("The unique CurrentUser\\Root certificate thumbprint is not available.") from error

        write_new(certificate_path, bytes(certificate["caDer"]))
        write_new(context["leafCertificatePath"], bytes(certificate["leafCertificatePem"]))
        write_new(context["leafPrivateKeyPath"], bytes(certificate["leafPrivateKeyPem"]))
        if hashlib.sha1(certificate_path.read_bytes()).hexdigest().upper() != certificate["thumbprint"]:
            raise CheckError("The stored test-root certificate does not match its recorded thumbprint.")

        listener_port = port
        state: FIXTURE_STATE = context["fixtureState"]
        tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        tls.minimum_version = ssl.TLSVersion.TLSv1_2
        tls.load_cert_chain(str(context["leafCertificatePath"]), str(context["leafPrivateKeyPath"]))
        server = FixtureServer(listener_port, state, tls)
        if server.server_address != ("127.0.0.1", listener_port):
            raise CheckError("The controlled HTTPS fixture is not bound to the exact loopback address.")
        context["serverTls"] = tls
        worker = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.1}, daemon=True)
        context["journal"]["listener"] = {
            "address": "127.0.0.1",
            "port": listener_port,
            "ownerPid": os.getpid(),
            "ownerStartTimeFiletime": journal["controllerProcess"]["startTimeFiletime"],
        }
        context["journal"]["cleanupProgress"] = "listener-intent-recorded"
        persist_journal(context)

        context["runStarted"] = True
        worker.start()
        ready_deadline = time.monotonic() + 5
        while time.monotonic() < ready_deadline:
            if worker.is_alive() and server.socket.fileno() >= 0:
                break
            time.sleep(0.05)
        else:
            raise BlockedError("The controlled HTTPS fixture did not become ready.")

        trust_env = {
            "ARCHIVE_CA_FILE": str(certificate_path),
            "ARCHIVE_THUMBPRINT": certificate["thumbprint"],
        }
        context["journal"]["trustImportAttempted"] = True
        context["journal"]["cleanupProgress"] = "current-user-root-import-intended"
        persist_journal(context)
        run_powershell(
            powershell,
            TRUST_IMPORT_SCRIPT,
            trust_env,
            timeout=5,
            workspace=context["childWorkingDirectory"],
            context=context,
            role="current-user-root-import",
        )
        context["journal"]["trustImportedAndVerified"] = True
        persist_journal(context)

        native_result = native_vault_probe(context)
        result["nativeVaultApiProbe"] = native_result
        environment = target_environment(profile, workspace, origin, issuer, tenant, audience)
        context["executable"] = executable
        context["childEnvironment"] = environment
        context["commands"] = []
        context["runStarted"] = True
        result["archive"] = {
            "name": archive.name,
            "sha256": context["journal"]["archiveSha256"],
            "receiptSha256": context["journal"]["receiptSha256"],
            "sizeBytes": archive.stat().st_size,
            "inventory": receipt["files"],
        }
        result["existingRuntimeTest"] = receipt.get("runtimeTest")
        result["executable"] = {"path": "agentweaver.exe", "sha256": executable_hash}
        result["controlledArchiveChecks"]["source"] = {"commit": source_commit, "tree": source_tree}
        result["controlledArchiveChecks"]["status"] = "failed"

        assertions = run_controlled_sequence(context, origin, issuer, tenant, audience)
        context["assertions"] = assertions

    except BlockedError as error:
        blocked = True
        failure = str(error)
    except BaseException as error:
        failure = str(error) or type(error).__name__
        if context is not None:
            context["fixtureState"].secrets.clear()
    finally:
        if context is not None:
            try:
                cleanup = (
                    cleanup_run(context, server, worker)
                    if context.get("journalReady")
                    else cleanup_unjournaled_workspace(context)
                )
            except BaseException:
                cleanup = {
                    "status": "failed",
                    "errors": ["cleanup-controller-failed"],
                    "listenerStopped": False,
                    "productionFixtureCredentialAbsent": False,
                    "refreshMarkerAbsent": False,
                    "nativeProbeCredentialAbsent": False,
                    "currentUserRootCertificateAbsent": False,
                    "stableLockResidual": None,
                    "workspaceRemoved": False,
                    "executableSha256After": None,
                }
                failure = failure or "Owned resource cleanup raised an unhandled error."
        elif "certificate" in locals():
            for value in certificate.values():
                if isinstance(value, bytearray):
                    value[:] = b"\0" * len(value)

    if cleanup is not None and cleanup["status"] != "passed":
        blocked = False
        failure = failure or "Owned native cleanup was incomplete or uncertain."
    elif failure and not blocked:
        blocked = False

    if context is not None:
        result["runId"] = run_id
        result["host"]["logonSid"] = context["profile"]["sid"]
        result["source"] = {"commit": source_commit, "tree": context["sourceTree"]}
        result["archive"] = {
            "name": context["archive"].name,
            "sha256": context["journal"]["archiveSha256"],
            "receiptSha256": context["journal"]["receiptSha256"],
            "sizeBytes": context["archive"].stat().st_size,
            "inventory": context["receipt"]["files"],
        }
        executable_hash_before = context.get("executableHash")
        executable_hash_after = cleanup.get("executableSha256After") if cleanup is not None else None
        result["executable"] = {
            "path": "agentweaver.exe",
            "sha256Before": executable_hash_before,
            "sha256After": executable_hash_after,
        }
        if executable_hash_before is not None and executable_hash_after != executable_hash_before:
            failure = failure or "The extracted apphost hash changed during execution."
        result["existingRuntimeTest"] = context["runtimeTest"]
        result["controlledArchiveChecks"]["commands"] = context["commands"]
        result["controlledArchiveChecks"]["assertions"] = context["assertions"]
        result["controlledArchiveChecks"]["fixtureCalls"] = context["fixtureState"].calls_snapshot()
        result["controlledArchiveChecks"]["fixtureAssertions"] = context["fixtureState"].assertions_snapshot()
        result["controlledArchiveChecks"]["nativeAdapter"] = {
            "kind": "production-Windows-vault-through-extracted-apphost",
            "status": "passed" if cleanup and cleanup["productionFixtureCredentialAbsent"] and not failure else "failed",
            "refreshExchanges": len([
                call for call in context["fixtureState"].calls_snapshot()
                if call["path"] == "/connect/token"
            ]),
            "logoutRemovalVerified": bool(cleanup and cleanup["productionFixtureCredentialAbsent"]),
        }
        result["controlledArchiveChecks"]["cleanup"] = cleanup
        if failure:
            result["controlledArchiveChecks"]["status"] = "failed"
            result["controlledArchiveChecks"]["reason"] = failure
        elif cleanup and cleanup["status"] == "passed":
            result["controlledArchiveChecks"]["status"] = "passed"
        else:
            result["controlledArchiveChecks"]["status"] = "failed"
            result["controlledArchiveChecks"]["reason"] = "Cleanup completion could not be verified."
    else:
        result["controlledArchiveChecks"]["status"] = "blocked" if blocked else "failed"
        if failure:
            result["controlledArchiveChecks"]["reason"] = failure
        if source_commit:
            result["source"]["commit"] = source_commit

    fail_sidecar_if_cleanup_unverified(result, cleanup)
    if failure and blocked and context is None:
        result["controlledArchiveChecks"]["status"] = "blocked"
    if failure and not blocked and result["controlledArchiveChecks"]["status"] == "passed":
        result["controlledArchiveChecks"]["status"] = "failed"
        result["controlledArchiveChecks"]["reason"] = failure
    result["completedAt"] = utc_now()

    try:
        save_sidecar(output, result)
    except Exception:
        print("Could not write the new sanitized archive-check sidecar.", file=sys.stderr)
        return 1
    status = result["controlledArchiveChecks"]["status"]
    if status == "passed":
        print("Windows archive checks passed; native journey proof is unrun and #2449 remains open.")
        return 0
    if status == "blocked":
        print("Windows archive checks were blocked before native test resources were acquired.")
        return 2
    print("Windows archive checks failed; inspect only the sanitized sidecar and retained recovery journal.")
    return 1


def cleanup_only(workspace_argument: Path, output_argument: Path) -> int:
    workspace = Path(os.path.abspath(workspace_argument))
    output = Path(os.path.abspath(output_argument))
    if os.name != "nt":
        raise BlockedError("Windows archive recovery is available only on Windows.")
    if not output.parent.is_dir() or output.exists() or output.is_symlink():
        raise CheckError("Recovery requires a new output file in an existing directory.")
    assert_no_reparse_components(output.parent)
    assert_no_reparse_components(workspace)
    if not workspace.is_dir() or has_reparse_attribute(workspace):
        raise CheckError("The recovery workspace is missing or unsafe.")
    journal_path = workspace / "journal.json"
    if has_reparse_attribute(journal_path):
        raise CheckError("The recovery journal is a reparse point.")
    journal = json.loads(journal_path.read_text(encoding="utf-8"))
    if not isinstance(journal, dict) or journal.get("schemaVersion") != 1:
        raise CheckError("The recovery journal is not a recognized ownership record.")
    if Path(journal.get("workspace", "")).resolve() != workspace.resolve():
        raise CheckError("The recovery journal does not own this workspace.")
    expected_identity = journal.get("workspaceIdentity")
    if not isinstance(expected_identity, dict) or file_identity(workspace) != expected_identity:
        raise CheckError("The recovery workspace identity changed.")
    if not isinstance(journal.get("logonSid"), str):
        raise CheckError("The recovery journal has no logon SID.")
    context = {
        "journal": journal,
        "workspace": workspace,
        "journalPath": journal_path,
        "journalTemporaryPath": workspace / "journal.json.new",
        "children": [],
        "commands": [],
        "assertions": {},
        "certificateMaterial": {},
        "journalReady": True,
    }
    profile = json.loads(run_powershell(
        powershell_executable(),
        "$ErrorActionPreference='Stop'; [ordered]@{sid=[System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value; localAppData=[Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)} | ConvertTo-Json -Compress",
        timeout=5,
        workspace=workspace / "child-temp",
        context=context,
        role="recovery-profile-check",
    ))
    if profile.get("sid") != journal["logonSid"]:
        raise CheckError("Recovery must run under the original Windows logon SID.")
    controller = journal.get("controllerProcess", {})
    if process_is_alive(int(controller.get("pid", -1)), int(controller.get("startTimeFiletime", -1))):
        raise CheckError("The original archive-check controller is still running; recovery did not mutate resources.")

    origin_identity = journal.get("identity", {})
    account = origin_identity.get("account")
    expected_target = credential_target(str(account))
    local_app_data = Path(profile["localAppData"])
    expected_directory, expected_lock, expected_marker = account_state_paths(str(local_app_data), str(account))
    if (
        journal.get("credentialTarget") != expected_target
        or origin_identity.get("credentialTarget") != expected_target
        or Path(origin_identity.get("lockPath", "")).resolve(strict=False) != expected_lock.resolve(strict=False)
        or Path(origin_identity.get("refreshMarkerPath", "")).resolve(strict=False) != expected_marker.resolve(strict=False)
    ):
        raise CheckError("The recorded native credential and state paths do not match the exact current-user account.")
    native_probe_target = journal.get("nativeProbeTarget")
    if native_probe_target != "Agentweaver.Cli.ArchiveProbe.v1:" + str(journal.get("runId", "")).replace("-", ""):
        raise CheckError("The native probe target is not owned by this archive run.")
    if origin_identity.get("projectId") is not None or origin_identity.get("runId") is not None:
        raise CheckError("The recovery credential is not an unbound tenant fixture.")
    if origin_identity.get("scopes") != ["openid", "api.read", "offline_access"] or account != account_key(
        str(origin_identity.get("issuer", "")),
        str(origin_identity.get("clientId", "")),
        str(origin_identity.get("audience", "")),
        str(origin_identity.get("gateway", "")),
        str(origin_identity.get("tenant", "")),
    ):
        raise CheckError("The recovery account does not match the canonical fixture identity.")
    created_paths = journal.get("createdPaths")
    if not isinstance(created_paths, list):
        raise CheckError("The recovery journal has no exact created-path inventory.")
    for raw_path in created_paths:
        if not isinstance(raw_path, str):
            raise CheckError("The recovery journal contains an invalid created path.")
        created_path = Path(raw_path)
        resolved = created_path.resolve(strict=False)
        if resolved == workspace.resolve() or workspace.resolve() not in resolved.parents:
            raise CheckError("A recovery created-path entry escaped the owned workspace.")
        assert_no_reparse_components(created_path)
    certificate_thumbprint = journal.get("certificateThumbprint")
    if journal.get("trustImportAttempted") and (
        not isinstance(certificate_thumbprint, str) or len(certificate_thumbprint) != 40
    ):
        raise CheckError("The recorded CurrentUser\\Root identity is invalid.")
    if certificate_thumbprint is not None and (
        not isinstance(certificate_thumbprint, str) or len(certificate_thumbprint) != 40
    ):
        raise CheckError("The recorded certificate thumbprint is invalid.")
    ca_path = Path(journal.get("certificatePath", ""))
    leaf_key = Path(journal.get("leafPrivateKeyPath", ""))
    allowed_workspace = workspace.resolve()
    for path in (ca_path, leaf_key, Path(journal.get("leafCertificatePath", ""))):
        if path.resolve(strict=False) != allowed_workspace and allowed_workspace not in path.resolve(strict=False).parents:
            raise CheckError("The recovery journal contains a path outside its owned workspace.")
        assert_no_reparse_components(path)

    errors: list[str] = []
    recovered_processes: list[dict[str, Any]] = []
    for identity in journal.get("processIdentities", []):
        pid = int(identity.get("pid", -1))
        start = int(identity.get("startTimeFiletime", -1))
        try:
            stopped = terminate_owned_process(pid, start)
            recovered_processes.append({"pid": pid, "stopped": stopped})
        except Exception:
            errors.append("owned-process-identity-or-shutdown-unverified")

    powershell = powershell_executable()
    context.update({
        "powershell": powershell,
        "caPath": ca_path,
        "lockPath": expected_lock,
        "markerPath": expected_marker,
        "credentialTarget": expected_target,
        "fixtureState": FIXTURE_STATE(
            str(origin_identity.get("gateway", "")),
            str(origin_identity.get("tenant", "")),
            "archive-fixture-actor",
            "agentweaver-cli",
        ),
    })
    result = initial_sidecar(
        str(journal.get("runId")),
        journal.get("sourceCommit"),
        journal.get("sourceTree"),
        journal.get("startedAt", utc_now()),
        "failed",
        "cleanup-only recovery does not rerun or pass the archive checks.",
    )
    result["host"]["logonSid"] = profile["sid"]
    if "owned-process-identity-or-shutdown-unverified" in errors:
        journal["cleanupProgress"] = "unresolved"
        journal["cleanupErrors"] = list(dict.fromkeys(errors))
        try:
            write_journal(journal_path, workspace / "journal.json.new", journal)
        except Exception:
            errors.append("recovery-journal-update-failed")
        result["controlledArchiveChecks"]["status"] = "failed"
        result["controlledArchiveChecks"]["reason"] = "Recovery could not verify shutdown of every owned process."
        result["controlledArchiveChecks"]["cleanup"] = {
            "status": "failed",
            "errors": list(dict.fromkeys(errors)),
            "recoveredProcesses": recovered_processes,
            "productionFixtureCredentialAbsent": False,
            "refreshMarkerAbsent": False,
            "nativeProbeCredentialAbsent": False,
            "currentUserRootCertificateAbsent": False,
            "stableLockResidual": None,
            "workspaceRemoved": False,
        }
        result["controlledArchiveChecks"]["fixtureCalls"] = []
        result["nativeJourneyProof"]["status"] = "not-run"
        result["issue2449Acceptance"] = "incomplete"
        result["completedAt"] = utc_now()
        save_sidecar(output, result)
        print("Recovery could not stop every owned process; native resources and the journal were preserved.", file=sys.stderr)
        return 1
    try:
        if journal.get("credentialSeedAttempted"):
            credential_delete(expected_target)
            if credential_read(expected_target) is not None:
                errors.append("production-fixture-credential-cleanup-unverified")
            else:
                journal["productionFixtureCredentialDeletedAndVerified"] = True
        elif credential_read(expected_target) is not None:
            errors.append("unexpected-credential-appeared-at-unowned-target")
    except Exception:
        errors.append("production-fixture-credential-cleanup-unverified")
    try:
        if journal.get("credentialSeedAttempted"):
            cleanup_marker(expected_marker)
            journal["refreshMarkerDeletedAndVerified"] = True
        elif expected_marker.exists():
            errors.append("unexpected-marker-appeared-at-unowned-state-path")
    except Exception:
        errors.append("refresh-marker-cleanup-unverified")
    try:
        if journal.get("nativeProbeAcquisitionAttempted"):
            credential_delete(native_probe_target)
            if credential_read(native_probe_target) is not None:
                errors.append("native-probe-credential-cleanup-unverified")
            else:
                journal["nativeProbeDeletedAndVerified"] = True
        elif credential_read(native_probe_target) is not None:
            errors.append("unexpected-credential-appeared-at-native-probe-target")
    except Exception:
        errors.append("native-probe-credential-cleanup-unverified")
    if journal.get("trustImportAttempted"):
        try:
            run_powershell(
                powershell,
                TRUST_REMOVE_SCRIPT,
                {
                    "ARCHIVE_CA_FILE": str(ca_path),
                    "ARCHIVE_THUMBPRINT": certificate_thumbprint,
                },
                timeout=5,
                workspace=workspace / "child-temp",
                context=context,
                role="recovery-current-user-root-removal",
            )
            journal["trustRemovedAndVerified"] = True
        except Exception:
            errors.append("current-user-root-removal-unverified")
    else:
        journal["trustRemovedAndVerified"] = True

    lock_residual = None
    try:
        if expected_lock.exists():
            if has_reparse_attribute(expected_lock) or not expected_lock.is_file() or expected_lock.stat().st_size != 0:
                raise CheckError("The stable product lock is not an empty regular file.")
            expected_lock_identity = journal.get("stableLockIdentity")
            if expected_lock_identity and file_identity(expected_lock) != expected_lock_identity:
                raise CheckError("The stable product lock identity changed.")
            lock_residual = {"path": str(expected_lock), "kind": "intentional-nonsecret-stable-product-lock", "removed": False}
    except Exception:
        errors.append("stable-lock-residual-unverified")

    helper_errors = stop_owned_children(context)
    errors.extend(helper_errors)

    journal["cleanupProgress"] = "recovered" if not errors else "unresolved"
    journal["cleanupErrors"] = list(dict.fromkeys(errors))
    try:
        write_journal(journal_path, workspace / "journal.json.new", journal)
    except Exception:
        errors.append("recovery-journal-update-failed")
    process_unresolved = any(error in errors for error in (
        "owned-process-identity-or-shutdown-unverified",
        "owned-cli-or-helper-process-did-not-stop",
    ))
    preserve_paths = {ca_path} if "current-user-root-removal-unverified" in errors else set()
    if not process_unresolved:
        _workspace_ok, cleanup_errors = delete_workspace_paths(
            context,
            keep_journal=True,
            preserve_paths=preserve_paths,
        )
        errors.extend(cleanup_errors)
        if not errors:
            try:
                journal_path.unlink()
                workspace.rmdir()
            except OSError:
                errors.append("owned-workspace-final-removal-failed")
        elif workspace.exists():
            journal["cleanupProgress"] = "unresolved"
            journal["cleanupErrors"] = list(dict.fromkeys(errors))
            try:
                write_journal(journal_path, workspace / "journal.json.new", journal)
            except Exception:
                errors.append("recovery-journal-update-failed")
    cleanup = {
        "status": "passed" if not errors else "failed",
        "errors": list(dict.fromkeys(errors)),
        "recoveredProcesses": recovered_processes,
        "productionFixtureCredentialAbsent": journal.get("productionFixtureCredentialDeletedAndVerified", False),
        "refreshMarkerAbsent": journal.get("refreshMarkerDeletedAndVerified", False),
        "nativeProbeCredentialAbsent": journal.get("nativeProbeDeletedAndVerified", False),
        "currentUserRootCertificateAbsent": journal.get("trustRemovedAndVerified", False),
        "stableLockResidual": lock_residual,
        "workspaceRemoved": not workspace.exists(),
    }
    result["controlledArchiveChecks"]["status"] = "failed"
    result["controlledArchiveChecks"]["reason"] = "Cleanup-only recovery preserves the original interrupted check as failed."
    result["controlledArchiveChecks"]["cleanup"] = cleanup
    result["controlledArchiveChecks"]["fixtureCalls"] = []
    result["nativeJourneyProof"]["status"] = "not-run"
    result["issue2449Acceptance"] = "incomplete"
    result["completedAt"] = utc_now()
    save_sidecar(output, result)
    print("Recovery completed; the interrupted archive check remains failed and #2449 remains open.")
    return 1


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path)
    parser.add_argument("--source-commit")
    parser.add_argument("--workspace", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--cleanup-only", action="store_true")
    args = parser.parse_args(argv)
    try:
        if args.cleanup_only:
            if args.directory is not None or args.source_commit is not None:
                raise CheckError("--cleanup-only does not accept an archive directory or source commit.")
            return cleanup_only(args.workspace, args.output)
        if args.directory is None or args.source_commit is None:
            raise CheckError("A candidate directory and full expected source commit are required.")
        return verify_and_run(args)
    except BlockedError as error:
        print(f"Blocked: {error}", file=sys.stderr)
        return 2
    except (CheckError, OSError, ValueError) as error:
        print(f"Windows archive check failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
