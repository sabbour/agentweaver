import YAML from "yaml";

export const AI_EXECUTION_SIGNING_ENV = "AiExecution__ProviderKeySigningKey";

export function parseManifestDocuments(yamlText) {
  return YAML.parseAllDocuments(yamlText)
    .map((doc) => doc.toJSON())
    .filter(Boolean);
}

function containerEnv(deployment) {
  return deployment?.spec?.template?.spec?.containers?.flatMap((container) =>
    (container.env ?? []).map((entry) => ({
      container: container.name,
      name: entry.name,
      valueFrom: entry.valueFrom ?? null,
      value: entry.value,
    })),
  ) ?? [];
}

function stableSecretRef(ref) {
  const secret = ref?.secretKeyRef;
  if (!secret?.name || !secret?.key) return null;
  return {
    name: String(secret.name),
    key: String(secret.key),
    optional: secret.optional === undefined ? false : Boolean(secret.optional),
  };
}

export function collectAiExecutionSigningRefs(docs, {
  requiredDeployments = ["agentweaver-api", "agentweaver-worker"],
} = {}) {
  const deployments = new Map();
  for (const doc of docs) {
    if (doc?.kind === "Deployment" && doc?.metadata?.name) {
      deployments.set(String(doc.metadata.name), doc);
    }
  }

  return requiredDeployments.map((deploymentName) => {
    const deployment = deployments.get(deploymentName);
    const entries = containerEnv(deployment)
      .filter((entry) => entry.name === AI_EXECUTION_SIGNING_ENV);
    return {
      deployment: deploymentName,
      present: entries.length > 0,
      count: entries.length,
      refs: entries.map((entry) => stableSecretRef(entry.valueFrom)),
      raw: entries,
    };
  });
}

export function assertSharedAiExecutionSigningSecret(docs, options = {}) {
  const refs = collectAiExecutionSigningRefs(docs, options);
  const failures = [];
  for (const entry of refs) {
    if (!entry.present) {
      failures.push(`${entry.deployment} is missing ${AI_EXECUTION_SIGNING_ENV}`);
      continue;
    }
    if (entry.count !== 1) {
      failures.push(`${entry.deployment} must define exactly one ${AI_EXECUTION_SIGNING_ENV}; found ${entry.count}`);
      continue;
    }
    if (!entry.refs[0]) {
      failures.push(`${entry.deployment} must source ${AI_EXECUTION_SIGNING_ENV} from a secretKeyRef`);
    }
  }

  const canonical = refs.find((entry) => entry.refs[0])?.refs[0] ?? null;
  if (canonical) {
    for (const entry of refs) {
      const ref = entry.refs[0];
      if (!ref) continue;
      if (ref.name !== canonical.name || ref.key !== canonical.key || ref.optional !== canonical.optional) {
        failures.push(
          `${entry.deployment} uses ${AI_EXECUTION_SIGNING_ENV} from secret ${ref.name}/${ref.key}`
          + ` (optional=${ref.optional}); expected ${canonical.name}/${canonical.key}`
          + ` (optional=${canonical.optional})`,
        );
      }
    }
  }

  if (failures.length > 0) {
    throw new Error(
      `AI execution signing-key deployment contract failed:\n- ${failures.join("\n- ")}`,
    );
  }

  return {
    secretName: canonical.name,
    secretKey: canonical.key,
    deployments: refs.map((entry) => entry.deployment),
  };
}
