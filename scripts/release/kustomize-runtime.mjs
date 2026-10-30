/** Parses rendered Kustomize output into Kubernetes resources for deployment and migration checks. */
import { parseAllDocuments } from 'yaml';

function fail(message) {
  throw new Error(`Kustomize rendering: ${message}`);
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
