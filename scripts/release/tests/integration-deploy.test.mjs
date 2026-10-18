import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash, X509Certificate } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { run as runProcess } from '../../azure/lib/exec.mjs';
import {
  deployIntegration,
  parseIntegrationArgs,
  verifyIntegrationPublicationRemote,
} from '../deploy-integration.mjs';
import { verifyBrokerRunBindingConfiguration } from '../deploy.mjs';
import { componentImageRepository } from '../pack.mjs';
import { WEB_LOCK_PATH, WEB_PROJECT_PATH } from '../validate.mjs';
import { agentHostComponent } from './agenthost-fixture.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const sourceSha = 'a'.repeat(40);
const sourceTree = 'b'.repeat(40);
const target = {
  subscriptionId: '90ab3701-83f0-4ba1-a90a-f2e68683adab',
  tenantId: '72f988bf-86f1-41af-91ab-2d7cd011db47',
  resourceGroup: 'aw-v1-p0',
  clusterName: 'aw-v1-p0-aks',
  namespace: 'agentweaver-v1-p0',
  nodeMin: 2,
  nodeMax: 3,
};
const productServices = [
  { id: 'Agentweaver.AgentHost', project: agentHostComponent.project },
  { id: 'Agentweaver.Identity.Broker', project: 'services/identity/Agentweaver.Identity.Broker/Agentweaver.Identity.Broker.csproj' },
  { id: 'Agentweaver.EventsAndSessions', project: 'services/events-and-sessions/Agentweaver.EventsAndSessions/Agentweaver.EventsAndSessions.csproj' },
  { id: 'Agentweaver.Gateway', project: 'services/gateway/Agentweaver.Gateway/Agentweaver.Gateway.csproj' },
  { id: 'Agentweaver.Mcp', project: 'services/mcp/Agentweaver.Mcp/Agentweaver.Mcp.csproj' },
  { id: 'Agentweaver.Web', project: WEB_PROJECT_PATH },
  { id: 'Agentweaver.Orchestrator', project: 'services/orchestrator/Agentweaver.Orchestrator/Agentweaver.Orchestrator.csproj' },
  { id: 'Agentweaver.Environment', project: 'services/environment/Agentweaver.Environment/Agentweaver.Environment.csproj' },
  { id: 'Agentweaver.Knowledge', project: 'services/knowledge/Agentweaver.Knowledge/Agentweaver.Knowledge.csproj' },
  { id: 'Agentweaver.Projects.Config', project: 'services/projects-config/Agentweaver.Projects.Config/Agentweaver.Projects.Config.csproj' },
].map(component => ({ ...component, kind: 'service', version: '1.0.0' }));
const sha256 = value => createHash('sha256').update(value).digest('hex');
const digest = value => `sha256:${value.repeat(64)}`;
const powershellVersion = spawnSync('pwsh', [
  '-NoProfile', '-NonInteractive', '-Command', '$PSVersionTable.PSVersion.Major',
], { encoding: 'utf8', windowsHide: true });
const hasPowerShell7 = powershellVersion.status === 0 && Number(powershellVersion.stdout.trim()) >= 7;
const testRootPem = `-----BEGIN CERTIFICATE-----
MIIDCDCCAfCgAwIBAgIJAJu/cTE3IcFJMA0GCSqGSIb3DQEBCwUAMCAxHjAcBgNV
BAMTFUFnZW50d2VhdmVyIFRlc3QgUm9vdDAeFw0yMDAxMDEwMDAwMDBaFw00NTAx
MDEwMDAwMDBaMCAxHjAcBgNVBAMTFUFnZW50d2VhdmVyIFRlc3QgUm9vdDCCASIw
DQYJKoZIhvcNAQEBBQADggEPADCCAQoCggEBANEtxS7A7hVbLPt4odOgrDqWY2wW
IxH37EW4jsFfSDHmcscRbwk78Z+APhkW6eYHkrzOPOxBpoF6V0DDBiC67j8EvgLB
q2y1kcYwjOwYunfXN15RPIxGzwXSA0BLKjVyzndlWHAto6ujxWz1fzHTAoe2usZD
khnf4Zt9IhGqkS5xb9ZOHG4I79yS8NKHYEFvSQRKHMKHvUje4heq6VU6kqYPlxVB
8mjDw9vdbTtgwCV6DRS3/NvduTyKvdlpVnP1cwgBLfC1f8xvt7SoR2dd3Fptz739
ehdaTxaczeGRMM23UBifup0coGedmlzNN7+bK8pG+y/0Dv6Su69DB2KccHkCAwEA
AaNFMEMwEgYDVR0TAQH/BAgwBgEB/wIBATAOBgNVHQ8BAf8EBAMCAQYwHQYDVR0O
BBYEFO2ruiU5Lh2RXKFSU3V8g9Zj9sgwMA0GCSqGSIb3DQEBCwUAA4IBAQAjMXji
6bZ540KjozNzAdi9zuOCxPuuV/YL/nKWKAgBmJR1WCuKNQm43QCcuafo4KF4vP1K
3VEVO2UC0zuanDz3Uw4d81WD1xWqsmD7I5kylhlcx7qwCp+pEqXZClrzB8JHXP7h
Avv08daph9oWz+olXvHvvdJ+aa9ozzP007LIidxAd2fK+3iEtilCAz6wPtJsQYL/
P/Wq/AhsTiec+/DWo/fke5Rkr2cwas1KmKBxjcnTStubNL1F7ntyCjcsurHRmjy5
8KkpDOE/hkcKRWOR+uOfTLSS7EMi7xXu7rp/Xcw3fZeln8wyeuT9WMjM0wj8NSxo
4PL40wBo10CJDzkO
-----END CERTIFICATE-----
`;
const testIntermediatePem = `-----BEGIN CERTIFICATE-----
MIIDDzCCAfegAwIBAgIIHyAhIiMkJSYwDQYJKoZIhvcNAQELBQAwIDEeMBwGA1UE
AxMVQWdlbnR3ZWF2ZXIgVGVzdCBSb290MB4XDTIwMDEwMTAwMDAwMFoXDTQ1MDEw
MTAwMDAwMFowKDEmMCQGA1UEAxMdQWdlbnR3ZWF2ZXIgVGVzdCBJbnRlcm1lZGlh
dGUwggEiMA0GCSqGSIb3DQEBAQUAA4IBDwAwggEKAoIBAQCfHb6gLl8eo+lgLuXU
/mDbPkMnzgh2Asmi1fRtoCh6h7zUJhC3M40qKLc4yzrLB6sw2t+d/8cs5rR5FF6J
slErQtCociC3R2/rRZMwmpq3BrwuJjAY+/381eCbRZEgZl8GtkZUVkrIGBAbUrxj
0PN4aPbBhYDVMvQeIdnvqHC0+KLgAOEEpy/MydBkOycTbCjJJ9FR10qg5LJY/Y6E
JeyiVB/G+XWcfz2Ky8NHj0mpSaluTsATMGsHaFQeDI8zX+EEWCW8vf8Thj89zTBW
uq/3XD8h24BRuyxeUc/40hIqcswHlo/ZphMOkmzjzAuBe2BEolPQXkjY52ycXnkY
FfkVAgMBAAGjRTBDMBIGA1UdEwEB/wQIMAYBAf8CAQAwDgYDVR0PAQH/BAQDAgEG
MB0GA1UdDgQWBBRqsGK5Lrbzfzg+sUjDcQICo4gyEjANBgkqhkiG9w0BAQsFAAOC
AQEAbQVsff+gq5TfgMwDFiJR5xhjxoLo/Ocdi5XE3m6V67sJL2I8AkS3agnKBAmv
r6citf/Ok/Mlwzw/x/K22jhlQhH2W3tkIbHURFbJJeOGCd17BaXsnZocplcxWc7F
FhOj6Kn2LAX4OFFXRPwCymdXU83V5mplDKgCw3ok/Cz0Z6iLQ1bSvudCYGoswwTC
CnEcOd1fJpmi4HzEzfN179PsMWSqUHZ/T0Lpl3qoCRUeDrPP/wD4nAwbza6EM9Zk
vl+wRQg17sCWu/mJVxg6/IicYgLMIvPAS4g4DyYEaP2wijctdbFLa/x59l0xVUsS
8CztiMbqS8mM86Z4jeDGiFsyiw==
-----END CERTIFICATE-----
`;
const testServerLeafPem = `-----BEGIN CERTIFICATE-----
MIIDYDCCAkigAwIBAgIIKSorLC0uLzAwDQYJKoZIhvcNAQELBQAwKDEmMCQGA1UE
AxMdQWdlbnR3ZWF2ZXIgVGVzdCBJbnRlcm1lZGlhdGUwHhcNMjAwMTAxMDAwMDAw
WhcNNDUwMTAxMDAwMDAwWjA+MTwwOgYDVQQDEzNwcm9qZWN0cy1jb25maWcuYWdl
bnR3ZWF2ZXItdjEtcDAuc3ZjLmNsdXN0ZXIubG9jYWwwggEiMA0GCSqGSIb3DQEB
AQUAA4IBDwAwggEKAoIBAQCuIuEXVMF2YU4JNDhWTrLNcaZJMPG/2f2sI+XsrzO1
P4yIPDnwMFKm+Dqwh8pP+ABQjXqfU/hUs1y5xMHj5UEufts8r4a+ZaZL5UMsocgQ
+2FCavYvptG3bvJZa8u2bQAVfLa6VPCBWRipzR4H7FwsGXbtwJ/CKDXSwO9ul0X8
fj1uQ0NI2DRiB+UWIwH+It3hqwAkgKcVz6lt+SGrW1WSoBWO0USmEnKP02JydXb6
NtoN7NsVDHAmDm1TtydDdIXvjxM42SLGuzbfvdiNuSpPqtWDaug9ILH4BUJltRts
TQeW4R8yF8BVmjeosETiySmjp16MblEjDF21Co0hB5x1AgMBAAGjeDB2MAwGA1Ud
EwEB/wQCMAAwDgYDVR0PAQH/BAQDAgWgMBYGA1UdJQEB/wQMMAoGCCsGAQUFBwMB
MD4GA1UdEQQ3MDWCM3Byb2plY3RzLWNvbmZpZy5hZ2VudHdlYXZlci12MS1wMC5z
dmMuY2x1c3Rlci5sb2NhbDANBgkqhkiG9w0BAQsFAAOCAQEAKf3g8JY586H87v/r
nSFN439gKH42zR0swJGoOnqmyTDAg0t7nlC7tY4K853e+YZwS3flpG8EQ2R70yeC
3hEh/KtCK8XZj1H3oiYQRUlONEHVL8bh5zXIrMv91Jek2ALgN8h5EFmbS33FqYbn
f2eTB+Hz+k8jhH1eDMuyKhRWFBr7kgYvX9mMEQl8O/Tzams45Xjf6cg83/flZgxS
Kf+l8wee4C2T6kBzfhKHD+IubGcMttNUwmfNH3bXYavaHWWqjHpjJ2MzTf+Q+mk9
8nVMjwYJ90em/28VH40Tb0Kco/z9oTU9OKDNKyaoqHvY+L38qWvt7FBM1qlm45E9
hF5eqw==
-----END CERTIFICATE-----
`;
const testClientAuthLeafPem = `-----BEGIN CERTIFICATE-----
MIIDYDCCAkigAwIBAgIIKiorLC0uLzAwDQYJKoZIhvcNAQELBQAwKDEmMCQGA1UE
AxMdQWdlbnR3ZWF2ZXIgVGVzdCBJbnRlcm1lZGlhdGUwHhcNMjAwMTAxMDAwMDAw
WhcNNDUwMTAxMDAwMDAwWjA+MTwwOgYDVQQDEzNwcm9qZWN0cy1jb25maWcuYWdl
bnR3ZWF2ZXItdjEtcDAuc3ZjLmNsdXN0ZXIubG9jYWwwggEiMA0GCSqGSIb3DQEB
AQUAA4IBDwAwggEKAoIBAQDHtHFGTTafq3Pgn3UwfDtasl7JCsSj6sBk5IpcV0n2
k/WeRxue2Tdcea6uOkh/7mwEW+lTYjTPaUMXL8w24KQBK5cn/lf6/MPzxLGt+eP9
9QLvMIEcVI6xZAfpUCB1pozJHhtkjYPWP/ov9vY6VkI814qyipEXHIHNMKK7sCB6
nt+9tt0vPSe1+tcmTQ8Mx2Hs19RH+NhTN+RN5J5N+t3IjtnyoDVOoInHmnkKZU8m
i8CC23HqayymLwgvkug59pygfPylIljw583AF5tqNjtMB2Wi3IfPyV+KSgRFWtaV
kb+o+JJIQ8FHynYTjm6Tcy1mSjqiuYyUxFLAwvYtbBoxAgMBAAGjeDB2MAwGA1Ud
EwEB/wQCMAAwDgYDVR0PAQH/BAQDAgWgMBYGA1UdJQEB/wQMMAoGCCsGAQUFBwMC
MD4GA1UdEQQ3MDWCM3Byb2plY3RzLWNvbmZpZy5hZ2VudHdlYXZlci12MS1wMC5z
dmMuY2x1c3Rlci5sb2NhbDANBgkqhkiG9w0BAQsFAAOCAQEAMZqTT2bM1FbJ6u1e
NVCeSEoC7UQLVjpzC5BjFqhMRB0s0Hikq+0u5bEhmHg2zy8ShDqVF2xr45NXUqtt
2J9cTMoG812sgwbNDeaokg0Htz6M3t8Fz3Ka25pCS9QPMcI/tNnpcVOpGdXD18Mr
XCcfxArMfkTFTqgTqZitL2X4KwTLE3AdYpGF+OjJ6ZOLe9JOsEnFYrPw1wNcCJH1
FFRf3yIo60I0g2+lPikYPvrgKf01UBs+r8CzBcG5+F8AiWY2gsrWFuSPbknpNQss
LEv9q25CsanSx6+2VO+M6l1JmpFstnybIl/j6y6yOWU6+otyFMwIK0Qrx1I2FnrQ
/DoGjw==
-----END CERTIFICATE-----
`;
const testCaBundle = testRootPem;
const testIntermediateTrustBundle = `${testIntermediatePem}${testRootPem}`;
const testLeafBase64 = Buffer.from(`${testServerLeafPem}${testIntermediatePem}`).toString('base64');
const testClientAuthLeafBase64 = Buffer.from(`${testClientAuthLeafPem}${testIntermediatePem}`).toString('base64');
const testLeafSha256 = sha256(new X509Certificate(testServerLeafPem).raw);

