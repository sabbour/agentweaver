import { makeStyles } from '@fluentui/react-components';
import { FauxControl } from './primitives';

/** RFP response package: compliance matrix, technical approach, pricing
 *  narrative, and a submission checklist. */
const useStyles = makeStyles({
  root: {
    fontFamily: '"Segoe UI", ui-sans-serif, system-ui, sans-serif',
    backgroundColor: '#fdfbf8',
    color: '#211d1a',
    minWidth: 0,
    padding: '22px 24px 26px',
  },
  masthead: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'flex-start',
    gap: '14px',
    flexWrap: 'wrap',
    paddingBottom: '14px',
    borderBottom: '2px solid #272320',
  },
  org: { fontSize: '12px', fontWeight: 700, letterSpacing: '0.14em', textTransform: 'uppercase', color: '#635c57' },
  title: { margin: '4px 0 0', fontSize: '21px', fontWeight: 800, letterSpacing: '-0.01em' },
  ref: { fontFamily: 'ui-monospace, monospace', fontSize: '12px', color: '#8a827b', textAlign: 'right' },
  section: { marginTop: '20px' },
  sectionTitle: {
    fontSize: '11px',
    fontWeight: 700,
    textTransform: 'uppercase',
    letterSpacing: '0.06em',
    color: '#8a827b',
    marginBottom: '10px',
  },
  lead: { fontSize: '13.5px', lineHeight: 1.6, margin: '0 0 8px', color: '#3f3935' },
  tableWrap: { overflowX: 'auto', borderRadius: '10px', border: '1px solid #e7e1dc' },
  table: { width: '100%', borderCollapse: 'collapse', minWidth: '480px' },
  th: {
    textAlign: 'left',
    padding: '9px 12px',
    fontSize: '11px',
    fontWeight: 700,
    color: '#635c57',
    backgroundColor: '#f6f2ee',
    borderBottom: '1px solid #e7e1dc',
  },
  td: { padding: '9px 12px', fontSize: '12.5px', borderBottom: '1px solid #efeae7', color: '#3f3935', verticalAlign: 'top' },
  reqId: { fontFamily: 'ui-monospace, monospace', fontSize: '11.5px', color: '#8a827b', whiteSpace: 'nowrap' },
  comply: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '5px',
    padding: '2px 9px',
    borderRadius: '9999px',
    fontSize: '11px',
    fontWeight: 700,
    whiteSpace: 'nowrap',
  },
  full: { backgroundColor: '#dcf0e3', color: '#146c37' },
  partial: { backgroundColor: '#fdf1df', color: '#8a4b01' },
  approachGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))',
    gap: '12px',
  },
  phase: { border: '1px solid #e7e1dc', borderRadius: '10px', padding: '13px', backgroundColor: '#ffffff' },
  phaseNo: { fontSize: '11px', fontWeight: 700, color: '#8a827b' },
  phaseName: { fontSize: '13.5px', fontWeight: 700, margin: '2px 0 5px' },
  phaseText: { fontSize: '12px', lineHeight: 1.5, color: '#4a443f' },
  pricingGrid: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1.4fr) minmax(0, 1fr)',
    gap: '16px',
    '@media (max-width: 620px)': { gridTemplateColumns: '1fr' },
  },
  budget: { border: '1px solid #e7e1dc', borderRadius: '10px', padding: '14px', backgroundColor: '#ffffff' },
  budgetRow: { display: 'flex', justifyContent: 'space-between', fontSize: '12.5px', padding: '6px 0', borderBottom: '1px solid #f1ece8' },
  budgetTotal: { display: 'flex', justifyContent: 'space-between', fontSize: '14px', fontWeight: 800, paddingTop: '9px' },
  checklist: { display: 'flex', flexDirection: 'column', gap: '8px' },
  check: { display: 'flex', alignItems: 'flex-start', gap: '9px', fontSize: '12.5px', color: '#3f3935' },
  checkBox: {
    width: '16px',
    height: '16px',
    borderRadius: '4px',
    backgroundColor: '#146c37',
    color: '#ffffff',
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: '11px',
    flexShrink: 0,
    marginTop: '1px',
  },
  checkOpen: { backgroundColor: '#ffffff', border: '1.5px solid #c9c2bb', color: 'transparent' },
  submit: {
    marginTop: '18px',
    display: 'flex',
    gap: '10px',
    alignItems: 'center',
    flexWrap: 'wrap',
  },
  submitBtn: {
    padding: '9px 16px',
    borderRadius: '9px',
    backgroundColor: '#272320',
    color: '#faf6f2',
    fontSize: '12.5px',
    fontWeight: 700,
  },
  submitNote: { fontSize: '11.5px', color: '#8a827b' },
});

