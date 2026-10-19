/** Validates the fixed P0 system-pool bounds from approved parameters, not a live count. */
export function assertAutoscalerBounds(values) {
  const minCount = values?.nodePoolMinCount?.value;
  const maxCount = values?.nodePoolMaxCount?.value;
  if (minCount !== 2 || maxCount !== 3 || values.nodePoolCount !== undefined) {
    throw new Error('P0 autoscaling parameters must explicitly approve minimum 2 and maximum 3; current count is observed, not a tracked override.');
  }
  return { minCount, maxCount };
}