function runBinding({ caBundle = testCaBundle, leafPem = testServerLeafPem } = {}) {
  return {
    projectsOwnerAddress: 'https://projects-config.agentweaver-v1-p0.svc.cluster.local:8443/',
    projectsAudience: 'https://projects.agentweaver.io/run-binding',
    projectsServerCertificateSha256: sha256(new X509Certificate(leafPem).raw),
    projectsTrustBundleSha256: sha256(caBundle),
  };
}

function runBindingConfigMaps(binding) {
  return {
    'identity-broker-runtime-config-v2': {
      ConnectionStrings__IdentityBroker: 'Host=database;Password=test-only-value',
      IdentityBroker__SecretRedemption__Audience: 'https://identity.agentweaver.io/api',
      IdentityBroker__Clients__0__Resources__0: 'https://identity.agentweaver.io/api',
      IdentityBroker__Clients__0__Resources__1: 'https://sessions.agentweaver.io/api',
      'IdentityBroker__RunBinding__ProjectsOwnerAddress': binding.projectsOwnerAddress,
      'IdentityBroker__RunBinding__ProjectsAudience': binding.projectsAudience,
      'IdentityBroker__RunBinding__ProjectsServerCertificateSha256': binding.projectsServerCertificateSha256,
      'IdentityBroker__RunBinding__ProjectsTrustBundleSha256': binding.projectsTrustBundleSha256,
    },
    'projects-config-runtime-config': {
      'ProjectsConfig__Tls__CertificatePath': '/var/run/projects-config-tls/tls.crt',
      'ProjectsConfig__Tls__KeyPath': '/var/run/projects-config-tls/tls.key',
      'ProjectsConfig__Tls__ServerCertificateSha256': binding.projectsServerCertificateSha256,
      'ProjectsConfig__Authentication__Audience': 'agentweaver-projects-api',
      'ProjectsConfig__Authentication__BrokerRunBindingAudience': binding.projectsAudience,
    },
  };
}

