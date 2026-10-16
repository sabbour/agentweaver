export function assertAutoscalerBounds(values) {
  const minCount = values?.nodePoolMinCount?.value;
  const maxCount = values?.nodePoolMaxCount?.value;
  if (minCount !== 2 || maxCount !== 3 || values.nodePoolCount !== undefined) {
    throw new Error('P0 autoscaling parameters must explicitly approve minimum 2 and maximum 3; current count is observed, not a tracked override.');
  }
  return { minCount, maxCount };
}

export function readSystemNodePool(config, execAz, { requireAutoscaling = false } = {}) {
  const result = execAz(['aks', 'nodepool', 'show', '--resource-group', config.resourceGroup,
    '--cluster-name', `${config.resourceGroup}-aks`, '--name', 'system', '-o', 'json'], {
    projectJson: value => ({ id: value.id, name: value.name, mode: value.mode, vmSize: value.vmSize,
      count: value.count, minCount: value.minCount, maxCount: value.maxCount,
      enableAutoScaling: value.enableAutoScaling, provisioningState: value.provisioningState }),
    preserveProjectedJson: true,
  });
  if (result.status !== 0) throw new Error('Exact system node pool read failed.');
  let pool;
  try { pool = JSON.parse(result.stdout); }
  catch { throw new Error('System node pool read returned malformed JSON.'); }
  const id = `/subscriptions/${config.subscriptionId}/resourceGroups/${config.resourceGroup}/providers/Microsoft.ContainerService/managedClusters/${config.resourceGroup}-aks/agentPools/system`;
  if (pool?.id?.toLowerCase() !== id.toLowerCase() || pool.name !== 'system' || pool.mode !== 'System' ||
      pool.vmSize !== 'Standard_D2s_v5' || pool.provisioningState !== 'Succeeded' ||
      !Number.isInteger(pool.count) || pool.count < config.nodePoolMinCount || pool.count > config.nodePoolMaxCount) {
    throw new Error('Existing system pool must match the approved target, SKU, successful state and 2-3 count; refusing a resize or replacement.');
  }
  if (requireAutoscaling && (pool.enableAutoScaling !== true ||
      pool.minCount !== config.nodePoolMinCount || pool.maxCount !== config.nodePoolMaxCount)) {
    throw new Error('Native system pool did not confirm the approved autoscaler bounds.');
  }
  return pool;
}
