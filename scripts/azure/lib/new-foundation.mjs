import { assertApprovedKeyVaultName, assertDedicatedResourceGroupOwnership, assertSubscription, assertTenant } from './guardrails.mjs';

export function assertNewFoundation(config, source) {
  if (!/^[a-z][a-z0-9]{5,11}$/.test(config.resourceGroup ?? '') ||
      config.resourceGroup === 'agentweaver') {
    throw new Error('A new foundation requires a separate 6-12 character lowercase resource group and prefix.');
  }
  if (source.scope !== 'infrastructure-only' || source.appRoutingDnsZoneResourceIds?.length !== 0 ||
      config.identityRoutingOnly || config.bootstrapIdentityPostgres || config.bootstrapIdentityRouting ||
      config.identityRoutingCertificatesOnly || config.bootstrapIdentityBrokerState ||
      config.alignExistingIdentityBrokerHostname || config.bootstrapIdentityBrokerRuntime ||
      config.bootstrapFoundationProbeInputs || config.bootstrapFoundationProbePostgres ||
      config.registerBrokerCallback || config.confirmBrokerCallback ||
      config.identityBrokerProviderConfig || config.identityBrokerProviderMode ||
      config.identityBrokerProviderOutput) {
    throw new Error('A new foundation supports only full infrastructure without existing DNS zones or bootstrap options.');
  }
  assertSubscription(config.subscriptionId, config.allowedSubscriptionId);
  assertTenant(config.tenantId, config.allowedTenantId);
  assertApprovedKeyVaultName(config.resourceGroup, source.keyVaultName);
  if (!source.keyVaultName.startsWith(`${config.resourceGroup}-`)) {
    throw new Error('The new foundation Key Vault name must use the selected prefix.');
  }
  if (typeof source.location !== 'string' || typeof source.monitorLocation !== 'string' ||
      source.location.toLowerCase() !== source.monitorLocation.toLowerCase()) {
    throw new Error('The new foundation cluster and Monitor workspace must use the same region.');
  }
}

export function checkNewFoundation(config, source, execAz, expectedIdentity) {
  const { resourceGroup, subscriptionId, tenantId } = config;
  const boundAz = (args, options) => execAz([...args, '--subscription', subscriptionId], options);
  const versionResult = execAz(['version', '-o', 'json'], { check: false });
  let version;
  try { version = JSON.parse(versionResult.stdout)?.['azure-cli']; } catch { /* rejected below */ }
  const match = /^(\d+)\.(\d+)\.(\d+)$/.exec(version ?? '');
  if (versionResult.status !== 0 || !match ||
      Number(match[1]) < 2 || Number(match[1]) === 2 && Number(match[2]) < 75) {
    throw new Error('Azure CLI 2.75.0 or later is required for what-if diagnostics.');
  }
  function read(args, label) {
    const result = boundAz(args, { check: false });
    if (result.status !== 0) throw new Error(`${label} failed: ${result.stderr}`);
    try { return JSON.parse(result.stdout); } catch { throw new Error(`${label} returned malformed JSON.`); }
  }
  const account = read(['account', 'show', '-o', 'json'], 'Account lookup');
  if (account?.id !== subscriptionId || account?.tenantId !== tenantId || account?.state !== 'Enabled' ||
      typeof account?.user?.name !== 'string' || !account.user.name ||
      typeof account?.user?.type !== 'string' || !account.user.type) {
    throw new Error('Actual Azure account identity, tenant or subscription does not match the selected enabled target.');
  }
  const identity = JSON.stringify(account.user);
  if (expectedIdentity !== undefined && identity !== expectedIdentity) {
    throw new Error('Azure account identity changed after preflight.');
  }
  const group = read(['group', 'show', '--name', resourceGroup, '-o', 'json'], 'Resource group lookup');
  const groupId = `/subscriptions/${subscriptionId}/resourceGroups/${resourceGroup}`;
  if (group?.id?.toLowerCase() !== groupId.toLowerCase() || group.name !== resourceGroup) {
    throw new Error('Resource group response is not the exact selected target.');
  }
  assertDedicatedResourceGroupOwnership(resourceGroup, group.tags);
  if (group.tags['agentweaver:owner'] !== source.owner || group.tags['agentweaver:cost-center'] !== source.costCenter) {
    throw new Error('Source parameter ownership differs from the actual target.');
  }
  for (const [args, label] of [
    [['resource', 'list', '--resource-group', resourceGroup, '-o', 'json'], 'Resource inventory'],
    [['deployment', 'group', 'list', '--resource-group', resourceGroup, '-o', 'json'], 'Deployment history'],
    [['monitor', 'log-analytics', 'workspace', 'list-deleted-workspaces', '--resource-group', resourceGroup,
      '-o', 'json'], 'Deleted workspace inventory'],
  ]) {
    const entries = read(args, label);
    if (!Array.isArray(entries) || entries.length !== 0) {
      throw new Error(`${label} is not empty or is incomplete; reconcile the target before deployment.`);
    }
  }
  const nodeResourceGroup = `${resourceGroup}-aks-nodes`;
  const exists = read(['group', 'exists', '--name', nodeResourceGroup, '-o', 'json'], 'AKS managed group lookup');
  if (exists !== false) throw new Error('The AKS managed resource group already exists or could not be verified absent.');
  return { execAz: boundAz, group, identity, nodeResourceGroup };
}

export function checkNewFoundationPlan(result, config) {
  let plan;
  try { plan = JSON.parse(result.stdout); } catch { throw new Error('What-if returned malformed JSON.'); }
  const prefix = `/subscriptions/${config.subscriptionId}/resourceGroups/${config.resourceGroup}/`.toLowerCase();
  const absentOrEmpty = value => value == null || Array.isArray(value) && value.length === 0;
  if (plan?.error || plan?.status !== undefined && plan.status !== 'Succeeded' ||
      !absentOrEmpty(plan?.diagnostics) || !absentOrEmpty(plan?.potentialChanges) ||
      !Array.isArray(plan?.changes) || plan.changes.length === 0 ||
      plan.changes.some(change => change?.changeType !== 'Create' ||
        !absentOrEmpty(change?.diagnostics) || change?.diagnostic != null ||
        change?.unsupportedReason != null || typeof change.resourceId !== 'string' ||
        !change.resourceId.toLowerCase().startsWith(prefix)) ||
      new Set(plan.changes.map(change => change.resourceId.toLowerCase())).size !== plan.changes.length) {
    throw new Error('What-if contains missing, unresolved, non-create or out-of-group changes.');
  }
  const canonical = value => Array.isArray(value) ? value.map(canonical) :
    value && typeof value === 'object'
      ? Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])]))
      : value;
  const changes = plan.changes.map(canonical).sort((a, b) => a.resourceId.localeCompare(b.resourceId));
  return JSON.stringify(changes);
}