function brokerRunBindingMocks(binding, {
  caBundle = testCaBundle,
  clientSecretKeys = '',
  configMaps = runBindingConfigMaps(binding),
  tlsCertificate = testLeafBase64,
  chainOutput = 'valid',
  usePowerShell = false,
} = {}) {
  const commands = [];
  let rawCalls = 0;
  const run = (file, args, options = {}) => {
    commands.push(args);
    if (file === 'pwsh') {
      assert.deepEqual(args.slice(0, 2), ['-NoProfile', '-NonInteractive']);
      const payload = JSON.parse(options.input);
      assert.deepEqual(Object.keys(payload).sort(), ['authorities', 'intermediates', 'leaf']);
      return usePowerShell ? runProcess(file, args, options).stdout.trim() : chainOutput;
    }
    assert.equal(file, 'kubectl');
    const resource = args[1];
    if (resource === 'configmap') {
      if (args[2] === 'service-backend-ca') {
        assert.fail('raw CA bytes must use the untrimmed command path');
      }
      const name = args[2];
      return JSON.stringify({
        metadata: { name, namespace: target.namespace },
        data: configMaps[name],
      });
    }
    if (resource !== 'secret') assert.fail(`unexpected preflight command: ${args.join(' ')}`);
    if (args[2] === 'identity-broker-client-secrets') return clientSecretKeys;
    if (args[2] !== 'projects-config-tls') assert.fail(`unexpected secret read: ${args[2]}`);
    if (args.at(-1).startsWith('--output=go-template=')) return 'kubernetes.io/tls|certificate|key';
    assert.equal(args.at(-1), '--output=jsonpath={.data.tls\\.crt}');
    return tlsCertificate;
  };
  const runRaw = (file, args) => {
    rawCalls++;
    assert.equal(file, 'kubectl');
    assert.deepEqual(args.slice(0, 3), ['get', 'configmap', 'service-backend-ca']);
    assert.equal(args.at(-1), '--output=jsonpath={.data.ca\\.crt}');
    return caBundle;
  };
  return {
    commands,
    get rawCalls() { return rawCalls; },
    root,
    run,
    runRaw,
  };
}

const powershellCertificateChainScript = `
$ErrorActionPreference = 'Stop'
$mode = ([Console]::In.ReadToEnd() | ConvertFrom-Json).mode
$notBefore = [DateTimeOffset]::UtcNow.AddMinutes(-5)
$notAfter = $notBefore.AddYears(2)
function New-Request([string]$subject) {
  $key = [System.Security.Cryptography.RSA]::Create(2048)
  $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
    $subject, $key, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
  return @{ Key = $key; Request = $request }
}
function New-Serial {
  $serial = [byte[]]::new(16)
  [System.Security.Cryptography.RandomNumberGenerator]::Fill($serial)
  $serial[0] = 1
  return $serial
}
$rootRequest = New-Request 'CN=Agentweaver run-binding test root'
$rootRequest.Request.CertificateExtensions.Add(
  [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $true, 3, $true))
$rootRequest.Request.CertificateExtensions.Add(
  [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign -bor
    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::CrlSign, $true))
$root = $rootRequest.Request.CreateSelfSigned($notBefore, $notAfter)
$intermediateRequest = New-Request 'CN=Agentweaver run-binding test intermediate'
$intermediatePathLength = if ($mode -eq 'path-length') { 0 } else { 1 }
$intermediateRequest.Request.CertificateExtensions.Add(
  [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new(
    $true, $true, $intermediatePathLength, $true))
$intermediateRequest.Request.CertificateExtensions.Add(
  [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign -bor
    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::CrlSign, $true))
if ($mode -eq 'intermediate-client-auth') {
  $eku = [System.Security.Cryptography.OidCollection]::new()
  $null = $eku.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.2'))
  $intermediateRequest.Request.CertificateExtensions.Add(
    [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku, $true))
}
$intermediate = $intermediateRequest.Request.Create(
  $root, $notBefore, $notAfter, (New-Serial))
$chain = @()
$leafIssuerName = $intermediate.SubjectName
$leafSigner = $intermediateRequest.Key
if ($mode -eq 'path-length') {
  $childRequest = New-Request 'CN=Agentweaver run-binding test subordinate'
  $childRequest.Request.CertificateExtensions.Add(
    [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $true, 0, $true))
  $childRequest.Request.CertificateExtensions.Add(
    [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
      [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign -bor
      [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::CrlSign, $true))
  $intermediateSigner = [System.Security.Cryptography.X509Certificates.X509SignatureGenerator]::CreateForRSA(
    $intermediateRequest.Key, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
  $child = $childRequest.Request.Create(
    $intermediate.SubjectName, $intermediateSigner, $notBefore, $notAfter, (New-Serial))
  $leafIssuerName = $child.SubjectName
  $leafSigner = $childRequest.Key
  $chain += $child
}
$leafRequest = New-Request 'CN=projects-config.agentweaver-v1-p0.svc.cluster.local'
$leafRequest.Request.CertificateExtensions.Add(
  [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
$leafRequest.Request.CertificateExtensions.Add(
  [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor
    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment, $true))
$leafEku = [System.Security.Cryptography.OidCollection]::new()
$null = $leafEku.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
$leafRequest.Request.CertificateExtensions.Add(
  [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($leafEku, $true))
$san = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
$san.AddDnsName('projects-config.agentweaver-v1-p0.svc.cluster.local')
$leafRequest.Request.CertificateExtensions.Add($san.Build())
$leafSignatureGenerator = [System.Security.Cryptography.X509Certificates.X509SignatureGenerator]::CreateForRSA(
  $leafSigner, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
$leaf = $leafRequest.Request.Create(
  $leafIssuerName, $leafSignatureGenerator, $notBefore, $notAfter, (New-Serial))
$chain += $intermediate
[PSCustomObject]@{
  root = [Convert]::ToBase64String($root.Export(
    [System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
  intermediates = @($chain | ForEach-Object {
    [Convert]::ToBase64String($_.Export(
      [System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
  })
  leaf = [Convert]::ToBase64String($leaf.Export(
    [System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
} | ConvertTo-Json -Compress
`;

function pemFromBase64(encoded) {
  return `-----BEGIN CERTIFICATE-----\n${encoded.match(/.{1,64}/g).join('\n')}\n-----END CERTIFICATE-----\n`;
}

function verifyGeneratedPowerShellChain(mode) {
  const generated = spawnSync('pwsh', [
    '-NoProfile', '-NonInteractive', '-Command', powershellCertificateChainScript,
  ], {
    encoding: 'utf8',
    input: JSON.stringify({ mode }),
    windowsHide: true,
  });
  assert.equal(generated.status, 0, generated.stderr || generated.stdout);
  const certificates = JSON.parse(generated.stdout);
  const caBundle = pemFromBase64(certificates.root);
  const leafPem = [
    pemFromBase64(certificates.leaf),
    ...certificates.intermediates.map(pemFromBase64),
  ].join('');
  const binding = runBinding({ caBundle, leafPem });
  const mocks = brokerRunBindingMocks(binding, {
    caBundle,
    tlsCertificate: Buffer.from(leafPem).toString('base64'),
    usePowerShell: true,
  });
  return verifyBrokerRunBindingConfiguration(binding, target, mocks);
}

