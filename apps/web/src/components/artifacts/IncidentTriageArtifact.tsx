import { makeStyles } from '@fluentui/react-components';
import { FauxControl } from './primitives';

/** Incident triage draft: an evidence-linked timeline plus remediation options
 *  presented for human review. Nothing is applied automatically. */
const useStyles = makeStyles({
  root: {
    fontFamily: '"Segoe UI", ui-sans-serif, system-ui, sans-serif',
    backgroundColor: '#fdfbf8',
    color: '#211d1a',
    minWidth: 0,
    padding: '20px 22px 24px',
  },
  head: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
  },
  idRow: { display: 'flex', alignItems: 'center', gap: '10px', flexWrap: 'wrap' },
  sev: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    padding: '3px 11px',
    borderRadius: '9999px',
    backgroundColor: '#fbe4e9',
    color: '#8a1f3f',
    fontWeight: 700,
    fontSize: '12px',
  },
  incId: { fontFamily: 'ui-monospace, "Cascadia Code", monospace', fontSize: '13px', color: '#635c57' },
  title: { margin: '10px 0 0', fontSize: '19px', fontWeight: 700, letterSpacing: '-0.01em' },
  draftBadge: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    padding: '4px 11px',
    borderRadius: '8px',
    backgroundColor: '#fdf1df',
    color: '#8a4b01',
    fontWeight: 700,
    fontSize: '12px',
    border: '1px solid #f0d9b0',
  },
  grid: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr)',
    gap: '18px',
    marginTop: '18px',
    '@media (max-width: 640px)': { gridTemplateColumns: '1fr' },
  },
  panel: {
    border: '1px solid #e7e1dc',
    borderRadius: '12px',
    padding: '16px',
    backgroundColor: '#ffffff',
  },
  panelTitle: {
    fontSize: '11px',
    fontWeight: 700,
    textTransform: 'uppercase',
    letterSpacing: '0.06em',
    color: '#8a827b',
    marginBottom: '12px',
  },
  summaryText: { fontSize: '13.5px', lineHeight: 1.6, margin: '0 0 12px' },
  kv: { display: 'flex', flexDirection: 'column', gap: '7px' },
  kvRow: { display: 'flex', gap: '8px', fontSize: '12.5px' },
  kvKey: { color: '#8a827b', minWidth: '96px', flexShrink: 0 },
  kvVal: { color: '#3f3935', fontWeight: 600 },
  timeline: { display: 'flex', flexDirection: 'column', gap: '0' },
  event: {
    display: 'grid',
    gridTemplateColumns: '18px 1fr',
    gap: '10px',
  },
  railCol: { display: 'flex', flexDirection: 'column', alignItems: 'center' },
  dot: {
    width: '11px',
    height: '11px',
    borderRadius: '50%',
    backgroundColor: '#8a827b',
    marginTop: '3px',
    flexShrink: 0,
  },
  dotAlert: { backgroundColor: '#a62147' },
  dotAmber: { backgroundColor: '#8a4b01' },
  connector: { width: '2px', flex: 1, backgroundColor: '#e7e1dc', minHeight: '14px' },
  eventBody: { paddingBottom: '16px' },
  eventTime: { fontFamily: 'ui-monospace, monospace', fontSize: '11.5px', color: '#8a827b' },
  eventText: { fontSize: '13px', lineHeight: 1.5, margin: '2px 0 6px' },
  evidence: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '5px',
    padding: '2px 8px',
    borderRadius: '6px',
    backgroundColor: '#f1ece8',
    color: '#4a443f',
    fontSize: '11px',
    fontWeight: 600,
    fontFamily: 'ui-monospace, monospace',
    marginRight: '6px',
  },
  remediation: { marginTop: '18px' },
  optionGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))',
    gap: '12px',
    marginTop: '12px',
  },
  option: {
    border: '1px solid #e7e1dc',
    borderRadius: '12px',
    padding: '15px',
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    backgroundColor: '#ffffff',
  },
  optionRec: { border: '1px solid #c9d9cd', backgroundColor: '#f4faf6' },
  optionHead: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '8px' },
  optionName: { fontSize: '14px', fontWeight: 700 },
  recTag: {
    fontSize: '10.5px',
    fontWeight: 700,
    color: '#146c37',
    backgroundColor: '#dcf0e3',
    padding: '2px 8px',
    borderRadius: '9999px',
  },
  optionText: { fontSize: '12.5px', lineHeight: 1.5, color: '#4a443f' },
  optionMeta: { display: 'flex', gap: '12px', fontSize: '11.5px', color: '#8a827b', flexWrap: 'wrap' },
  approve: {
    marginTop: '4px',
    padding: '8px 12px',
    borderRadius: '8px',
    border: '1px solid #d8d2cc',
    textAlign: 'center',
    fontSize: '12px',
    fontWeight: 700,
    color: '#3f3935',
  },
  banner: {
    marginTop: '18px',
    padding: '12px 14px',
    borderRadius: '10px',
    backgroundColor: '#fdf1df',
    border: '1px solid #f0d9b0',
    color: '#7a4300',
    fontSize: '12.5px',
    fontWeight: 600,
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
  },
});

