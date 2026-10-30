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
import { prepareBrokerRunBinding } from '../prepare-broker-run-binding.mjs';
import { componentImageRepository } from '../pack.mjs';
import { WEB_LOCK_PATH, WEB_PROJECT_PATH } from '../validate.mjs';
import { agentHostComponent } from './agenthost-fixture.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const sourceSha = 'a'.repeat(40);
const sourceTree = 'b'.repeat(40);
const target = {
  subscriptionId: '90ab3701-83f0-4ba1-a90a-f2e68683adab',
  tenantId: '72f988bf-86f1-41af-91ab-2d7cd011db47',
  resourceGroup: 'agentweaver',
  clusterName: 'agentweaver-aks',
  namespace: 'agentweaver',
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
MIIDNDCCAhygAwIBAgIUW43laS12EoDi4cERD82LYIyLQCcwDQYJKoZIhvcNAQEL
BQAwIDEeMBwGA1UEAwwVQWdlbnR3ZWF2ZXIgVGVzdCBSb290MB4XDTI2MTAxODA3
MDQxMloXDTQ1MTIxNzA3MDQxMlowIDEeMBwGA1UEAwwVQWdlbnR3ZWF2ZXIgVGVz
dCBSb290MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA1QP6YcIovFyF
kyClAh9YxWX/qyC2l3orDT4sk0S+G5495YBE1k5wFUJPRLR80mFcxH0udgRgXGrh
G4AqywkJoD6X06cEX84defTebU2GFT8krbOxfZSykM2klZ6NGASr7c8bCyii9gJB
XJfiG1aNJvgl8BhK0BTqwnCuNOsY9MNTj3WClA+VMRyu1N0LqTQ2fwjvf+NSZ8Ty
emz0WRaZ6R0DwLfbXjx3bUFbltr6AgUOHy4Blu5dh8Vk0zD3yoIKDIbLzR4Y+TB3
S1S9uJkZqW+0jkuC8z3jmt9GY7GOaP9JbCiR5wOh5AZqDXvwxX4ZBninjGWXcf/9
gbexKLEEBwIDAQABo2YwZDAdBgNVHQ4EFgQUYR9iB07elTRfqDBjzk3gQSaLuEYw
HwYDVR0jBBgwFoAUYR9iB07elTRfqDBjzk3gQSaLuEYwEgYDVR0TAQH/BAgwBgEB
/wIBAjAOBgNVHQ8BAf8EBAMCAQYwDQYJKoZIhvcNAQELBQADggEBAFJ5DWjPaIca
EeWoxNgie7FrLnxHwrjgm5ebXeqVHDpBkxIXpGlYyZrFuYHbgTQiCUnBEQmPw9WN
don0K9WPx/2dbrG2CdZ2AyhdlVMyEbjLlo/cPPbDREcuy7YIt7TWu8SMfPTnWNCR
KKOR/gMp3DHN1zSrkriwLYMAjgTaPJ22v75MTDbnljJIcRKTr+6R2t7cggFG5yfC
y2dPEeJwDm9BiZLtRoP5Z0VjHnDfrEBsP3urAguOH4UHd4haBzkbncXwRS4xaZhr
5pierLOLoSFCzhyR1KHOCIFi8hIKKwOF7+Um6gTw6Pf9WiwegCu8sJ9Tndtovihd
9FsMIEXbnWY=
-----END CERTIFICATE-----
`;
const testIntermediatePem = `-----BEGIN CERTIFICATE-----
MIIDPDCCAiSgAwIBAgIUXjaG/QqbPEp4EPjy3RJlXSJX1kYwDQYJKoZIhvcNAQEL
BQAwIDEeMBwGA1UEAwwVQWdlbnR3ZWF2ZXIgVGVzdCBSb290MB4XDTI2MTAxODA3
MDQxM1oXDTQ1MTIxNzA3MDQxM1owKDEmMCQGA1UEAwwdQWdlbnR3ZWF2ZXIgVGVz
dCBJbnRlcm1lZGlhdGUwggEiMA0GCSqGSIb3DQEBAQUAA4IBDwAwggEKAoIBAQCe
ybTGrz9abpSncndazdzOy5Hx54c7OCnOjC0d0eGPzlKaJSn+WvkmpK8+3ppRSl90
PXbmcXSGgtGSgFcQVqtYO07JfJtSJYTmqqKrtXjPYL/GAgKMaT7c50CBBVSkGKoC
yGF8DyKlFrDQ5cEn/gFx1W3wiReLMLsGc5+lcTDVn3yxFELaZO/jerZhfnVEW1si
YVKeIg4S4l7n1amjvPzhFouFJlbwLYcPKqe7wkIBUUYdFNVfA+UrmwZjBjvAmVtJ
lqnTqngPW9f4kJ5Z45ke5NxsEPKh+iGk44mH6dE521+Opg6VKO+XBAEQ1AYEN/VW
UrNUDUigMsOFG9W1R43XAgMBAAGjZjBkMBIGA1UdEwEB/wQIMAYBAf8CAQEwDgYD
VR0PAQH/BAQDAgEGMB0GA1UdDgQWBBT/FpJakXdZ4tpPTVGg5XiyvMb1iDAfBgNV
HSMEGDAWgBRhH2IHTt6VNF+oMGPOTeBBJou4RjANBgkqhkiG9w0BAQsFAAOCAQEA
snl627BAqgb85wKM5LqpIcvGttDOT/kl8X4JS6Z4zIPuGGDSEcxBz+zSD8Mua3Uk
iaQZFH5bUIqZTD+KHqWSCuKwcBbyvo1/gy6YiL94frnxVdw8MRPYexNFZHhCIVpk
jwg+KFaNNwye+KPljgAqh8c4UOd6lWDSNavaVWz4ATKO3HvC1b7ULIoumKEpFKw8
FBodrmDDes4ePOmWAlqdusZIydvlWwTJJzmpbdMwtQeZgQFipK1iia7cH3Nbzj2R
sEM82k9//xCuG8fXspvt7UkNhqVVs5//uf7ZBwgqfNyXXuwkNKd2qYAZkUhzAIYi
0rfkqyKtNRaYUkBk39yugg==
-----END CERTIFICATE-----
`;
const testServerLeafPem = `-----BEGIN CERTIFICATE-----
MIIDnzCCAoegAwIBAgIUZzQUQJW1dqDmipRF23bbnHkEweQwDQYJKoZIhvcNAQEL
BQAwKDEmMCQGA1UEAwwdQWdlbnR3ZWF2ZXIgVGVzdCBJbnRlcm1lZGlhdGUwHhcN
MjYxMDE4MDcwNDE0WhcNNDUxMjE3MDcwNDE0WjA4MTYwNAYDVQQDDC1wcm9qZWN0
cy1jb25maWcuYWdlbnR3ZWF2ZXIuc3ZjLmNsdXN0ZXIubG9jYWwwggEiMA0GCSqG
SIb3DQEBAQUAA4IBDwAwggEKAoIBAQCs/AlnK0DbzJAdiVQ0Mn/pgCHA+yd6/CeD
o+fZGzneLW7QwVvr2QADDLn3QvaFn9VpFWYArzg/vYhfdprm77mg1d8a5bYk7MOn
VLOWTcjSHXLpcYGt5b0KAWRS78K3iIcRxdV9POnKv91D8RJdwa6vyualPfPZakpV
TqzsYzT4q2iGEceHLZnT7WpnkD7KJGvXpGWFGduwTNfgAQDn0W89d0AUUlQL1cOX
kAqMQSQo0s0Cxw6CZiSVHZmlZkiQGK5emUSCPrnPxHt4o1oGVQ5CPSXyk7RaR1PQ
ag+ki6qy+W2FYOgMpCq0PUJPkgds77OjCzrXakMH4fPDBagmh1clAgMBAAGjgbAw
ga0wDAYDVR0TAQH/BAIwADAOBgNVHQ8BAf8EBAMCBaAwEwYDVR0lBAwwCgYIKwYB
BQUHAwEwOAYDVR0RBDEwL4ItcHJvamVjdHMtY29uZmlnLmFnZW50d2VhdmVyLnN2
Yy5jbHVzdGVyLmxvY2FsMB0GA1UdDgQWBBRrMjKZeC+wKCBqGwIPCRR77BnKGDAf
BgNVHSMEGDAWgBT/FpJakXdZ4tpPTVGg5XiyvMb1iDANBgkqhkiG9w0BAQsFAAOC
AQEARXObApuhE2rsBCM8hhrFnsoNC1OgGBkRz4/FCygivCLt8ozS97jf3EKzCYTF
W1jytnZU9oBPzbE8KzIL74d8cUTBIs9+Vkn0TtpsuzPJfnjjLTps5g9dOReWIye+
3/JoJpWMPUcrJrouRZI7KmQV3QF7AIN/n89p28aLnsjg+WIOWLQKttO7WAp7kjLT
AqeAIjPDmb2HbzsWPyH2uF+84o3OBMTuf73O0a2PIlXN5KsXcUdN8lNfvNbxRVzL
8gAM2rqEu92wbwRKrhLtPhb7tpqZsP4d8zC4B4r3fTXk6hxwdW3AEV/cNCZ5MeK1
HLB3pRIOvd0ig9oHgRhj5bH8Nw==
-----END CERTIFICATE-----
`;
const testClientAuthLeafPem = `-----BEGIN CERTIFICATE-----
MIIDnzCCAoegAwIBAgIUZzQUQJW1dqDmipRF23bbnHkEweUwDQYJKoZIhvcNAQEL
BQAwKDEmMCQGA1UEAwwdQWdlbnR3ZWF2ZXIgVGVzdCBJbnRlcm1lZGlhdGUwHhcN
MjYxMDE4MDcwNDE0WhcNNDUxMjE3MDcwNDE0WjA4MTYwNAYDVQQDDC1wcm9qZWN0
cy1jb25maWcuYWdlbnR3ZWF2ZXIuc3ZjLmNsdXN0ZXIubG9jYWwwggEiMA0GCSqG
SIb3DQEBAQUAA4IBDwAwggEKAoIBAQC/eJEJTq5JN5h7XfVZvwyefbX7qhZLE2oX
BYR+xwYSfaIEQCbtr7sLcjZ7JqgDyx28Qlr2EenAI4VT/G5S0wNFj8iMXp0ezLP0
AZLojSuKj7pfsIW+AEGzuR1Uemir7c3AYrcnqcM50kA8u/abfvVd4xNzFIvtOpAK
D3Hp0csDaseP16EQBrFJeghOVUjo26dLMyvh294RoxS1C10ixrI3BEywQ2l4wE1H
jf1s/5Jh6mbSs0Z5ERROTjiDZ5sut5OhL+Qx37gRG4RZxGz4kbRFcmE1CpJKr3tv
+AuVqOashJvoo+H9kjKeealsWxQfPrRHVp5P4S7M7viR4V53KlLJAgMBAAGjgbAw
ga0wDAYDVR0TAQH/BAIwADAOBgNVHQ8BAf8EBAMCBaAwEwYDVR0lBAwwCgYIKwYB
BQUHAwIwOAYDVR0RBDEwL4ItcHJvamVjdHMtY29uZmlnLmFnZW50d2VhdmVyLnN2
Yy5jbHVzdGVyLmxvY2FsMB0GA1UdDgQWBBQwiHaxGdWjNIbFzU+4nvy+A118ejAf
BgNVHSMEGDAWgBT/FpJakXdZ4tpPTVGg5XiyvMb1iDANBgkqhkiG9w0BAQsFAAOC
AQEAUsOGifzMoCsnyOL0QTN1bI8SiMhwC6GTDx+MD8klG/yuV7xEDL0j9kg2XaEP
dT/l4d05mtiUPnKFl8iKytw44ParbUtEjpvgResLtAUTVaOUpOvKCgtwbw6PmzT0
dD8Bv0Ldc62+eFZoWL9+UO8xrauT3uCKf6lTZC/4MB1jv3r10rC7njm/n9aAGYNR
Osoi+2LbGplVmZQnGDxzeFSJTErNVfMl+qWY9XlUOmkUT0CtzaZMEzVbvWEiNV8e
JGGVls0Cf4lj0wQkuoq3Mi0GT6zmOJq6LSpIjNOSFPGmkqzHH+AmrZ7LdtOfAybd
i7C50tds+/ek9Jqh33SGBrJJ2Q==
-----END CERTIFICATE-----
`;const testCaBundle = testRootPem;
const testIntermediateTrustBundle = `${testIntermediatePem}${testRootPem}`;
const testLeafBase64 = Buffer.from(`${testServerLeafPem}${testIntermediatePem}`).toString('base64');
const testClientAuthLeafBase64 = Buffer.from(`${testClientAuthLeafPem}${testIntermediatePem}`).toString('base64');
const testLeafSha256 = sha256(new X509Certificate(testServerLeafPem).raw);

function runBinding({ caBundle = testCaBundle, leafPem = testServerLeafPem } = {}) {
  return {
    projectsOwnerAddress: 'https://projects-config.agentweaver.svc.cluster.local:8443/',
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
$leafRequest = New-Request 'CN=projects-config.agentweaver.svc.cluster.local'
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
$san.AddDnsName('projects-config.agentweaver.svc.cluster.local')
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

test('release preflight allows interrupted run-binding replacement but strict verification requires both maps', () => {
  const binding = runBinding();
  const configMaps = runBindingConfigMaps(binding);
  const bindingKeys = {
    'identity-broker-runtime-config-v2': [
      'IdentityBroker__RunBinding__ProjectsOwnerAddress',
      'IdentityBroker__RunBinding__ProjectsAudience',
      'IdentityBroker__RunBinding__ProjectsServerCertificateSha256',
      'IdentityBroker__RunBinding__ProjectsTrustBundleSha256',
    ],
    'projects-config-runtime-config': [
      'ProjectsConfig__Tls__CertificatePath',
      'ProjectsConfig__Tls__KeyPath',
      'ProjectsConfig__Tls__ServerCertificateSha256',
      'ProjectsConfig__Authentication__BrokerRunBindingAudience',
    ],
  };
  for (const [name, keys] of Object.entries(bindingKeys)) {
    for (const key of keys) delete configMaps[name][key];
  }
  const unconfigured = brokerRunBindingMocks(binding, { configMaps });
  const evidence = verifyBrokerRunBindingConfiguration(binding, target, {
    ...unconfigured, allowMissingSettings: true,
  });
  assert.equal(evidence.runBindingSettingsConfigured, false);

  const brokerConfiguredOnly = runBindingConfigMaps(binding);
  for (const key of bindingKeys['projects-config-runtime-config']) {
    delete brokerConfiguredOnly['projects-config-runtime-config'][key];
  }
  const interrupted = brokerRunBindingMocks(binding, { configMaps: brokerConfiguredOnly });
  const preflight = verifyBrokerRunBindingConfiguration(binding, target, {
    ...interrupted, allowMissingSettings: true,
  });
  assert.equal(preflight.runBindingSettingsConfigured, false);
  assert.throws(() => verifyBrokerRunBindingConfiguration(binding, target, interrupted),
    /projects-config-runtime-config must contain exactly the required/);

  const incompleteBrokerMap = runBindingConfigMaps(binding);
  delete incompleteBrokerMap['identity-broker-runtime-config-v2']
    .IdentityBroker__RunBinding__ProjectsAudience;
  const incomplete = brokerRunBindingMocks(binding, { configMaps: incompleteBrokerMap });
  assert.throws(() => verifyBrokerRunBindingConfiguration(binding, target, {
    ...incomplete, allowMissingSettings: true,
  }), /identity-broker-runtime-config-v2 must contain exactly the required/);
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

test('run-binding descriptor preparation reads only the approved context and public certificate values', () => {
  const calls = [];
  let written;
  const result = prepareBrokerRunBinding({
    argv: ['--output', 'artifacts/release/broker-run-binding.json'],
  }, {
    root,
    run: (_file, args) => {
      calls.push(args);
      if (args[0] === 'config' && args[1] === 'current-context') return 'agentweaver-aks';
      if (args.includes('configmap')) {
        return JSON.stringify({
          metadata: { name: 'service-backend-ca', namespace: 'agentweaver' },
          data: { 'ca.crt': testCaBundle },
        });
      }
      assert.deepEqual(args.slice(-3), [
        '--namespace', 'agentweaver', '--output=jsonpath={.data.tls\\.crt}',
      ]);
      assert.ok(!args.some(argument => argument.includes('tls.key')));
      return testLeafBase64;
    },
    mkdir: () => undefined,
    writeFile: (file, contents, options) => { written = { file, contents, options }; },
  });

  assert.deepEqual(result.binding, runBinding());
  assert.equal(written.file, path.join(root, 'artifacts', 'release', 'broker-run-binding.json'));
  assert.equal(written.options.flag, 'wx');
  assert.deepEqual(JSON.parse(written.contents), runBinding());
  assert.deepEqual(calls.map(args => args[0]), ['config', '--context', '--context']);
});

test('run-binding descriptor preparation refuses a different kubectl context before reading resources', () => {
  let reads = 0;
  assert.throws(() => prepareBrokerRunBinding({}, {
    root,
    run: () => { reads++; return 'another-cluster'; },
    mkdir: () => assert.fail('an invalid context must not write a descriptor'),
    writeFile: () => assert.fail('an invalid context must not write a descriptor'),
  }), /current kubectl context must be agentweaver-aks/);
  assert.equal(reads, 1);
});

test('source-only integration publication uses a separate workflow without entering release or deployment paths', () => {
  const workflowPath = path.join(root, '.github', 'workflows', 'v1-integration-images.yml');
  const workflow = readFileSync(workflowPath, 'utf8');
  assert.match(workflow, /workflow_dispatch:/);
  assert.match(workflow, /release:integration-pack/);
  assert.match(workflow, /release:integration-publish/);
  assert.doesNotMatch(workflow, /RELEASE_NUGET_|release:deploy|merge-packs\.mjs/);
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

  // A caller-supplied List is validated against all catalog consumers and their annotations.
  const result = await deployIntegration(deployOptions(fixture, {
    providerCatalogConfigMapName: undefined,
    providerCatalogSourceCommit: undefined,
    runtimeManifest,
  }), dependencies);

  assert.equal(result.renderedResources, 18);
  assert.equal(runtimeManifest.items
    .filter(item => item.kind === 'Deployment' &&
      ['knowledge', 'projects-config', 'orchestrator'].includes(item.metadata.name)).length, 3);
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

test('execute mode forwards fetch and delay to pre/post-apply registry verification and records exact readbacks', async t => {
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
  dependencies.fetch = async () => assert.fail('stub verifiers do not perform network reads');
  dependencies.delay = async () => assert.fail('stub verifiers do not delay');
  const verificationCalls = { service: 0, host: 0 };
  for (const [name, key] of [['verifyServiceImage', 'service'], ['verifyAgentHostImage', 'host']]) {
    const verify = dependencies[name];
    dependencies[name] = async (image, fetchImpl, delay) => {
      assert.equal(fetchImpl, dependencies.fetch);
      assert.equal(delay, dependencies.delay);
      verificationCalls[key]++;
      return verify(image);
    };
  }

  const result = await deployIntegration(options, dependencies);
  assert.deepEqual(verificationCalls, { service: 20, host: 2 });
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