function createFixture(t) {
  const fixtureRoot = path.join(root, 'artifacts', 'release-tests');
  mkdirSync(fixtureRoot, { recursive: true });
  const fixture = mkdtempSync(path.join(fixtureRoot, 'v1-integration-deploy-'));
  t.after(() => rmSync(fixture, { recursive: true, force: true }));

  const manifest = {
    schemaVersion: 1,
    stage: 'draft',
    components: productServices,
    compatibility: [],
  };
  mkdirSync(path.join(fixture, 'releases'), { recursive: true });
  writeFileSync(path.join(fixture, 'releases', 'foundation.json'), `${JSON.stringify(manifest, null, 2)}\n`);
  for (const component of productServices) {
    if (component.id === 'Agentweaver.Web') continue;
    const project = path.join(fixture, component.project);
    mkdirSync(path.dirname(project), { recursive: true });
    writeFileSync(project, '<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>\n');
  }
  mkdirSync(path.join(fixture, 'apps', 'web'), { recursive: true });
  writeFileSync(path.join(fixture, WEB_PROJECT_PATH), JSON.stringify({ name: 'web', version: '1.0.0' }));
  writeFileSync(path.join(fixture, WEB_LOCK_PATH), JSON.stringify({
    name: 'web',
    version: '1.0.0',
    lockfileVersion: 3,
    packages: { '': { name: 'web', version: '1.0.0' } },
  }));

  const components = productServices.map(component => {
    const imageConfigDigest = digest('e');
    const measurement = component.id === 'Agentweaver.AgentHost' ? {
      kind: 'agenthost-image',
      schemaVersion: 1,
      sourceSha,
      sourceTree,
      image: {
        configDigest: imageConfigDigest,
        version: component.version,
        platform: 'linux/amd64',
        compressedPullBytes: 123456,
      },
    } : undefined;
    return {
      ...component,
      record: {
        id: component.id,
        kind: 'service',
        version: component.version,
        project: component.project,
        ...(measurement ? { build: { measurement } } : {}),
      },
      artifact: {
        componentId: component.id,
        kind: 'image',
        path: `${component.id}.${component.version}.${component.id === 'Agentweaver.AgentHost' ||
          component.id === 'Agentweaver.Web' ? 'tar' : 'tar.gz'}`,
        tag: sourceSha,
        imageReference: `${componentImageRepository(component.id)}:${sourceSha}`,
        imageConfigDigest,
        integrationLabels: {
          'org.opencontainers.image.revision': sourceSha,
          'io.agentweaver.source-tree': sourceTree,
          'org.opencontainers.image.version': component.version,
        },
        labels: {
          'org.opencontainers.image.revision': sourceSha,
          'io.agentweaver.source-tree': sourceTree,
          'org.opencontainers.image.version': component.version,
        },
        sha256: 'd'.repeat(64),
      },
    };
  });
  const provenance = {
    schemaVersion: 1,
    stage: 'draft',
    sourceSha,
    manifestSha256: sha256(JSON.stringify(manifest)),
    integration: { mode: 'images-only', sourceTree, imageTag: sourceSha },
    components: components.map(({ record }) => record),
    artifacts: components.map(({ artifact }) => artifact),
  };
  const provenanceBytes = Buffer.from(`${JSON.stringify(provenance, null, 2)}\n`);
  const publication = {
    schemaVersion: 1,
    mode: 'images-only',
    status: 'published',
    sourceSha,
    sourceTree,
    claimSha: 'c'.repeat(40),
    provenanceSha256: sha256(provenanceBytes),
    planned: components.map(({ id, version, artifact }) => ({
      id,
      version,
      kind: 'image',
      sha256: artifact.sha256,
      destination: `ghcr.io/sabbour/${componentImageRepository(id)}:${sourceSha}`,
    })),
    published: components.map(({ id, version, artifact }) => {
      const imageDigest = digest((productServices.findIndex(item => item.id === id) + 1).toString(16));
      const result = {
        id,
        version,
        kind: 'image',
        archiveSha256: artifact.sha256,
        sourceSha,
        sourceTree,
        tag: sourceSha,
        configDigest: artifact.imageConfigDigest,
        image: `ghcr.io/sabbour/${componentImageRepository(id)}@${imageDigest}`,
      };
      if (id === 'Agentweaver.AgentHost') {
        result.preparedOciMeasurement = components.find(item => item.id === id).record.build.measurement;
      }
      return result;
    }),
  };
  const argv = [
    '--source-sha', sourceSha,
    '--manifest', 'releases/foundation.json',
    '--provenance', 'provenance.json',
    '--publication', 'publication.json',
    '--subscription-id', target.subscriptionId,
    '--tenant-id', target.tenantId,
    '--resource-group', target.resourceGroup,
    '--cluster', target.clusterName,
    '--namespace', target.namespace,
    '--node-min', String(target.nodeMin),
    '--node-max', String(target.nodeMax),
    '--provider-catalog-configmap', 'projects-config-provider-catalog',
    '--provider-catalog-source-commit', 'f'.repeat(40),
    '--result', 'deployment-result.json',
  ];

  return {
    root: fixture,
    manifest,
    provenance,
    provenanceBytes,
    publication,
    argv,
    parsed: parseIntegrationArgs(argv),
  };
}

function deploymentDependencies(fixture, run = () => '') {
  const imageByRef = new Map(fixture.publication.published.map(result => {
    const component = fixture.manifest.components.find(item => item.id === result.id);
    return [result.image, { result, component }];
  }));
  return {
    root: fixture.root,
    git(...args) {
      if (args[0] === 'rev-parse' && args[1] === 'HEAD') return sourceSha;
      if (args[0] === 'rev-parse' && args[1] === `${sourceSha}^{tree}`) return sourceTree;
      if (args[0] === 'status' && args[1] === '--porcelain') return '';
      if (args[0] === 'show' && args[1] === 'HEAD:releases/foundation.json') {
        return JSON.stringify(fixture.manifest);
      }
      assert.fail(`unexpected Git call: ${args.join(' ')}`);
    },
    verifyPublicationRemote: async () => true,
    verifyServiceImage: async image => {
      const { result, component } = imageByRef.get(image) ?? assert.fail(`unexpected image ${image}`);
      const imageDigest = image.slice(image.lastIndexOf('@') + 1);
      return {
        image,
        digest: imageDigest,
        acceptedDigests: new Set([imageDigest]),
        configDigest: result.configDigest,
        sourceSha,
        sourceTree,
        version: component.version,
      };
    },
    verifyAgentHostImage: async image => {
      const imageDigest = image.image.slice(image.image.lastIndexOf('@') + 1);
      return {
        image: image.image,
        digest: imageDigest,
        acceptedDigests: new Set([imageDigest]),
        compressedPullBytes: 123456,
      };
    },
    verifyTarget: async () => ({ exact: true }),
    verifyPrerequisites: () => {},
    verifyRouting: async () => {},
    verifyLive: async () => {},
    run,
  };
}

function deployOptions(fixture, updates = {}) {
  return {
    ...fixture.parsed,
    manifest: fixture.manifest,
    provenance: fixture.provenance,
    provenanceBytes: fixture.provenanceBytes,
    publication: fixture.publication,
    ...updates,
  };
}

