import { createHash } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';

// Environment BYOK model bindings that an operator keeps outside product source (#2616).
// The deploy helper merges them into the rendered runtime ConfigMaps, so product defaults stay clean.
export const MODEL_BINDINGS_OVERLAY_KIND = 'agentweaver.model-bindings-overlay';

const AGENTHOST_RUNTIME_CONFIG = 'agenthost-runtime-v1';
const EVENTS_RUNTIME_CONFIG = 'events-and-sessions-runtime-config';
const PROJECTS_RUNTIME_CONFIG = 'projects-config-runtime-config';
const EVENTS_DEPLOYMENT = 'events-and-sessions';
export const EVENTS_RUNTIME_CONFIG_ANNOTATION = 'agentweaver.io/runtime-config-sha256';
const BINDING_PREFIX = 'AgentHost__ModelBindings__';
const REVISION_KEY = 'AgentHost__ModelBindingsRevision';
const AZURE_COST_PREFIX = 'EventsAndSessions__Cost__Azure__';
const MODEL_RATES_PREFIX = `${AZURE_COST_PREFIX}RateCard__ModelRates__`;
const EFFORTS = ['low', 'medium', 'high', 'xhigh'];
const IDENTIFIER = /^[A-Za-z0-9._:-]{1,256}$/;
// Reference and deployment names become ConfigMap key segments, where ':' and '__' are separators.
const KEY_SEGMENT = /^(?!_)(?!.*__)(?!.*_$)[A-Za-z0-9._-]{1,256}$/;
const RATE = /^(?:0|[1-9]\d*)(?:\.\d*[1-9])?$/;
const DATE = /^\d{4}-\d{2}-\d{2}$/;
const RATE_CARD_ID = /^[A-Za-z0-9._-]{1,128}$/;
const API_VERSION = /^[A-Za-z0-9-]{1,32}$/;

function fail(message) {
  throw new Error(`Kustomize deployment: ${message}`);
}

const isObject = value => value !== null && typeof value === 'object' && !Array.isArray(value);

function requireFields(value, allowed, required, location) {
  if (!isObject(value)) fail(`${location} must be a JSON object.`);
  const unexpected = Object.keys(value).find(key => !allowed.includes(key));
  if (unexpected) {
    fail(`${location} has the unsupported field "${unexpected}". Do not put an API key in the file; ` +
      'the project configuration holds the Key Vault secret reference.');
  }
  const missing = required.find(key => value[key] === undefined);
  if (missing) fail(`${location} must set "${missing}".`);
}

function requireText(value, pattern, location) {
  if (typeof value !== 'string' || !pattern.test(value)) fail(`${location} has an invalid value.`);
  return value;
}

function validateBinding(reference, binding) {
  const location = `The model bindings file binding "${reference}"`;
  requireText(reference, KEY_SEGMENT, 'A model bindings file reference');
  requireFields(binding, ['ModelId', 'SourceMode', 'Provider', 'SupportedReasoningEfforts', 'PromptCapacityTokens'],
    ['ModelId', 'SourceMode', 'Provider', 'SupportedReasoningEfforts'], location);
  requireText(binding.ModelId, KEY_SEGMENT, `${location} ModelId`);
  if (binding.SourceMode !== 'Byok') fail(`${location} must have SourceMode Byok.`);
  const provider = binding.Provider;
  requireFields(provider, ['Type', 'BaseUrl', 'WireApi', 'AzureApiVersion'], ['Type', 'BaseUrl', 'WireApi'],
    `${location} Provider`);
  if (typeof provider.BaseUrl !== 'string') fail(`${location} Provider.BaseUrl must be a string.`);
  if (!['responses', 'completions'].includes(provider.WireApi)) {
    fail(`${location} Provider.WireApi must be responses or completions.`);
  }
  const validVersion = provider.AzureApiVersion === undefined ||
    (typeof provider.AzureApiVersion === 'string' && API_VERSION.test(provider.AzureApiVersion));
  if (!validVersion ||
      !(provider.Type === 'openai' && provider.AzureApiVersion === undefined ||
        provider.Type === 'azure' && provider.AzureApiVersion !== undefined)) {
    fail(`${location} must use Provider.Type openai without AzureApiVersion, or azure with AzureApiVersion.`);
  }
  const efforts = binding.SupportedReasoningEfforts;
  if (!Array.isArray(efforts) || efforts.length === 0 || new Set(efforts).size !== efforts.length ||
      efforts.some(effort => !EFFORTS.includes(effort))) {
    fail(`${location} SupportedReasoningEfforts must list unique values from ${EFFORTS.join(', ')}.`);
  }
  if (binding.PromptCapacityTokens !== undefined &&
      (!Number.isSafeInteger(binding.PromptCapacityTokens) || binding.PromptCapacityTokens <= 0)) {
    fail(`${location} PromptCapacityTokens must be a positive integer.`);
  }
}

