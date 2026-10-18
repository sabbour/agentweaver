/**
 * Parses Kustomize output and verifies that release image transformers match the publication receipt.
 * The release helper applies the rendered source bundle instead of generating shared workloads.
 */
import { parseAllDocuments } from 'yaml';
import { componentImageRepository } from './pack.mjs';

function fail(message) {
  throw new Error(`release deployment: ${message}`);
}

/** Parses kubectl kustomize output and rejects empty or invalid YAML documents. */
export function parseKustomizeResources(text) {
  if (typeof text !== 'string' || !text.trim()) fail('Kustomize rendered no Kubernetes resources');
  const documents = parseAllDocuments(text);
  const items = [];
  for (const document of documents) {
    if (document.errors.length) {
      fail(`Kustomize output contains invalid YAML: ${document.errors.map(({ message }) => message).join('; ')}`);
    }
    const resource = document.toJS();
    if (resource !== null && resource !== undefined) items.push(resource);
  }
  if (!items.length) fail('Kustomize rendered no Kubernetes resources');
  return { apiVersion: 'v1', kind: 'List', items };
}

/** Builds canonical Kustomize image entries from digest-verified publication data. */
export function createKustomizeImageEntries(images) {
  if (!Array.isArray(images) || images.length !== 10) {
    fail('Kustomize image pins require the exact ten published service images');
  }
  const entries = images.map((image) => {
    const repository = `ghcr.io/sabbour/${componentImageRepository(image.id)}`;
    const digest = image.id === 'Agentweaver.AgentHost'
      ? image.sandboxImageDigest
      : image.image?.slice(image.image.lastIndexOf('@') + 1);
    if (typeof digest !== 'string' || !/^sha256:[a-f0-9]{64}$/.test(digest)) {
      fail(`the verified publication receipt has no exact image digest for ${image.id}`);
    }
    return { name: repository, newName: repository, digest };
  });
  if (new Set(entries.map(({ name }) => name)).size !== entries.length) {
    fail('publication receipt contains duplicate Kustomize image repositories');
  }
  return entries;
}

/** Requires every Kustomize image transform to match one receipt repository and digest exactly. */
export function validateKustomizeImagePins(kustomization, images) {
  const expected = createKustomizeImageEntries(images);
  const actual = kustomization?.images;
  if (!Array.isArray(actual) || actual.length !== expected.length) {
    fail('Kustomization must pin exactly the ten image repositories in the publication receipt');
  }
  const actualByName = new Map();
  for (const entry of actual) {
    if (!entry || typeof entry.name !== 'string' || actualByName.has(entry.name)) {
      fail('Kustomization contains an invalid or duplicate image transform');
    }
    actualByName.set(entry.name, entry);
  }
  for (const entry of expected) {
    const pinned = actualByName.get(entry.name);
    if (pinned?.newName !== entry.newName || pinned?.digest !== entry.digest ||
        pinned.newTag !== undefined) {
      fail(`Kustomization image pin for ${entry.name} must match the publication receipt exactly`);
    }
  }
  return expected;
}