export function IncidentTriageArtifact() {
  const s = useStyles();
  return (
    <div className={s.root}>
      <div className={s.head}>
        <div>
          <div className={s.idRow}>
            <span className={s.sev}>● SEV-2</span>
            <span className={s.incId}>INC-4471</span>
          </div>
          <h3 className={s.title}>Checkout latency spike &amp; elevated 5xx on payments-api</h3>
        </div>
        <span className={s.draftBadge}>⚑ Triage draft — for human review</span>
      </div>

      <div className={s.grid}>
        <section className={s.panel}>
          <div className={s.panelTitle}>Triage summary</div>
          <p className={s.summaryText}>
            p95 checkout latency rose from 240ms to 5.8s at 14:32 UTC, with 5xx climbing to
            7.4%. The correlation points to connection-pool exhaustion on <code>payments-api</code>{' '}
            shortly after a config rollout reduced the pool ceiling. Customer-facing impact is
            partial: card payments degraded, wallet payments unaffected.
          </p>
          <div className={s.kv}>
            <div className={s.kvRow}><span className={s.kvKey}>Detected</span><span className={s.kvVal}>14:34 UTC · latency SLO burn alert</span></div>
            <div className={s.kvRow}><span className={s.kvKey}>Blast radius</span><span className={s.kvVal}>Card checkout · ~18% of sessions</span></div>
            <div className={s.kvRow}><span className={s.kvKey}>Suspected cause</span><span className={s.kvVal}>Pool ceiling change in cfg #8821</span></div>
            <div className={s.kvRow}><span className={s.kvKey}>Confidence</span><span className={s.kvVal}>Medium-high (evidence-linked)</span></div>
          </div>
        </section>

        <section className={s.panel}>
          <div className={s.panelTitle}>Evidence-linked timeline</div>
          <div className={s.timeline}>
            {[
              { t: '14:30', text: 'Config rollout cfg#8821 sets DB pool max 20 → 6.', kind: 'amber', ev: 'deploy/cfg-8821' },
              { t: '14:32', text: 'payments-api pool saturation; wait-queue depth > 200.', kind: 'alert', ev: 'metric/pool_wait' },
              { t: '14:34', text: 'Latency SLO burn alert fires; 5xx at 7.4%.', kind: 'alert', ev: 'alert/slo-burn' },
              { t: '14:41', text: 'Wallet path unaffected — isolates blast radius to card.', kind: 'plain', ev: 'trace/wallet-ok' },
            ].map((e, i, arr) => (
              <div className={s.event} key={e.t}>
                <div className={s.railCol}>
                  <span
                    className={`${s.dot} ${e.kind === 'alert' ? s.dotAlert : e.kind === 'amber' ? s.dotAmber : ''}`}
                  />
                  {i < arr.length - 1 && <span className={s.connector} />}
                </div>
                <div className={s.eventBody}>
                  <div className={s.eventTime}>{e.t} UTC</div>
                  <p className={s.eventText}>{e.text}</p>
                  <span className={s.evidence}>🔗 {e.ev}</span>
                </div>
              </div>
            ))}
          </div>
        </section>
      </div>

      <section className={s.remediation}>
        <div className={s.panelTitle} style={{ marginTop: '4px' }}>Remediation options — choose after review</div>
        <div className={s.optionGrid}>
          <article className={`${s.option} ${s.optionRec}`}>
            <div className={s.optionHead}>
              <span className={s.optionName}>Roll back cfg#8821</span>
              <span className={s.recTag}>Suggested</span>
            </div>
            <p className={s.optionText}>Restore pool ceiling to 20. Directly addresses the suspected cause; fully reversible.</p>
            <div className={s.optionMeta}><span>Est. recovery ~3 min</span><span>Risk: low</span></div>
            <FauxControl className={s.approve}>Approve rollback</FauxControl>
          </article>
          <article className={s.option}>
            <div className={s.optionHead}><span className={s.optionName}>Scale out replicas</span></div>
            <p className={s.optionText}>Add 4 payments-api replicas to absorb queue depth while the config is reviewed.</p>
            <div className={s.optionMeta}><span>Est. recovery ~6 min</span><span>Risk: medium</span></div>
            <FauxControl className={s.approve}>Approve scale-out</FauxControl>
          </article>
          <article className={s.option}>
            <div className={s.optionHead}><span className={s.optionName}>Shed card traffic</span></div>
            <p className={s.optionText}>Temporarily route card checkout to the wallet fallback to protect the SLO.</p>
            <div className={s.optionMeta}><span>Est. recovery ~2 min</span><span>Risk: customer-visible</span></div>
            <FauxControl className={s.approve}>Approve traffic shed</FauxControl>
          </article>
        </div>
      </section>

      <div className={s.banner}>
        🔒 No changes have been applied. Every remediation above requires explicit human approval before it runs.
      </div>
    </div>
  );
}