/** Reads and validates an environment model bindings file without the rendered product maps. */
export function parseModelBindingsOverlay(text) {
  let overlay;
  try {
    overlay = JSON.parse(text);
  } catch {
    fail('The model bindings file must be valid JSON.');
  }
  requireFields(overlay, ['kind', 'schemaVersion', 'baseRevision', 'baseModelBindings', 'revision',
    'modelBindings', 'rateCard', 'modelRates'], ['kind', 'schemaVersion', 'baseRevision', 'baseModelBindings',
    'revision', 'modelBindings', 'rateCard', 'modelRates'], 'The model bindings file');
  if (overlay.kind !== MODEL_BINDINGS_OVERLAY_KIND || overlay.schemaVersion !== 1) {
    fail(`The model bindings file must have kind ${MODEL_BINDINGS_OVERLAY_KIND} and schemaVersion 1.`);
  }
  requireText(overlay.baseRevision, IDENTIFIER, 'The model bindings file baseRevision');
  requireText(overlay.revision, IDENTIFIER, 'The model bindings file revision');
  if (overlay.revision === overlay.baseRevision) {
    fail('The model bindings file revision must differ from its baseRevision.');
  }
  if (!isObject(overlay.baseModelBindings)) fail('The model bindings file baseModelBindings must be an object.');
  if (!isObject(overlay.modelBindings) || Object.keys(overlay.modelBindings).length === 0) {
    fail('The model bindings file must contain at least one binding in modelBindings.');
  }
  for (const [reference, binding] of Object.entries(overlay.modelBindings)) validateBinding(reference, binding);
  requireFields(overlay.rateCard, ['id', 'version'], ['id', 'version'], 'The model bindings file rateCard');
  requireText(overlay.rateCard.id, RATE_CARD_ID, 'The model bindings file rateCard.id');
  requireText(overlay.rateCard.version, DATE, 'The model bindings file rateCard.version');
  if (!isObject(overlay.modelRates)) fail('The model bindings file modelRates must be an object.');
  const models = new Set(Object.values(overlay.modelBindings).map(binding => binding.ModelId));
  for (const [model, rates] of Object.entries(overlay.modelRates)) {
    requireText(model, KEY_SEGMENT, 'A model bindings file modelRates name');
    if (!models.has(model)) fail(`The model bindings file has rates for "${model}", but no binding uses it.`);
    requireFields(rates, ['input', 'cachedInput', 'output'], ['input', 'output'],
      `The model bindings file modelRates "${model}"`);
    for (const [category, rate] of Object.entries(rates)) {
      if (typeof rate !== 'string' || !RATE.test(rate) || Number(rate) <= 0) {
        fail(`The model bindings file rate ${model}.${category} must be a positive decimal string, such as "2.5".`);
      }
    }
  }
  const unpriced = [...models].filter(model => !Object.hasOwn(overlay.modelRates, model));
  if (unpriced.length) fail(`The model bindings file has no modelRates for ${unpriced.join(', ')}.`);
  return overlay;
}

function findMap(resources, name) {
  const matches = resources.filter(resource =>
    resource.kind === 'ConfigMap' && resource.metadata?.name === name);
  if (matches.length !== 1 || !isObject(matches[0].data)) {
    fail(`Kustomize must render exactly one ConfigMap/${name} with data.`);
  }
  return matches[0];
}

