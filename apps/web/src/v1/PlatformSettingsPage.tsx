/**
 * Platform settings page at the 0.x `/platform-settings` location. It shows the
 * Gateway's platform component report: every area, which service owns it, its
 * readiness, and the active and available implementations. Statuses are shown
 * exactly as reported. When an owner did not report, the area says so instead
 * of guessing. Only tenant or platform administrators can read the report.
 */
import { Badge, Button, Text } from '@fluentui/react-components';
import { ArrowSyncRegular } from '@fluentui/react-icons';
import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { PageSection } from '../components/ui/PageSection';
import { useAuth } from './AuthContext';
import { GatewayError, gatewayClient } from './api';
import type {
  PlatformComponentAreaReport, PlatformComponentCardinality, PlatformComponentOwner,
  PlatformComponentReadiness, PlatformComponentReason, PlatformComponentReport,
} from './contracts';
import { errorMessage } from './errors';
import { ErrorNotice, Loading, PageHeading } from './pageParts';

const READINESS: Record<PlatformComponentReadiness, { label: string; color: 'success' | 'warning' | 'danger' | 'informative' | 'subtle' }> = {
  ready: { label: 'Ready', color: 'success' },
  degraded: { label: 'Degraded', color: 'warning' },
  notReady: { label: 'Not ready', color: 'danger' },
  disabled: { label: 'Disabled', color: 'subtle' },
  notAvailable: { label: 'Not available', color: 'subtle' },
  unknown: { label: 'Unknown', color: 'informative' },
};

const REASONS: Record<PlatformComponentReason, string> = {
  ownerUnavailable: 'Status not reported. The owning service did not answer.',
  noBackendOwner: 'No service owns this area yet.',
  notSelected: 'No implementation is selected.',
  notImplemented: 'Not built yet.',
  readinessCheckFailed: 'The readiness check failed.',
  readinessTimedOut: 'The readiness check timed out.',
};

const OWNERS: Record<PlatformComponentOwner, string> = {
  eventsAndSessions: 'Events and sessions',
  environment: 'Environment',
  knowledge: 'Knowledge',
  orchestrator: 'Orchestrator',
  identityBroker: 'Identity broker',
  none: 'None',
};

const CARDINALITY: Record<PlatformComponentCardinality, string> = {
  exclusive: 'One',
  orderedComposite: 'Ordered list',
  platformSingleton: 'One for the platform',
  layered: 'Layered',
  keyedByMeterSource: 'One per meter source',
  perApplication: 'One per application',
  perCanvas: 'One per canvas',
};

/** Readable name for an area key such as `networkPolicy`. Unknown keys show as sent. */
function areaLabel(key: string): string {
  const words = key.replace(/([a-z0-9])([A-Z])/g, '$1 $2').trim();
  return words ? words[0].toUpperCase() + words.slice(1).toLowerCase() : key;
}

/** Readiness badge plus reason text. Unknown values from a newer service show as sent. */
function ReadinessState({ readiness, reason }: { readiness: PlatformComponentReadiness; reason: PlatformComponentReason | null }) {
  const state = READINESS[readiness] ?? { label: readiness, color: 'informative' as const };
  // An unreported owner reads "Status not reported", never a made-up status.
  const label = readiness === 'unknown' && reason === 'ownerUnavailable' ? 'Status not reported' : state.label;
  return (
    <span>
      <Badge appearance="tint" color={state.color}>{label}</Badge>
      {reason && <div className="v1-muted">{REASONS[reason] ?? reason}</div>}
    </span>
  );
}

