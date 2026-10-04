export const AI_EXECUTION_SIGNING_ENV = "AiExecution__ProviderKeySigningKey";

function indentOf(line) {
  return line.match(/^\s*/)[0].length;
}

function scalar(value) {
  const trimmed = value.trim();
  if (trimmed === "") return "";
  if ((trimmed.startsWith('"') && trimmed.endsWith('"')) || (trimmed.startsWith("'") && trimmed.endsWith("'"))) {
    return trimmed.slice(1, -1);
  }
  if (trimmed === "true") return true;
  if (trimmed === "false") return false;
  if (trimmed === "null") return null;
  if (/^-?\d+$/.test(trimmed)) return Number(trimmed);
  if (trimmed.startsWith("[") || trimmed.startsWith("{")) {
    try {
      return JSON.parse(trimmed);
    } catch {
      return trimmed;
    }
  }
  return trimmed;
}

function parseKeyValue(text) {
  const match = text.match(/^([^:]+):(.*)$/);
  if (!match) return null;
  return { key: match[1].trim(), value: match[2].trim() };
}

function parseYamlDocument(text) {
  const lines = text
    .split(/\r?\n/)
    .filter((line) => line.trim() && !line.trimStart().startsWith("#"));

  function parseBlock(index, indent) {
    const line = lines[index];
    if (line === undefined || indentOf(line) < indent) return [null, index];
    return line.slice(indent).startsWith("- ")
      ? parseArray(index, indent)
      : parseObject(index, indent);
  }

  function parseObject(index, indent) {
    const object = {};
    while (index < lines.length) {
      const line = lines[index];
      const currentIndent = indentOf(line);
      if (currentIndent < indent) break;
      if (currentIndent > indent) break;
      const trimmed = line.slice(indent);
      if (trimmed.startsWith("- ")) break;
      const pair = parseKeyValue(trimmed);
      if (!pair) {
        index += 1;
        continue;
      }
      if (pair.value === "") {
        const childIndent = lines[index + 1] === undefined ? indent + 2 : indentOf(lines[index + 1]);
        const [value, next] = parseBlock(index + 1, childIndent);
        object[pair.key] = value ?? {};
        index = next;
      } else {
        object[pair.key] = scalar(pair.value);
        index += 1;
        if ((pair.value === "|" || pair.value === ">") && index < lines.length) {
          while (index < lines.length && indentOf(lines[index]) > currentIndent) index += 1;
        }
      }
    }
    return [object, index];
  }

  function parseArray(index, indent) {
    const items = [];
    while (index < lines.length) {
      const line = lines[index];
      const currentIndent = indentOf(line);
      if (currentIndent < indent) break;
      if (currentIndent !== indent || !line.slice(indent).startsWith("- ")) break;
      const rest = line.slice(indent + 2);
      if (rest.trim() === "") {
        const [value, next] = parseBlock(index + 1, indent + 2);
        items.push(value);
        index = next;
        continue;
      }
      const pair = parseKeyValue(rest);
      if (pair) {
        const item = {};
        if (pair.value === "") {
          const childIndent = lines[index + 1] === undefined ? indent + 2 : indentOf(lines[index + 1]);
          const [value, next] = parseBlock(index + 1, childIndent);
          item[pair.key] = value ?? {};
          index = next;
        } else {
          item[pair.key] = scalar(pair.value);
          index += 1;
        }
        if (index < lines.length && indentOf(lines[index]) > indent) {
          const [extra, next] = parseObject(index, indent + 2);
          Object.assign(item, extra);
          index = next;
        }
        items.push(item);
      } else {
        items.push(scalar(rest));
        index += 1;
        if ((rest.trim() === "|" || rest.trim() === ">") && index < lines.length) {
          while (index < lines.length && indentOf(lines[index]) > currentIndent) index += 1;
        }
      }
    }
    return [items, index];
  }

  return parseBlock(0, indentOf(lines[0] ?? ""))[0];
}

export function parseManifestDocuments(manifestText) {
  const text = String(manifestText ?? "").trim();
  if (!text) return [];
  if (text.startsWith("{") || text.startsWith("[")) {
    const parsed = JSON.parse(text);
    if (Array.isArray(parsed)) return parsed;
    if (parsed?.kind === "List" && Array.isArray(parsed.items)) return parsed.items;
    return [parsed];
  }
  return text
    .split(/\r?\n---\r?\n/)
    .map((doc) => doc.trim())
    .filter(Boolean)
    .map(parseYamlDocument)
    .filter(Boolean);
}

function containerEnv(deployment) {
  return deployment?.spec?.template?.spec?.containers?.flatMap((container) =>
    (Array.isArray(container.env) ? container.env : []).map((entry) => ({
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
