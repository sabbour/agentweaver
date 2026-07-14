/** Evidence-only extension point; deployment-specific, least-privilege collectors are injected. */
export async function collectCrossReference({ runId, traceId, startedAt, endedAt, collectors = [] }) {
  const context = { runId, traceId, startedAt, endedAt };
  const results = await Promise.all(collectors.map(async (collector) => {
    try { return await collector(context); } catch (error) { return { available: false, error: String(error.message ?? error) }; }
  }));
  return { context, results };
}
