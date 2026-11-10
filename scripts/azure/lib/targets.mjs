/**
 * Tracked registry of the dedicated v1 install targets.
 * The target name is the resource group name and the Bicep namePrefix.
 */
export const INSTALL_TARGETS = Object.freeze({
  agentweaver: Object.freeze({
    foundationParameters: 'infra/bicep/parameters/p0-integration.approved.json',
    aksParameters: 'infra/bicep/parameters/p0-aks-redeploy.approved.json',
  }),
  awwus2np01: Object.freeze({
    foundationParameters: 'infra/bicep/parameters/awwus2np01.approved.json',
    aksParameters: 'infra/bicep/parameters/awwus2np01-aks-redeploy.approved.json',
  }),
});

/** Returns the registered target for a resource group name, or undefined. */
export function installTarget(name) {
  return typeof name === 'string' && Object.hasOwn(INSTALL_TARGETS, name) ? INSTALL_TARGETS[name] : undefined;
}