test('Broker run-binding preflight checks the full server chain and raw CA bytes without reading the private key', () => {
  const binding = runBinding();
  const mocks = brokerRunBindingMocks(binding);
  const evidence = verifyBrokerRunBindingConfiguration(binding, target, mocks);
  assert.equal(mocks.rawCalls, 1);
  assert.equal(evidence.trustBundleSha256, binding.projectsTrustBundleSha256);
  assert.equal(evidence.projectsServerCertificateSha256, testLeafSha256);
  assert.doesNotMatch(JSON.stringify(evidence), /test-only-value/);
  assert.equal(mocks.commands.some(args => args.at(-1) === '--output=jsonpath={.data.tls\\.key}'), false);
  assert.equal(mocks.commands.some(args => args.includes('--output') && args.includes('json') &&
    args[1] === 'secret' && args[2] === 'projects-config-tls'), false);

  const shadowMocks = brokerRunBindingMocks(binding, {
    clientSecretKeys: 'IdentityBroker__RunBinding__ProjectsAudience',
  });
  assert.throws(() => verifyBrokerRunBindingConfiguration(binding, target, shadowMocks),
    /must not shadow the explicit run-binding settings/);
  assert.equal(shadowMocks.rawCalls, 0);
});

test('Broker run-binding validates the chain with the platform X.509 path builder', { skip: !hasPowerShell7 }, () => {
  const binding = runBinding();
  const mocks = brokerRunBindingMocks(binding, { usePowerShell: true });
  const evidence = verifyBrokerRunBindingConfiguration(binding, target, mocks);
  assert.equal(evidence.projectsServerCertificateSha256, binding.projectsServerCertificateSha256);
});

test('Broker run-binding accepts a generated server certificate chain through PowerShell 7', { skip: !hasPowerShell7 }, () => {
  const evidence = verifyGeneratedPowerShellChain('valid');
  assert.match(evidence.projectsServerCertificateSha256, /^[a-f0-9]{64}$/);
});

test('Broker run-binding rejects an intermediate path-length constraint violation through PowerShell 7', { skip: !hasPowerShell7 }, () => {
  assert.throws(() => verifyGeneratedPowerShellChain('path-length'),
    /X\.509 server certificate chain validation failed/);
});

test('Broker run-binding rejects an intermediate with client-only EKU through PowerShell 7', { skip: !hasPowerShell7 }, () => {
  assert.throws(() => verifyGeneratedPowerShellChain('intermediate-client-auth'),
    /X\.509 server certificate chain validation failed/);
});

test('Broker run-binding accepts pinned intermediate authorities and rejects client-only certificates', () => {
  const intermediateBinding = runBinding({ caBundle: testIntermediateTrustBundle });
  const intermediateMocks = brokerRunBindingMocks(intermediateBinding, {
    caBundle: testIntermediateTrustBundle,
  });
  assert.equal(verifyBrokerRunBindingConfiguration(intermediateBinding, target, intermediateMocks)
    .trustBundleSha256, intermediateBinding.projectsTrustBundleSha256);

  const clientBinding = runBinding({ leafPem: testClientAuthLeafPem });
  const clientMocks = brokerRunBindingMocks(clientBinding, {
    tlsCertificate: testClientAuthLeafBase64,
  });
  assert.throws(() => verifyBrokerRunBindingConfiguration(clientBinding, target, clientMocks),
    /serverAuth/);
});

test('Broker run-binding audience must differ from the existing Projects Config audience', () => {
  const binding = runBinding();
  const configMaps = runBindingConfigMaps(binding);
  configMaps['projects-config-runtime-config'].ProjectsConfig__Authentication__Audience =
    binding.projectsAudience;
  const mocks = brokerRunBindingMocks(binding, { configMaps });
  assert.throws(() => verifyBrokerRunBindingConfiguration(binding, target, mocks),
    /must be distinct from its existing authentication audience/);
  assert.equal(mocks.rawCalls, 0);
});

test('Broker run-binding audience must differ from every Identity Broker client resource', () => {
  const binding = runBinding();
  const configMaps = runBindingConfigMaps(binding);
  configMaps['identity-broker-runtime-config-v2'].IdentityBroker__Clients__0__Resources__1 =
    binding.projectsAudience;
  const mocks = brokerRunBindingMocks(binding, { configMaps });
  assert.throws(() => verifyBrokerRunBindingConfiguration(binding, target, mocks),
    /must be distinct from all existing Identity Broker public audiences/);
  assert.equal(mocks.rawCalls, 0);
});

test('Broker run-binding audience must differ from URI-equivalent Identity Broker client resources', () => {
  const binding = runBinding();
  const configMaps = runBindingConfigMaps(binding);
  configMaps['identity-broker-runtime-config-v2'].IdentityBroker__Clients__0__Resources__1 =
    'https://PROJECTS.agentweaver.io:443/run-binding';
  const mocks = brokerRunBindingMocks(binding, { configMaps });
  assert.throws(() => verifyBrokerRunBindingConfiguration(binding, target, mocks),
    /must be distinct from all existing Identity Broker public audiences/);
  assert.equal(mocks.rawCalls, 0);
});

test('Broker run-binding audience must differ from the Identity Broker SecretRedemption audience', () => {
  const binding = runBinding();
  const configMaps = runBindingConfigMaps(binding);
  configMaps['identity-broker-runtime-config-v2'].IdentityBroker__SecretRedemption__Audience =
    binding.projectsAudience;
  const mocks = brokerRunBindingMocks(binding, { configMaps });
  assert.throws(() => verifyBrokerRunBindingConfiguration(binding, target, mocks),
    /must be distinct from all existing Identity Broker public audiences/);
  assert.equal(mocks.rawCalls, 0);
});

test('Broker run-binding audience uses its canonical URI for the exact SecretRedemption comparison', () => {
  const binding = runBinding();
  binding.projectsAudience = 'https://projects.agentweaver.io';
  const configMaps = runBindingConfigMaps(binding);
  configMaps['identity-broker-runtime-config-v2'].IdentityBroker__SecretRedemption__Audience =
    'https://projects.agentweaver.io/';
  const mocks = brokerRunBindingMocks(binding, { configMaps });
  assert.throws(() => verifyBrokerRunBindingConfiguration(binding, target, mocks),
    /must be distinct from all existing Identity Broker public audiences/);
  assert.equal(mocks.rawCalls, 0);
});

test('Broker run-binding audience rejects a URI-equivalent root Projects Config audience', () => {
  const binding = runBinding();
  binding.projectsAudience = 'https://projects.agentweaver.io';
  const configMaps = runBindingConfigMaps(binding);
  configMaps['projects-config-runtime-config'].ProjectsConfig__Authentication__Audience =
    'https://projects.agentweaver.io/';
  const mocks = brokerRunBindingMocks(binding, { configMaps });
  assert.throws(() => verifyBrokerRunBindingConfiguration(binding, target, mocks),
    /must be distinct from its existing authentication audience/);
  assert.equal(mocks.rawCalls, 0);
});

test('Broker run-binding accepts distinct but URI-similar public audiences', () => {
  const binding = runBinding();
  binding.projectsAudience = 'https://projects.agentweaver.io';
  const configMaps = runBindingConfigMaps(binding);
  configMaps['projects-config-runtime-config'].ProjectsConfig__Authentication__Audience =
    'https://projects.agentweaver.io/run-binding';
  configMaps['identity-broker-runtime-config-v2'].IdentityBroker__Clients__0__Resources__0 =
    'https://PROJECTS.agentweaver.io:443/other';
  configMaps['identity-broker-runtime-config-v2'].IdentityBroker__SecretRedemption__Audience =
    'https://PROJECTS.agentweaver.io:443/other';
  const mocks = brokerRunBindingMocks(binding, { configMaps });
  const evidence = verifyBrokerRunBindingConfiguration(binding, target, mocks);
  assert.equal(evidence.projectsServerCertificateSha256, binding.projectsServerCertificateSha256);
});

