/** Returns the fixed container repository name for a component that produces an image. */
export function componentImageRepository(componentId) {
  if (componentId === 'Agentweaver.FoundationProbe') return 'agentweaver-foundation-probe';
  if (componentId === 'Agentweaver.Web') return 'agentweaver-web';
  if (componentId === 'Agentweaver.AgentHost') return 'agentweaver-agenthost';
  return componentId.toLowerCase();
}

/** Builds the legacy version-and-commit tag used by historical P1 evidence. */
export function componentImageTag(version, sourceSha) {
  return `${version.replaceAll('+', '_')}-${sourceSha}`;
}