function readAgentHost(resource) {
  let settings;
  try {
    settings = JSON.parse(resource.data['appsettings.json']);
  } catch {
    fail(`ConfigMap/${AGENTHOST_RUNTIME_CONFIG} must contain valid appsettings.json.`);
  }
  if (!isObject(settings?.AgentHost?.ModelBindings)) {
    fail(`ConfigMap/${AGENTHOST_RUNTIME_CONFIG} must contain AgentHost model bindings.`);
  }
  return settings;
}

function eventsBindingKeys(reference, binding) {
  const prefix = `${BINDING_PREFIX}${reference}__`;
  const provider = binding.Provider;
  return [
    [`${prefix}ModelId`, binding.ModelId],
    [`${prefix}SourceMode`, 'Byok'],
    [`${prefix}Provider__Type`, provider.Type],
    [`${prefix}Provider__BaseUrl`, provider.BaseUrl],
    [`${prefix}Provider__WireApi`, provider.WireApi],
    ...(provider.AzureApiVersion === undefined ? [] : [[`${prefix}Provider__AzureApiVersion`, provider.AzureApiVersion]]),
    ...(binding.PromptCapacityTokens === undefined ? [] : [[`${prefix}PromptCapacityTokens`, String(binding.PromptCapacityTokens)]]),
    ...binding.SupportedReasoningEfforts.map((effort, index) => [`${prefix}SupportedReasoningEfforts__${index}`, effort]),
  ];
}

function projectsBindingKeys(reference, binding) {
  const prefix = `${BINDING_PREFIX}${reference}__`;
  return [
    [`${prefix}SourceMode`, 'Byok'],
    [`${prefix}Enabled`, 'true'],
    ...binding.SupportedReasoningEfforts.map((effort, index) => [`${prefix}SupportedReasoningEfforts__${index}`, effort]),
  ];
}

function azureCostKeys(overlay) {
  const keys = [
    ['ResourceId', `azure-cost:${overlay.revision}`],
    ['ResourceGeneration', '1'],
    ['OptionsRevision', overlay.revision],
    ['OptionsSchemaVersion', '1'],
    ['PricingModel', 'StandardTokens'],
    ['RateCard__Id', overlay.rateCard.id],
    ['RateCard__Version', overlay.rateCard.version],
    ['RateCard__MeterSource', 'byok.tokens'],
    ['RateCard__Unit', 'USD'],
    ['RateCard__NanoUnitsPerUnit', '1000000'],
  ].map(([key, value]) => [`${AZURE_COST_PREFIX}${key}`, value]);
  for (const [model, rates] of Object.entries(overlay.modelRates)) {
    for (const category of ['input', 'cachedInput', 'output']) {
      if (rates[category] !== undefined) keys.push([`${MODEL_RATES_PREFIX}${model}__${category}`, rates[category]]);
    }
  }
  return keys;
}

// Adds binding keys after the last existing binding key and sets the revision, as `models byok plan` does.
function mergeFlatKeys(data, bindingKeys, revision, extraKeys = []) {
  const entries = Object.entries(data);
  let last = -1;
  entries.forEach(([key], index) => { if (key.startsWith(BINDING_PREFIX)) last = index; });
  entries.splice(last + 1, 0, ...bindingKeys);
  const merged = Object.fromEntries(entries.map(([key, value]) => [key, key === REVISION_KEY ? revision : value]));
  for (const [key, value] of extraKeys) merged[key] = value;
  return merged;
}

/**
 * Merges an environment model bindings file into the rendered AgentHost, Events, and Projects maps.
 * The Events and Projects keys come from the AgentHost bindings, so the maps cannot disagree.
 */