test('integration deployment arguments default to read-only and require an exact source SHA', t => {
  const fixture = createFixture(t);
  assert.equal(fixture.parsed.dryRun, true);
  assert.equal(fixture.parsed.execute, false);
  assert.throws(() => parseIntegrationArgs(fixture.argv.slice(2)), /sourceSha is required/);
  const uppercaseSha = [...fixture.argv];
  uppercaseSha[1] = 'A'.repeat(40);
  assert.throws(() => parseIntegrationArgs(uppercaseSha), /lowercase 40-character Git commit SHA/);
  const descriptorArgs = [...fixture.argv, '--broker-run-binding', 'binding.json'];
  assert.equal(parseIntegrationArgs(descriptorArgs).brokerRunBindingPath, 'binding.json');
  const missingCatalog = descriptorArgs.filter((value, index, values) =>
    !['--provider-catalog-configmap', '--provider-catalog-source-commit'].includes(value) &&
    !['--provider-catalog-configmap', '--provider-catalog-source-commit'].includes(values[index - 1]));
  assert.throws(() => parseIntegrationArgs(missingCatalog), /--provider-catalog-configmap is required/);
});

test('source-only integration deployment remains local without a separate GitHub publication workflow', () => {
  const workflowPath = path.join(root, '.github', 'workflows', 'v1-integration-images.yml');
  assert.throws(() => readFileSync(workflowPath, 'utf8'), { code: 'ENOENT' });
  const releaseWorkflow = readFileSync(path.join(root, '.github', 'workflows', 'v1-release-pack.yml'), 'utf8');
  assert.doesNotMatch(releaseWorkflow, /release:integration-pack|integration-source-sha/);
  assert.match(releaseWorkflow, /name: Agentweaver v1 release publication/);
});

test('immutable publication claim must match the exact source, provenance, and image plan', async t => {
  const fixture = createFixture(t);
  const namespace = `agentweaver-integration/${sourceSha}`;
  const resultTagSha = 'f'.repeat(40);
  const claim = {
    schemaVersion: 1,
    sourceSha,
    mode: 'images-only',
    sourceTree,
    provenanceSha256: fixture.publication.provenanceSha256,
    planned: fixture.publication.planned,
    status: 'claimed',
    published: [],
  };
  const claimTag = {
    tag: `${namespace}/claim`,
    message: JSON.stringify(claim),
    object: { type: 'commit', sha: sourceSha },
  };
  const resultTag = {
    tag: `${namespace}/result`,
    message: JSON.stringify(fixture.publication),
    object: { type: 'commit', sha: sourceSha },
  };
  const run = (file, args) => {
    assert.equal(file, 'gh');
    const endpoint = args[1];
    if (endpoint.includes('/git/ref/tags/')) {
      const isClaim = endpoint.endsWith('%2Fclaim');
      return JSON.stringify({ object: {
        type: 'tag',
        sha: isClaim ? fixture.publication.claimSha : resultTagSha,
      } });
    }
    if (endpoint === `repos/sabbour/agentweaver/git/tags/${fixture.publication.claimSha}`) {
      return JSON.stringify(claimTag);
    }
    if (endpoint === `repos/sabbour/agentweaver/git/tags/${resultTagSha}`) {
      return JSON.stringify(resultTag);
    }
    assert.fail(`unexpected GitHub call: ${endpoint}`);
  };
  assert.equal(await verifyIntegrationPublicationRemote(fixture.publication, sourceSha, {
    root: fixture.root,
    run,
  }), true);

  const tamperedClaim = structuredClone(claim);
  tamperedClaim.planned[0].destination = 'ghcr.io/sabbour/other:wrong';
  claimTag.message = JSON.stringify(tamperedClaim);
  await assert.rejects(verifyIntegrationPublicationRemote(fixture.publication, sourceSha, {
    root: fixture.root,
    run,
  }), /immutable integration claim is not bound to the exact source and publication plan/);
});

test('integration deployment rejects draft, provenance, and receipt mismatches before remote checks', async t => {
  const fixture = createFixture(t);
  const dependencies = deploymentDependencies(fixture, () =>
    assert.fail('deployment command must not run for invalid source evidence'));
  dependencies.verifyPublicationRemote = async () => assert.fail('remote receipt check must not run');
  dependencies.verifyServiceImage = async () => assert.fail('registry access must not run');

  const manifestWithEvidence = { ...fixture.manifest, evidence: {} };
  fixture.manifest.evidence = {};
  await assert.rejects(deployIntegration(deployOptions(fixture, { manifest: manifestWithEvidence }), dependencies),
    /draft must not claim deployment or persona evidence/);
  delete fixture.manifest.evidence;

  const provenanceWithWrongTree = structuredClone(fixture.provenance);
  provenanceWithWrongTree.integration.sourceTree = 'c'.repeat(40);
  await assert.rejects(deployIntegration(deployOptions(fixture, { provenance: provenanceWithWrongTree }), dependencies),
    /preparation provenance, draft manifest, and images-only receipt do not match/);

  const receiptWithWrongTree = { ...fixture.publication, sourceTree: 'c'.repeat(40) };
  await assert.rejects(deployIntegration(deployOptions(fixture, { publication: receiptWithWrongTree }), dependencies),
    /images-only publication receipt is partial or not bound to the exact source and tree/);
});

test('default integration dry-run verifies images and renders without applying resources', async t => {
  const fixture = createFixture(t);
  const commands = [];
  const dependencies = deploymentDependencies(fixture, (file, args) => {
    commands.push([file, args]);
    assert.deepEqual([file, ...args], ['kubectl', 'apply', '--dry-run=client', '--filename', '-']);
    return '';
  });
  const result = await deployIntegration(deployOptions(fixture), dependencies);
  assert.equal(result.dryRun, true);
  assert.equal(result.services.length, 9);
  assert.equal(result.sourceSha, sourceSha);
  assert.equal(result.sourceTree, sourceTree);
  assert.equal(result.renderedResources, 18);
  assert.deepEqual(result.targetInfo, { exact: true });
  assert.equal(commands.length, 1);
});

test('supplied runtime List carries its own catalog identity when CLI catalog values are omitted', async t => {
  const fixture = createFixture(t);
  let runtimeManifest;
  const dependencies = deploymentDependencies(fixture, (file, args, runOptions = {}) => {
    assert.equal(file, 'kubectl');
    assert.deepEqual(args, ['apply', '--dry-run=client', '--filename', '-']);
    runtimeManifest = JSON.parse(runOptions.input);
    return '';
  });
  await deployIntegration(deployOptions(fixture), dependencies);

  // A caller-supplied List is validated against its paired catalog consumers and annotations.
  const result = await deployIntegration(deployOptions(fixture, {
    providerCatalogConfigMapName: undefined,
    providerCatalogSourceCommit: undefined,
    runtimeManifest,
  }), dependencies);

  assert.equal(result.renderedResources, 18);
  assert.equal(runtimeManifest.items
    .filter(item => item.kind === 'Deployment' && ['projects-config', 'orchestrator']
      .includes(item.metadata.name)).length, 2);
});

test('Broker run-binding requires the exact generated runtime List and stays opt-in', async t => {
  const fixture = createFixture(t);
  const binding = runBinding();
  const options = deployOptions(fixture, { brokerRunBindingPath: 'binding.json' });
  let runtimeManifest;
  let preflightCalls = 0;
  const dependencies = deploymentDependencies(fixture, (file, args, runOptions = {}) => {
    assert.equal(file, 'kubectl');
    assert.deepEqual(args, ['apply', '--dry-run=client', '--filename', '-']);
    runtimeManifest = JSON.parse(runOptions.input);
    return '';
  });
  dependencies.verifyBrokerRunBindingConfiguration = () => {
    preflightCalls++;
    return { trustBundleSha256: binding.projectsTrustBundleSha256 };
  };
  dependencies.readFile = (file) => {
    assert.equal(path.basename(file), 'binding.json');
    return JSON.stringify(binding);
  };
  const result = await deployIntegration(options, dependencies);
  assert.equal(result.renderedResources, 20);
  assert.equal(preflightCalls, 1);
  const policies = runtimeManifest.items.filter(item => item.kind === 'CiliumNetworkPolicy');
  assert.deepEqual(policies.map(item => item.metadata.name).sort(), [
    'identity-broker-projects-egress',
    'projects-config-broker-ingress',
  ]);
  const broker = runtimeManifest.items.find(item =>
    item.kind === 'Deployment' && item.metadata.name === 'identity-broker');
  assert.equal(broker.spec.template.metadata.annotations['agentweaver.io/broker-run-binding-sha256'],
    sha256(JSON.stringify(binding)));

  const alteredRuntime = structuredClone(runtimeManifest);
  alteredRuntime.items.find(item => item.kind === 'CiliumNetworkPolicy')
    .spec.egress[0].toEndpoints[0].matchLabels['agentweaver.io/workload'] = 'gateway';
  await assert.rejects(deployIntegration(deployOptions(fixture, {
    brokerRunBinding: binding,
    runtimeManifest: alteredRuntime,
  }), dependencies), /exact generated runtime manifest/);
  assert.equal(preflightCalls, 1);
});

