/**
 * An independently supplied scenario scope prevents untrusted gate text from
 * turning a syntactically valid judge approval into execution outside the persona's
 * intended action set. This is a safety interlock, not a quality judgment.
 */
export function enforceInScopeApproval(decision, gate, expectedScope = {}) {
  if (decision?.decision !== 'approve') return { decision, downgraded: false };
  const allowedTools = new Set(expectedScope.allowedToolNames ?? []);
  const toolName = gate?.toolName ?? gate?.action ?? null;
  if (toolName && allowedTools.has(toolName)) return { decision, downgraded: false };
  return {
    downgraded: true,
    decision: {
      ...decision,
      decision: 'defer',
      scope: 'once',
      reason: `${decision?.reason ?? 'approval'}; deferred because gate "${toolName ?? 'unknown'}" is outside the independently supplied scenario scope`,
    },
  };
}
