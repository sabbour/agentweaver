/**
 * Priority issue and pull request choices come from stored owner records.
 * These reads never call the provider or create a credential grant.
 */
import { Button, Field, Select, Text } from '@fluentui/react-components';
import { useCallback, useEffect, useRef, useState } from 'react';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import type { ProjectIssuePage, ProjectIssueSummary, ProjectPullRequestPage, ProjectPullRequestSummary } from './contracts';
import { errorCode, errorMessage } from './errors';
import { ErrorNotice, Loading } from './pageParts';

type Kind = 'issue' | 'pull_request';
type RecordRow = ProjectIssueSummary | ProjectPullRequestSummary;
type StateFilter = 'open' | 'closed' | 'merged' | 'all';
export type PriorityRepositoryTarget = Pick<RecordRow, 'providerId' | 'repository' | 'number'>;

/** Repository identity is part of the selection, because numbers alone are not globally unique. */
function rowKey(row: RecordRow): string {
  return JSON.stringify([row.providerId, row.repository.owner, row.repository.name, row.number]);
}

/** Builds only the supported provider's canonical URL, never a URL supplied in editable text. */
function providerUrl(kind: Kind, row: RecordRow): string | null {
  if (row.providerId !== 'github') return null;
  return `https://github.com/${encodeURIComponent(row.repository.owner)}/${encodeURIComponent(row.repository.name)}/${kind === 'issue' ? 'issues' : 'pull'}/${row.number}`;
}

interface Props {
  projectId: string;
  kind: Kind;
  busy?: boolean;
  onSelect?: (target: PriorityRepositoryTarget) => void | Promise<void>;
  /** A saved link is resolved again through the same authorized list, not cached metadata. */
  number?: number;
  providerId?: string;
  repository?: { owner: string; name: string };
}

/** Removes all old rows immediately when the project, identity or authority changes. */
export function PrioritySourceControl(props: Props) {
  const { session, authorizationContext } = useAuth();
  const authority = JSON.stringify([props.projectId, props.kind, session?.accessToken, authorizationContext]);
  return <ScopedSourceControl key={authority} {...props} />;
}

function ScopedSourceControl(props: Props) {
  const [filter, setFilter] = useState<StateFilter>(props.number === undefined ? 'open' : 'all');
  return <section aria-label={props.kind === 'issue' ? 'Stored issues' : 'Stored pull requests'}>
    {props.number === undefined && <Field label="State">
      <Select value={filter} onChange={(_, data) => setFilter(data.value as StateFilter)}>
        <option value="open">Open</option>
        <option value="closed">Closed</option>
        {props.kind === 'pull_request' && <option value="merged">Merged</option>}
        <option value="all">All</option>
      </Select>
    </Field>}
    {/* A changed filter starts a new page sequence; old responses cannot enter it. */}
    <SourceControlPage key={filter} {...props} filter={filter} />
  </section>;
}