test('execute mode rejects unsafe or existing result paths before mutating apply', async t => {
  const scenarios = [
    {
      name: 'result outside the source checkout',
      resultPath: path.join('..', 'outside-deployment-result.json'),
      error: /result path must remain inside the source checkout/,
    },
    {
      name: 'existing immutable result',
      resultPath: 'deployment-result.json',
      error: /deployment result already exists/,
      existingContent: 'earlier deployment result',
    },
  ];

  for (const scenario of scenarios) {
    await t.test(scenario.name, async t => {
      const fixture = createFixture(t);
      if (scenario.existingContent) {
        writeFileSync(path.join(fixture.root, scenario.resultPath), scenario.existingContent);
      }
      const options = deployOptions(fixture, {
        execute: true,
        dryRun: false,
        resultPath: scenario.resultPath,
      });
      const commands = [];
      const dependencies = deploymentDependencies(fixture, (file, args) => {
        commands.push([file, args]);
        return '';
      });

      await assert.rejects(deployIntegration(options, dependencies), scenario.error);
      assert.equal(commands.filter(([file, args]) =>
        file === 'kubectl' && args[0] === 'apply' && !args.includes('--dry-run=client')).length, 0);
      assert.equal(commands.filter(([file, args]) =>
        file === 'kubectl' && args[0] === 'apply' && args.includes('--dry-run=client')).length, 1);
      if (scenario.existingContent) {
        assert.equal(readFileSync(path.join(fixture.root, scenario.resultPath), 'utf8'), scenario.existingContent);
      }
    });
  }
});

test('execute mode records exact registry and Kubernetes readbacks without external commands', async t => {
  const fixture = createFixture(t);
  const resultPath = path.join(fixture.root, 'deployment-result.json');
  const options = deployOptions(fixture, {
    execute: true,
    dryRun: false,
    resultPath: 'deployment-result.json',
  });
  let runtimeManifest;
  const commands = [];
  const dependencies = deploymentDependencies(fixture, (file, args, options = {}) => {
    commands.push(args);
    assert.equal(file, 'kubectl');
    if (args[0] === 'apply') {
      assert.ok(options.input);
      runtimeManifest = JSON.parse(options.input);
      return '';
    }
    assert.equal(args[0], 'get');
    const name = args[1] === 'pods'
      ? args[args.indexOf('--selector') + 1].split('=').at(-1)
      : args[2];
    const resource = runtimeManifest.items.find(item =>
      item.kind === (args[1] === 'deployment' ? 'Deployment' : 'Service') && item.metadata.name === name);
    if (args[1] === 'deployment' || args[1] === 'service') return JSON.stringify(resource);
    assert.equal(args[1], 'pods');
    const image = runtimeManifest.items.find(item =>
      item.kind === 'Deployment' && item.metadata.name === name).spec.template.spec.containers[0].image;
    return JSON.stringify({
      items: [{
        metadata: { name: `${name}-ready`, namespace: target.namespace },
        spec: { containers: [{ image }] },
        status: {
          conditions: [{ type: 'Ready', status: 'True' }],
          containerStatuses: [{ ready: true, imageID: image }],
        },
      }],
    });
  });

  const result = await deployIntegration(options, dependencies);
  assert.equal(result.dryRun, false);
  assert.equal(result.services.length, 9);
  assert.equal(result.resultPath, resultPath);
  assert.match(result.readbackSha256, /^[a-f0-9]{64}$/);
  assert.equal(commands.filter(args => args[0] === 'apply' && args.includes('--dry-run=client')).length, 1);
  assert.equal(commands.filter(args => args[0] === 'apply' && !args.includes('--dry-run=client')).length, 1);
  assert.equal(commands.filter(args => args[0] === 'get').length, 27);

  const saved = JSON.parse(readFileSync(resultPath, 'utf8'));
  assert.equal(saved.status, 'deployed');
  assert.equal(saved.sourceSha, sourceSha);
  assert.equal(saved.sourceTree, sourceTree);
  assert.equal(saved.registryImages.length, 10);
  assert.equal(saved.readbacks.resources.length, 27);
  assert.equal(saved.provenanceSha256, fixture.publication.provenanceSha256);
  assert.notEqual(saved.provenanceSha256, sha256(JSON.stringify(fixture.provenance)));
});