/** One table row per area, in the order the Gateway sends them. */
function AreaTable({ areas }: { areas: PlatformComponentAreaReport[] }) {
  return (
    <div className="v1-table-wrap">
      <table className="v1-table">
        <caption>Platform component areas</caption>
        <thead>
          <tr>
            <th scope="col">Area</th>
            <th scope="col">Owner</th>
            <th scope="col">Status</th>
            <th scope="col">Active</th>
            <th scope="col">Available</th>
          </tr>
        </thead>
        <tbody>
          {areas.map((area) => (
            <tr key={area.area}>
              <th scope="row">
                {areaLabel(area.area)}
                <div className="v1-muted">{area.purpose}</div>
                <div className="v1-muted">{CARDINALITY[area.cardinality] ?? area.cardinality}</div>
              </th>
              <td>{OWNERS[area.owner] ?? area.owner}</td>
              <td><ReadinessState readiness={area.readiness} reason={area.reason} /></td>
              <td>
                {area.active.length === 0 ? <span className="v1-muted">None</span> : (
                  <ul className="v1-implementation-list">
                    {area.active.map((item) => (
                      <li key={`${item.implementationId}/${item.meterSource ?? ''}/${item.order ?? ''}`}>
                        {item.displayName}
                        <span className="v1-muted"> · version {item.version} · {item.hosting}</span>
                        {item.layer && <Badge appearance="outline" size="small">{item.layer}</Badge>}
                        {item.meterSource && <span className="v1-muted"> · {item.meterSource}</span>}
                        <div><ReadinessState readiness={item.readiness} reason={item.reason} /></div>
                      </li>
                    ))}
                  </ul>
                )}
              </td>
              <td>
                {area.available.length === 0 ? <span className="v1-muted">None</span> : (
                  <ul className="v1-implementation-list">
                    {area.available.map((item) => (
                      <li key={item.implementationId}>
                        {item.displayName}
                        <span className="v1-muted"> · version {item.version}{item.enabled ? '' : ' · turned off'}</span>
                      </li>
                    ))}
                  </ul>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** Load state for the platform component report, shared by every page that shows it. */
export interface PlatformComponentReportState {
  report: PlatformComponentReport | null;
  loading: boolean;
  /** Plain message for the last failure, or null. */
  error: string | null;
  /** True when the last failure was a 403, so pages can explain who may read it. */
  forbidden: boolean;
  load: () => Promise<void>;
}

/**
 * Reads the platform component report with an unbound token. The service checks
 * the admin role, so a 403 is expected for other users and is reported as
 * `forbidden` instead of a generic error.
 */
// eslint-disable-next-line react-refresh/only-export-components
export function usePlatformComponentReport(): PlatformComponentReportState {
  const { apiCall } = useAuth();
  const [report, setReport] = useState<PlatformComponentReport | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [forbidden, setForbidden] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    setForbidden(false);
    try {
      setReport(await apiCall((token, tenant) => gatewayClient.getPlatformComponents(token, tenant), null));
    } catch (reason) {
      // Drop the old report so the page shows the failure, not stale statuses.
      setReport(null);
      const denied = reason instanceof GatewayError && reason.status === 403;
      setForbidden(denied);
      setError(denied ? 'Only tenant or platform administrators can view platform components.' : errorMessage(reason));
    } finally {
      setLoading(false);
    }
  }, [apiCall]);

  useEffect(() => {
    // Load on the next tick so state updates stay out of the effect body.
    const timer = window.setTimeout(() => void load(), 0);
    return () => window.clearTimeout(timer);
  }, [load]);

  return { report, loading, error, forbidden, load };
}

/**
 * Platform component areas with their states. Shows the spinner only for the
 * first load, then the error, then the report. `filter` limits the rows, for
 * example to areas that are not ready.
 */
export function PlatformComponentsSection({
  state,
  title = 'Platform components',
  description = 'Each area, the service that owns it, and the implementations it runs or can run.',
  filter,
  emptyText = 'No platform component areas were reported.',
  actions,
}: {
  state: PlatformComponentReportState;
  title?: string;
  description?: string;
  filter?: (area: PlatformComponentAreaReport) => boolean;
  emptyText?: string;
  actions?: ReactNode;
}) {
  const areas = state.report ? (filter ? state.report.areas.filter(filter) : state.report.areas) : [];
  return (
    <PageSection title={title} description={description} actions={actions}>
      {state.loading && !state.report ? <Loading label="Loading platform components" /> : state.error ? <ErrorNotice>{state.error}</ErrorNotice> : state.report && (
        <>
          <Text className="v1-muted">Reported {new Date(state.report.observedAt).toLocaleString()}</Text>
          {areas.length === 0 ? <Text>{emptyText}</Text> : <AreaTable areas={areas} />}
        </>
      )}
    </PageSection>
  );
}

/** Route page for `/platform-settings`. */
export function PlatformSettingsPage() {
  const state = usePlatformComponentReport();
  return (
    <>
      <PageHeading
        title="Platform settings"
        description="Platform components for this deployment and their status."
        breadcrumb={false}
        actions={<Button icon={<ArrowSyncRegular />} disabled={state.loading} onClick={() => void state.load()}>Refresh</Button>}
      />
      <PlatformComponentsSection state={state} />
    </>
  );
}