// Performance/cost metrics collector (issue #1 expansion, requirement 4).
//
// Reuses the SAME endpoint the product dashboard uses — GET /api/projects/{id}/metrics
// (App Insights model-usage/token data, with a persisted-usage fallback) — rather than
// reinventing token accounting. The summary is persisted in the finding so cost/speed
// regressions are visible over time alongside pass/fail.
//
// Cost note: the API reports spend as `nanoAiu` (nano AI-units); we surface both the
// raw nanoAiu and a human aiu figure (nanoAiu / 1e9) without inventing a $ conversion.

/**
 * Fetch + summarize project metrics. Must be called BEFORE project cleanup.
 * Never throws — returns a `{ available: false }` summary on any error so metrics
 * collection can't fail a scenario.
 * @param {import('./client.mjs').AgentweaverClient} client
 * @param {string} projectId
 * @returns {Promise<object>}
 */
export async function summarizeProjectMetrics(client, projectId) {
  if (!projectId) return { available: false, reason: 'no project id' };

  const res = await client.get(`/api/projects/${projectId}/metrics`);
  if (res.status !== 200 || !res.responseBody) {
    return { available: false, reason: `status ${res.status}` };
  }

  const m = res.responseBody;
  const modelUsage = Array.isArray(m.modelUsage) ? m.modelUsage : [];
  const agentBreakdown = Array.isArray(m.agentBreakdown) ? m.agentBreakdown : [];

  const totalTokens = agentBreakdown.reduce((s, a) => s + (Number(a.totalTokens) || 0), 0);
  const totalNanoAiu =
    modelUsage.reduce((s, u) => s + (Number(u.totalNanoAiu) || 0), 0) ||
    agentBreakdown.reduce((s, a) => s + (Number(a.totalNanoAiu) || 0), 0);
  const totalInvocations = modelUsage.reduce((s, u) => s + (Number(u.invocationCount) || 0), 0);

  const responseDuration = pickPercentiles(m.responseDuration);
  const timeToFirstToken = pickPercentiles(m.timeToFirstToken);

  // Any signal at all? App Insights can lag a just-created run; report presence so a
  // reviewer knows whether zeros mean "cheap" or "not yet ingested".
  const hasData = totalTokens > 0 || totalNanoAiu > 0 || totalInvocations > 0;

  return {
    available: true,
    hasData,
    totalTokens,
    totalNanoAiu,
    totalAiu: totalNanoAiu / 1e9,
    totalInvocations,
    modelUsage: modelUsage.map((u) => ({
      model: u.model,
      invocationCount: u.invocationCount,
      totalNanoAiu: u.totalNanoAiu,
    })),
    agentBreakdown: agentBreakdown.map((a) => ({
      agentName: a.agentName,
      invocationCount: a.invocationCount,
      totalTokens: a.totalTokens,
      totalNanoAiu: a.totalNanoAiu,
    })),
    responseDuration,
    timeToFirstToken,
  };
}

/** Reduce a percentiles array (label/p50Ms/p95Ms) to the overall/first entry. */
function pickPercentiles(arr) {
  if (!Array.isArray(arr) || arr.length === 0) return null;
  const overall = arr.find((p) => /all|overall|total/i.test(p?.label ?? '')) ?? arr[0];
  return { label: overall.label ?? null, p50Ms: overall.p50Ms ?? null, p95Ms: overall.p95Ms ?? null };
}