test('execute run-binding verifies Pod settings, owners, policies, and the Projects Service endpoint', async t => {
  const fixture = createFixture(t);
  const binding = runBinding();
  const configurationEvidence = {
    trustBundleSha256: binding.projectsTrustBundleSha256,
    projectsServerCertificateSha256: binding.projectsServerCertificateSha256,
  };
  const options = deployOptions(fixture, {
    brokerRunBinding: binding,
    execute: true,
    dryRun: false,
    resultPath: 'binding-deployment-result.json',
  });
  let runtimeManifest;
  let configurationChecks = 0;
  let projectEndpointAddress = '10.0.0.42';
  let alterProjectsTlsMount = false;
  let alterReadyPodFingerprint = false;
  let alterReadyPodSettings = false;
  let alterReplicaSetOwner = false;
  let readyPodEnvOverrides = {};
  let duplicateReadyPodExpectedEnv = false;
  const dependencies = deploymentDependencies(fixture, (file, args, runOptions = {}) => {
    assert.equal(file, 'kubectl');
    if (args[0] === 'apply') {
      runtimeManifest = JSON.parse(runOptions.input);
      return '';
    }
    assert.equal(args[0], 'get');
    if (args[1] === 'ciliumnetworkpolicies.cilium.io') {
      return JSON.stringify({
        items: runtimeManifest.items.filter(item =>
          item.kind === 'CiliumNetworkPolicy' && item.metadata.labels['agentweaver.io/run-binding'] === 'projects'),
      });
    }
    if (args[1] === 'endpointslices.discovery.k8s.io') {
      return JSON.stringify({
        items: [{
          metadata: {
            name: 'projects-config-endpointslice',
            namespace: target.namespace,
            labels: { 'kubernetes.io/service-name': 'projects-config' },
            ownerReferences: [{
              kind: 'Service', name: 'projects-config', uid: 'projects-config-service-uid',
              controller: true,
            }],
          },
          ports: [{ name: 'https', port: 8443, protocol: 'TCP' }],
          endpoints: [{
            addresses: [projectEndpointAddress],
            conditions: { ready: true },
            targetRef: {
              kind: 'Pod', namespace: target.namespace, name: 'projects-config-ready',
              uid: 'projects-config-pod-uid',
            },
          }],
        }],
      });
    }
    if (args[1] === 'pods') {
      const name = args[args.indexOf('--selector') + 1].split('=').at(-1);
      const deploymentResource = runtimeManifest.items.find(item =>
        item.kind === 'Deployment' && item.metadata.name === name);
      const template = deploymentResource.spec.template;
      const podSpec = structuredClone(template.spec);
      const annotations = structuredClone(template.metadata.annotations ?? {});
      if (alterReadyPodFingerprint && name === 'identity-broker') {
        annotations['agentweaver.io/broker-run-binding-sha256'] = 'stale';
      }
      if (alterReadyPodSettings && name === 'identity-broker') {
        const caMount = podSpec.containers[0].volumeMounts
          .find(mount => mount.mountPath === '/var/run/service-backend-ca');
        caMount.subPath = 'ca.crt';
      }
      if (name === 'identity-broker' || name === 'projects-config') {
        podSpec.containers[0].env.push(
          { name: 'AZURE_CLIENT_ID', value: 'workload-client' },
          { name: 'AZURE_TENANT_ID', value: 'workload-tenant' },
          { name: 'AZURE_FEDERATED_TOKEN_FILE', value: '/var/run/secrets/azure/tokens/identity-token' },
          ...structuredClone(readyPodEnvOverrides[name] ?? []),
        );
      }
      if (duplicateReadyPodExpectedEnv && name === 'identity-broker') {
        const urls = podSpec.containers[0].env.find(entry => entry.name === 'ASPNETCORE_URLS');
        podSpec.containers[0].env.push({ ...urls, name: urls.name.toLowerCase() });
      }
      const image = podSpec.containers[0].image;
      return JSON.stringify({
        items: [{
          metadata: {
            name: `${name}-ready`,
            namespace: target.namespace,
            uid: `${name}-pod-uid`,
            annotations,
            ...(name === 'identity-broker' || name === 'projects-config' ? {
              ownerReferences: [{
                kind: 'ReplicaSet', name: `${name}-rs`, uid: `${name}-rs-uid`, controller: true,
              }],
            } : {}),
          },
          spec: podSpec,
          status: {
            conditions: [{ type: 'Ready', status: 'True' }],
            containerStatuses: [{ ready: true, imageID: image }],
            ...(name === 'projects-config' ? {
              podIP: '10.0.0.42',
              podIPs: [{ ip: '10.0.0.42' }],
            } : {}),
          },
        }],
      });
    }
    if (args[1] === 'replicaset') {
      const deploymentName = args[2].slice(0, -'-rs'.length);
      return JSON.stringify({
        metadata: {
          name: args[2],
          namespace: target.namespace,
          uid: `${deploymentName}-rs-uid`,
          ownerReferences: [{
            kind: 'Deployment',
            name: deploymentName,
            uid: alterReplicaSetOwner ? 'stale-deployment-uid' : `${deploymentName}-deployment-uid`,
            controller: true,
          }],
        },
      });
    }
    const name = args[2];
    const resource = runtimeManifest.items.find(item =>
      item.kind === (args[1] === 'deployment' ? 'Deployment' : 'Service') && item.metadata.name === name);
    if (args[1] === 'deployment') {
      const deployment = structuredClone(resource);
      deployment.metadata.uid = `${name}-deployment-uid`;
      if (alterProjectsTlsMount && name === 'projects-config') {
        deployment.spec.template.spec.containers[0].volumeMounts
          .find(mount => mount.mountPath === '/var/run/projects-config-tls').subPath = 'tls.crt';
      }
      return JSON.stringify(deployment);
    }
    assert.equal(args[1], 'service');
    return JSON.stringify({
      ...resource,
      metadata: { ...resource.metadata, uid: `${name}-service-uid` },
    });
  });
  dependencies.verifyBrokerRunBindingConfiguration = () => {
    configurationChecks++;
    return configurationEvidence;
  };

  const result = await deployIntegration(options, dependencies);
  assert.equal(result.dryRun, false);
  assert.equal(configurationChecks, 2);
  const saved = JSON.parse(readFileSync(path.join(fixture.root, 'binding-deployment-result.json'), 'utf8'));
  assert.equal(saved.readbacks.resources.length, 31);
  assert.equal(saved.readbacks.resources.filter(resource => resource.kind === 'replicaset').length, 2);
  assert.equal(saved.readbacks.brokerRunBinding.configuration.trustBundleSha256,
    binding.projectsTrustBundleSha256);
  assert.match(saved.readbacks.brokerRunBinding.policiesSha256, /^[a-f0-9]{64}$/);
  assert.equal(saved.readbacks.brokerRunBinding.projectsPod, 'projects-config-ready');
  assert.equal(saved.readbacks.brokerRunBinding.projectsServiceUid, 'projects-config-service-uid');
  const exactRun = dependencies.run;
  projectEndpointAddress = '10.0.0.99';
  await assert.rejects(deployIntegration(deployOptions(fixture, {
    ...options,
    resultPath: 'binding-wrong-endpoint-result.json',
  }), dependencies), /EndpointSlice addresses must match the ready Projects pod IPs/);
  projectEndpointAddress = '10.0.0.42';
  alterProjectsTlsMount = true;
  await assert.rejects(deployIntegration(deployOptions(fixture, {
    ...options,
    resultPath: 'binding-altered-mount-result.json',
  }), dependencies), /changed the exact Broker run-binding settings or TLS mount/);
  alterProjectsTlsMount = false;
  alterReadyPodFingerprint = true;
  await assert.rejects(deployIntegration(deployOptions(fixture, {
    ...options,
    resultPath: 'binding-stale-pod-fingerprint-result.json',
  }), dependencies), /ready Pod does not preserve the exact Broker run-binding fingerprint/);
  alterReadyPodFingerprint = false;
  alterReadyPodSettings = true;
  await assert.rejects(deployIntegration(deployOptions(fixture, {
    ...options,
    resultPath: 'binding-altered-pod-settings-result.json',
  }), dependencies), /ready Pod does not preserve the exact Broker run-binding fingerprint/);
  alterReadyPodSettings = false;
  readyPodEnvOverrides = {
    'identity-broker': [{
      name: 'IdentityBroker__RunBinding__ProjectsAudience',
      value: 'https://override.agentweaver.io',
    }],
  };
  await assert.rejects(deployIntegration(deployOptions(fixture, {
    ...options,
    resultPath: 'binding-direct-pod-audience-override-result.json',
  }), dependencies), /ready Pod does not preserve the exact Broker run-binding fingerprint/);
  readyPodEnvOverrides = {
    'projects-config': [{
      name: 'pRoJeCtScOnFiG__aUtHeNtIcAtIoN__bRoKeRrUnBiNdInGaUdIeNcE',
      value: 'https://override.agentweaver.io',
    }],
  };
  await assert.rejects(deployIntegration(deployOptions(fixture, {
    ...options,
    resultPath: 'binding-mixed-case-pod-audience-override-result.json',
  }), dependencies), /ready Pod does not preserve the exact Broker run-binding fingerprint/);
  readyPodEnvOverrides = {};
  duplicateReadyPodExpectedEnv = true;
  await assert.rejects(deployIntegration(deployOptions(fixture, {
    ...options,
    resultPath: 'binding-case-equivalent-pod-env-result.json',
  }), dependencies), /ready Pod does not preserve the exact Broker run-binding fingerprint/);
  duplicateReadyPodExpectedEnv = false;
  alterReplicaSetOwner = true;
  await assert.rejects(deployIntegration(deployOptions(fixture, {
    ...options,
    resultPath: 'binding-stale-deployment-owner-result.json',
  }), dependencies), /ReplicaSet is not owned by the exact current identity-broker Deployment/);
  alterReplicaSetOwner = false;

  dependencies.run = (file, args, runOptions) => {
    const output = exactRun(file, args, runOptions);
    if (args[1] !== 'ciliumnetworkpolicies.cilium.io') return output;
    const policyList = JSON.parse(output);
    policyList.items.push({
      ...structuredClone(policyList.items[0]),
      metadata: { ...policyList.items[0].metadata, name: 'unexpected-run-binding-policy' },
    });
    return JSON.stringify(policyList);
  };
  await assert.rejects(deployIntegration(deployOptions(fixture, {
    ...options,
    resultPath: 'binding-extra-policy-result.json',
  }), dependencies), /must contain exactly the two generated policies/);
});