export function RfpProposalArtifact() {
  const s = useStyles();
  return (
    <div className={s.root}>
      <div className={s.masthead}>
        <div>
          <div className={s.org}>Northwind Public Sector · Proposal Response</div>
          <h3 className={s.title}>Cloud modernization &amp; managed platform services</h3>
        </div>
        <div className={s.ref}>
          RFP-2026-0342<br />Due 2026-08-14 17:00 ET<br />Response v3 · 42 pp
        </div>
      </div>

      <section className={s.section}>
        <div className={s.sectionTitle}>Compliance matrix</div>
        <div className={s.tableWrap}>
          <table className={s.table}>
            <thead>
              <tr>
                <th className={s.th}>Req</th>
                <th className={s.th}>Requirement</th>
                <th className={s.th}>Status</th>
                <th className={s.th}>Reference</th>
              </tr>
            </thead>
            <tbody>
              {[
                ['3.1.2', 'Data residency within region; encryption at rest & in transit', 'full', '§4.2 Security'],
                ['3.2.0', 'SSO / SCIM with role-based access & audit export', 'full', '§4.4 Identity'],
                ['3.4.7', '99.9% availability with credit-backed SLA', 'full', '§6.1 SLA'],
                ['3.6.1', 'On-prem connector for legacy mainframe feed', 'partial', '§5.3 Integration'],
                ['3.8.3', 'FedRAMP Moderate alignment roadmap', 'partial', '§7.2 Compliance'],
              ].map(([id, req, status, ref]) => (
                <tr key={id}>
                  <td className={`${s.td} ${s.reqId}`}>{id}</td>
                  <td className={s.td}>{req}</td>
                  <td className={s.td}>
                    <span className={`${s.comply} ${status === 'full' ? s.full : s.partial}`}>
                      {status === 'full' ? '✓ Compliant' : '◐ Partial'}
                    </span>
                  </td>
                  <td className={s.td}>{ref}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      <section className={s.section}>
        <div className={s.sectionTitle}>Technical approach</div>
        <div className={s.approachGrid}>
          {[
            ['Phase 1', 'Discovery & landing zone', 'Assess current estate, stand up a compliant landing zone, and define migration waves with rollback gates.'],
            ['Phase 2', 'Iterative migration', 'Move workloads in risk-ordered waves with automated validation and zero-downtime cutover patterns.'],
            ['Phase 3', 'Managed operations', 'Transition to 24/7 managed operations with SLO dashboards, cost governance, and quarterly reviews.'],
          ].map(([no, name, text]) => (
            <div className={s.phase} key={no}>
              <div className={s.phaseNo}>{no}</div>
              <div className={s.phaseName}>{name}</div>
              <div className={s.phaseText}>{text}</div>
            </div>
          ))}
        </div>
      </section>

      <section className={s.section}>
        <div className={s.sectionTitle}>Pricing &amp; budget narrative</div>
        <div className={s.pricingGrid}>
          <p className={s.lead}>
            Pricing is structured as a fixed-fee delivery for Phases 1–2 with a monthly managed-service
            subscription for Phase 3, sized to the 60-workload estate. The model front-loads discovery
            to de-risk the migration and keeps run-rate predictable, with a 10% contingency reserved
            against the partial on-prem connector requirement.
          </p>
          <div className={s.budget}>
            <div className={s.budgetRow}><span>Phase 1 · Discovery</span><span>$180k</span></div>
            <div className={s.budgetRow}><span>Phase 2 · Migration</span><span>$640k</span></div>
            <div className={s.budgetRow}><span>Phase 3 · Managed (12 mo)</span><span>$1.02M</span></div>
            <div className={s.budgetRow}><span>Contingency (10%)</span><span>$184k</span></div>
            <div className={s.budgetTotal}><span>Year-1 total</span><span>$2.02M</span></div>
          </div>
        </div>
      </section>

      <section className={s.section}>
        <div className={s.sectionTitle}>Submission checklist</div>
        <div className={s.checklist}>
          {[
            ['Technical volume (§1–7) complete & page-limited', true],
            ['Compliance matrix signed by solution lead', true],
            ['Pricing volume sealed separately per §2.4', true],
            ['Past-performance references (3) attached', true],
            ['Executive signature & authorized negotiator letter', false],
          ].map(([label, done]) => (
            <div className={s.check} key={label as string}>
              <span className={`${s.checkBox} ${done ? '' : s.checkOpen}`}>{done ? '✓' : ''}</span>
              <span>{label}</span>
            </div>
          ))}
        </div>
        <div className={s.submit}>
          <FauxControl className={s.submitBtn}>Submit response</FauxControl>
          <span className={s.submitNote}>1 checklist item outstanding before submission is enabled.</span>
        </div>
      </section>
    </div>
  );
}
