import { readFile } from 'node:fs/promises';

const isObject = (value) => value !== null && typeof value === 'object' && !Array.isArray(value);
const properties = (schema) => isObject(schema?.properties) ? schema.properties : {};
const required = (schema) => new Set(Array.isArray(schema?.required) ? schema.required : []);
const normalizeRequired = (value) => Array.isArray(value) ? Object.fromEntries(value.map((name) => [name, null])) : (value ?? {});

function typeMatches(schema, expected) {
  if (!expected) return true;
  return Array.isArray(schema?.type) ? schema.type.includes(expected) : schema?.type === expected;
}

function checkShape(schema, requirement, label) {
  const errors = [];
  const props = properties(schema);
  const requiredNames = required(schema);
  for (const [name, expectedType] of Object.entries(normalizeRequired(requirement?.requires))) {
    if (!(name in props)) errors.push(`${label}.${name} is missing`);
    else {
      if (expectedType && !typeMatches(props[name], expectedType)) {
        errors.push(`${label}.${name} type changed: expected ${expectedType}, got ${JSON.stringify(props[name]?.type ?? null)}`);
      }
      if (label === 'input' && !requiredNames.has(name)) errors.push(`input.${name} is no longer required`);
    }
  }
  return errors;
}

export async function loadCapabilitiesContract(file) {
  const contract = JSON.parse(await readFile(file, 'utf8'));
  if (!isObject(contract) || !Array.isArray(contract.capabilities) || typeof contract.contractVersion !== 'string') {
    throw new Error(`Invalid capabilities contract: ${file}`);
  }
  return contract;
}

/** Runs beside discovery; its names never become persona-driving action space. */
export function checkCapabilities(liveTools, contract) {
  const tools = Array.isArray(liveTools) ? liveTools : [];
  const byName = new Map(tools.filter((tool) => typeof tool?.name === 'string').map((tool) => [tool.name, tool]));
  const results = (contract.capabilities ?? []).map((capability) => {
    const names = Array.isArray(capability.tools) ? capability.tools : [];
    const tool = names.map((name) => byName.get(name)).find(Boolean);
    if (!tool) return {
      capability: capability.capability, status: capability.optional ? 'SKIP' : 'FAIL', expectedTools: names,
      message: capability.optional ? 'optional capability is absent' : `required tool missing (expected one of: ${names.join(', ')})`,
    };
    const errors = [...checkShape(tool.inputSchema, capability.in, 'input'), ...checkShape(tool.outputSchema, capability.out, 'output')];
    return { capability: capability.capability, tool: tool.name, status: errors.length ? 'FAIL' : 'PASS', message: errors.join('; ') || 'compatible', errors };
  });
  const requiredNames = new Set((contract.capabilities ?? []).flatMap((item) => item.tools ?? []));
  const additiveTools = tools.map((tool) => tool.name).filter((name) => !requiredNames.has(name));
  return { contractVersion: contract.contractVersion, ok: results.every((item) => item.status !== 'FAIL'), results, drift: additiveTools.length ? [{ kind: 'additive-tools', tools: additiveTools }] : [] };
}

export function assertCapabilitiesCompatible(report) {
  if (report.ok) return report;
  const failed = report.results.filter((item) => item.status === 'FAIL').map((item) => `${item.capability}: ${item.message}`).join(' | ');
  const error = new Error(`CONTRACT FAIL: ${failed}`);
  error.report = report;
  throw error;
}