export function applyModelBindingsOverlay(resources, overlay) {
  const agentHostMap = findMap(resources, AGENTHOST_RUNTIME_CONFIG);
  const eventsMap = findMap(resources, EVENTS_RUNTIME_CONFIG);
  const projectsMap = findMap(resources, PROJECTS_RUNTIME_CONFIG);
  const settings = readAgentHost(agentHostMap);
  const agentHost = settings.AgentHost;
  const revisions = [agentHost.ModelBindingsRevision, eventsMap.data[REVISION_KEY], projectsMap.data[REVISION_KEY]];
  if (revisions.some(revision => revision !== overlay.baseRevision) ||
      !isDeepStrictEqual(agentHost.ModelBindings, overlay.baseModelBindings)) {
    fail(`The product model bindings (revision ${agentHost.ModelBindingsRevision}) are not the base of the model ` +
      `bindings file (baseRevision ${overlay.baseRevision}). Make a new file with ` +
      '`agentweaver models byok plan --overlay-output` from this source.');
  }
  if (Object.values(agentHost.ModelBindings).some(binding => binding?.SourceMode === 'Byok') ||
      Object.keys(eventsMap.data).some(key => key.startsWith(AZURE_COST_PREFIX))) {
    fail('The product source already has a BYOK binding or an Azure rate card. Keep environment BYOK bindings ' +
      'only in the model bindings file.');
  }
  for (const reference of Object.keys(overlay.modelBindings)) {
    const prefix = `${BINDING_PREFIX}${reference}__`;
    if (Object.hasOwn(agentHost.ModelBindings, reference) ||
        [eventsMap, projectsMap].some(map => Object.keys(map.data).some(key => key.startsWith(prefix)))) {
      fail(`Model reference "${reference}" already exists in the product source. Choose another reference.`);
    }
  }

  const entries = Object.entries(overlay.modelBindings);
  agentHost.ModelBindingsRevision = overlay.revision;
  for (const [reference, binding] of entries) agentHost.ModelBindings[reference] = structuredClone(binding);
  agentHostMap.data['appsettings.json'] = JSON.stringify(settings, null, 2);
  eventsMap.data = mergeFlatKeys(eventsMap.data,
    entries.flatMap(([reference, binding]) => eventsBindingKeys(reference, binding)),
    overlay.revision, azureCostKeys(overlay));
  projectsMap.data = mergeFlatKeys(projectsMap.data,
    entries.flatMap(([reference, binding]) => projectsBindingKeys(reference, binding)), overlay.revision);
  return { revision: overlay.revision, references: entries.map(([reference]) => reference) };
}

const azureCost = resource => Object.fromEntries(Object.entries(resource?.data ?? {})
  .filter(([key]) => key.startsWith(AZURE_COST_PREFIX)));

/** Stop a deploy that would change the live Azure BYOK rate card without a new model binding revision. */
export function requireLiveAzureCostPreserved(live, desired) {
  if (!live || live.data?.[REVISION_KEY] !== desired?.data?.[REVISION_KEY]) return;
  if (!isDeepStrictEqual(azureCost(live), azureCost(desired))) {
    fail(`The rendered ConfigMap/${EVENTS_RUNTIME_CONFIG} would change the live Azure rate card without a new ` +
      'model binding revision. Make a new model bindings file with a new revision.');
  }
}

/**
 * Events reads its runtime ConfigMap as environment variables, which change only when the pod restarts.
 * A digest of the map data in the pod template makes every map change roll out Events.
 */
export function stampEventsRuntimeConfigDigest(resources) {
  const map = findMap(resources, EVENTS_RUNTIME_CONFIG);
  const deployment = resources.find(resource =>
    resource.kind === 'Deployment' && resource.metadata?.name === EVENTS_DEPLOYMENT);
  if (!deployment?.spec?.template) fail(`Kustomize must render deployment/${EVENTS_DEPLOYMENT}.`);
  const sorted = Object.keys(map.data).sort().map(key => [key, map.data[key]]);
  const digest = createHash('sha256').update(JSON.stringify(sorted)).digest('hex');
  deployment.spec.template.metadata ??= {};
  deployment.spec.template.metadata.annotations = {
    ...(deployment.spec.template.metadata.annotations ?? {}),
    [EVENTS_RUNTIME_CONFIG_ANNOTATION]: digest,
  };
  return digest;
}