function SourceControlPage({ projectId, kind, filter, onSelect, busy, number, providerId, repository }: Props & { filter: StateFilter }) {
  const { apiCall } = useAuth();
  const [rows, setRows] = useState<RecordRow[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const [picked, setPicked] = useState('');
  const [coverage, setCoverage] = useState<string | null>(null);
  const [checking, setChecking] = useState(false);
  const checkingRef = useRef(false);
  const active = useRef(false);
  const pending = useRef(false);
  const seenCursors = useRef(new Set<string>());

  const read = useCallback(async (next: string | null) => {
    // A fast double click must not append the same page twice.
    if (pending.current) return;
    pending.current = true;
    setLoading(true);
    setError(null);
    try {
      // Owner lists use the unbound session; writes still go only to the Priority owner.
      const page = await apiCall<ProjectIssuePage | ProjectPullRequestPage>((token, tenant) => kind === 'issue'
        ? gatewayClient.listProjectIssues(token, projectId, { state: filter === 'merged' ? 'all' : filter, cursor: next, pageSize: 25 }, tenant)
        : gatewayClient.listProjectPullRequests(token, projectId, { state: filter, cursor: next, pageSize: 25 }, tenant), null);
      if (!active.current) return;
      // A repeated cursor would make Load more loop forever, so surface a contract failure.
      if (page.nextCursor && seenCursors.current.has(page.nextCursor))
        throw new Error('The service repeated a page. Refresh the list and try again.');
      if (page.nextCursor) seenCursors.current.add(page.nextCursor);
      setRows((previous) => {
        const combined = new Map((next ? previous : []).map((row) => [rowKey(row), row]));
        for (const row of page.items) combined.set(rowKey(row), row);
        return [...combined.values()];
      });
      setCursor(page.nextCursor);
      if ('coverage' in page)
        setCoverage(page.coverage.recordedSince);
    } catch (reason) {
      if (!active.current) return;
      // Do not leave selectable or reopenable metadata after access loss or a failed refresh.
      setRows([]);
      setCursor(null);
      setPicked('');
      setCoverage(null);
      seenCursors.current.clear();
      setError(reason);
    } finally {
      pending.current = false;
      if (active.current) setLoading(false);
    }
  }, [apiCall, projectId, kind, filter]);

  useEffect(() => {
    active.current = true;
    const timer = window.setTimeout(() => { void read(null); }, 0);
    return () => { active.current = false; window.clearTimeout(timer); };
  }, [read]);

  const chosen = rows.find((row) => rowKey(row) === picked);
  const hasIdentity = providerId !== undefined && repository !== undefined;
  const matches = rows.filter((row) => row.number === number && (!hasIdentity ||
    (row.providerId === providerId && row.repository.owner === repository?.owner && row.repository.name === repository?.name)));
  // New links identify one exact repository. Legacy numbers need every page checked.
  const saved = (hasIdentity || !cursor) && matches.length === 1 ? matches[0] : undefined;
  const verifyAndSelect = async () => {
    if (!chosen || checkingRef.current) return;
    checkingRef.current = true;
    setChecking(true);
    setError(null);
    try {
      // Save the exact authorized selection. The owner rechecks project access on write.
      await onSelect?.({ providerId: chosen.providerId, repository: chosen.repository, number: chosen.number });
    } catch (reason) {
      if (!active.current) return;
      // Failed verification must remove every old choice, including protected titles.
      setRows([]);
      setCursor(null);
      setPicked('');
      setCoverage(null);
      setError(reason);
    } finally {
      checkingRef.current = false;
      if (active.current) setChecking(false);
    }
  };
  const label = kind === 'issue' ? 'Issue' : 'Pull request';
  const url = saved && providerUrl(kind, saved);
  return <div className="v1-stack">
    {loading && <Loading label={`Loading ${label.toLowerCase()} records`} />}
    {error != null && <ErrorNotice code={errorCode(error)}>{errorMessage(error)}</ErrorNotice>}
    {coverage && <Text>Stored pull requests since {new Date(coverage).toLocaleString()}. Earlier pull requests are not included.</Text>}
    {number === undefined ? <>
      {rows.length > 0 && <Field label={label}>
        <Select value={chosen ? picked : ''} onChange={(_, data) => setPicked(data.value)} disabled={loading || checking || busy}>
          <option value="">Choose {kind === 'issue' ? 'an issue' : 'a pull request'}</option>
          {rows.map((row) => <option key={rowKey(row)} value={rowKey(row)}>
            {`${row.repository.owner}/${row.repository.name} #${row.number} · ${row.title || label} · ${row.state}`}
          </option>)}
        </Select>
      </Field>}
      {onSelect && <Button appearance="primary" disabled={!chosen || loading || checking || busy}
        onClick={() => void verifyAndSelect()}>{checking ? 'Saving link…' : `Link ${label.toLowerCase()}`}</Button>}
    </> : saved ? <Text>
      {saved.repository.owner}/{saved.repository.name} #{saved.number} · {saved.title || label} · Stored state: {saved.state}{' '}
      {url && <a href={url} target="_blank" rel="noopener noreferrer">Open {label.toLowerCase()}</a>}
    </Text> : !loading && error == null && <Text>{cursor ? 'Load all remaining records to identify this link.'
      : matches.length > 1 ? 'More than one repository has this number. This link cannot identify its target.'
        : 'This link is not in the stored records.'}</Text>}
    {!loading && error == null && rows.length === 0 && number === undefined && <Text>
      {kind === 'issue' ? 'No synced issues match this state.' : 'No stored pull requests match this state.'}
    </Text>}
    {cursor && <Button disabled={loading || checking} onClick={() => void read(cursor)}>Load more {kind === 'issue' ? 'issues' : 'pull requests'}</Button>}
    <Button disabled={loading || checking} onClick={() => {
      // Refresh is a new page sequence, not an append to old results.
      seenCursors.current.clear();
      setRows([]);
      setPicked('');
      void read(null);
    }}>{error != null ? 'Try again' : 'Refresh records'}</Button>
  </div>;
}
