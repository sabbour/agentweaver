import { test } from 'node:test';
import assert from 'node:assert/strict';
import { X509Certificate } from 'node:crypto';
import { createSecureContext } from 'node:tls';
import { bootstrapIdentityBrokerState, generateBrokerSigning, projectBrokerState } from '../lib/identity-broker-state.mjs';

const namespace = 'agentweaver-v1-p0';
const labels = { 'agentweaver.io/service': 'identity-broker', 'agentweaver.io/managed-by': 'installer' };
const signing = { kind: 'Secret', type: 'Opaque', metadata: {
  name: 'identity-broker-signing', namespace, uid: 'signing-uid', labels,
}, data: { 'signing.pfx': 'PRIVATE-PFX', password: 'PRIVATE-PASSWORD' } };
const claim = { kind: 'PersistentVolumeClaim', metadata: {
  name: 'identity-broker-key-ring', namespace, uid: 'claim-uid', labels,
}, spec: { accessModes: ['ReadWriteOnce'], volumeMode: 'Filesystem' }, status: { phase: 'Pending' } };

function collaborators({ secret = signing, pvc = claim, denied, writeFailure = false } = {}) {
  const calls = [];
  let generationCount = 0;
  return {
    calls, get generationCount() { return generationCount; },
    withKubeconfig(config, action) { return action(['--kubeconfig', 'owned-user-config']); },
    generateSigning() { generationCount++; return { ...signing, immutable: true }; },
    execKubectl(args, options) {
      calls.push({ args, options });
      assert.ok(args.includes('--kubeconfig'));
      assert.ok(args.includes('--request-timeout=30s'));
      if (args.includes('get')) {
        const resource = args[args.indexOf('get') + 1];
        const object = resource === 'namespaces' ? { kind: 'Namespace', metadata: {
          name: namespace, labels: { 'pod-security.kubernetes.io/enforce': 'restricted' },
        } } : resource === 'secrets' ? secret : pvc;
        return { status: 0, stdout: JSON.stringify(options.projectJson({ items: object ? [object] : [] })), stderr: '' };
      }
      if (args.includes('can-i')) return { status: denied ? 1 : 0, stdout: denied ? 'no' : 'yes', stderr: denied ? 'Forbidden' : '' };
      if (args.includes('create')) {
        if (writeFailure) return { status: 1, stdout: 'PRIVATE-PFX', stderr: 'PRIVATE-PASSWORD' };
        if (args.includes('-')) secret = signing;
        else pvc = claim;
        return { status: 0, stdout: 'created', stderr: '' };
      }
      throw new Error('Unexpected command');
    },
  };
}

test('existing signing and key-ring state is metadata-only and remains unchanged', () => {
  const deps = collaborators();
  const receipt = bootstrapIdentityBrokerState({}, deps);
  assert.equal(receipt.signing.created, false);
  assert.equal(receipt.keyRing.created, false);
  assert.equal(receipt.runtimeVerified, false);
  assert.equal(deps.generationCount, 0);
  assert.ok(!deps.calls.some(call => call.args.includes('create')));
  assert.ok(!JSON.stringify(projectBrokerState(signing)).includes('PRIVATE'));
  assert.ok(!JSON.stringify(receipt).includes('PRIVATE'));
});

test('first install checks permissions, creates only exact signing and PVC, and reads native UIDs', () => {
  const deps = collaborators({ secret: null, pvc: null });
  const receipt = bootstrapIdentityBrokerState({}, deps);
  assert.equal(deps.generationCount, 1);
  assert.equal(receipt.signing.created, true);
  assert.equal(receipt.signing.uid, 'signing-uid');
  assert.equal(receipt.keyRing.created, true);
  const creates = deps.calls.filter(call => call.args.includes('create') && !call.args.includes('can-i'));
  assert.equal(creates.length, 2);
  assert.equal(creates[0].args[creates[0].args.indexOf('--filename') + 1], '-');
  assert.ok(!creates[0].args.join(' ').includes('PRIVATE'));
  assert.ok(creates[0].options.input.includes('PRIVATE'));
  assert.ok(!deps.calls.some(call => call.args.includes('apply') || call.args.includes('delete') || call.args.includes('patch')));
});

test('partial state preserves original keys and rejects orphaned durable data', () => {
  const orphan = collaborators({ secret: null });
  assert.throws(() => bootstrapIdentityBrokerState({}, orphan), /recover the original/);
  assert.equal(orphan.generationCount, 0);
  const incomplete = collaborators({ secret: { ...signing, data: { password: 'private' } } });
  assert.throws(() => bootstrapIdentityBrokerState({}, incomplete), /refusing replacement or rotation/);
  const partial = collaborators({ pvc: null });
  assert.equal(bootstrapIdentityBrokerState({}, partial).keyRing.created, true);
  assert.equal(partial.generationCount, 0);
});

test('empty or foreign signing material blocks creation of durable state without exposing values', () => {
  for (const secret of [
    { ...signing, data: { 'signing.pfx': '', password: '' } },
    { ...signing, metadata: { ...signing.metadata, labels: { ...labels, 'agentweaver.io/managed-by': 'foreign' } } },
  ]) {
    const deps = collaborators({ secret, pvc: null });
    assert.throws(() => bootstrapIdentityBrokerState({}, deps), /refusing replacement or rotation/);
    assert.equal(deps.generationCount, 0);
    assert.ok(!deps.calls.some(call => call.args.includes('create')));
    assert.ok(!JSON.stringify(projectBrokerState(secret)).includes('PRIVATE'));
  }
});
test('permission failures stop before generation; secret-write errors never echo private input', () => {
  const denied = collaborators({ secret: null, pvc: null, denied: true });
  assert.throws(() => bootstrapIdentityBrokerState({}, denied), /permission denied.*Forbidden/);
  assert.equal(denied.generationCount, 0);
  const failed = collaborators({ secret: null, pvc: null, writeFailure: true });
  assert.throws(() => bootstrapIdentityBrokerState({}, failed), error =>
    /create failed.*private output omitted/.test(error.message) && !error.message.includes('PRIVATE-'));
});

test('actual .NET generator produces a password-protected RSA production PFX without persisted private files', () => {
  const secret = generateBrokerSigning(process.cwd());
  const password = Buffer.from(secret.data.password, 'base64').toString('utf8');
  const pfx = Buffer.from(secret.data['signing.pfx'], 'base64');
  const context = createSecureContext({ pfx, passphrase: password });
  const certificate = new X509Certificate(context.context.getCertificate());
  assert.equal(certificate.publicKey.asymmetricKeyType, 'rsa');
  assert.equal(certificate.publicKey.asymmetricKeyDetails.modulusLength, 3072);
  assert.ok(Date.parse(certificate.validTo) > Date.now() + 360 * 24 * 60 * 60 * 1000);
  assert.throws(() => createSecureContext({ pfx, passphrase: 'wrong-password' }));
  assert.equal(secret.immutable, true);
  assert.equal(secret.metadata.namespace, namespace);
  pfx.fill(0);
});
