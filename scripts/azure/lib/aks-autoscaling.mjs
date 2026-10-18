/** Reads and validates the fixed P0 system node-pool autoscaling contract from Azure. */
export function assertAutoscalerBounds(values) {
  const minCount = values?.nodePoolMinCount?.value;
  const maxCount = values?.nodePoolMaxCount?.value;
  if (minCount !== 2 || maxCount !== 3 || values.nodePoolCount !== undefined) {
    throw new Error('P0 autoscaling parameters must explicitly approve minimum 2 and maximum 3; current count is observed, not a tracked override.');
  }
  return { minCount, maxCount };
}

/** Reads the current count used as the AKS-only deployment input. */
export function readSystemNodePool(config, execAz) {
  const result = execAz(['aks', 'nodepool', 'show', '--resource-group', config.resourceGroup,
    '--cluster-name', `${config.resourceGroup}-aks`, '--name', 'system', '-o', 'json'], {
    projectJson: value => ({ count: value.count }),
    preserveProjectedJson: true,
  });
  if (result.status !== 0) throw new Error('Exact system node pool read failed.');
  let pool;
  try { pool = JSON.parse(result.stdout); }
  catch { throw new Error('System node pool read returned malformed JSON.'); }
  if (!pool || typeof pool !== 'object' || !Number.isInteger(pool.count)) {
    throw new Error('System node pool read did not provide the count needed for deployment.');
  }
  return pool;
}
