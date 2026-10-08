import '@xyflow/react/dist/style.css';
import { ReactFlow, Background, Controls, MiniMap } from '@xyflow/react';
import '../components/shell/shell.css';
import './styles.css';
import { FluentProvider, Button, Badge, Field, Input, Textarea, Spinner, TabList, Tab, MessageBar, MessageBarBody } from '@fluentui/react-components';
import {
  Apps24Regular,
  BookToolbox24Regular,
  Brain24Regular,
  Chat24Regular,
  ChevronRightRegular,
  Dismiss24Regular,
  Flowchart24Regular,
  Home24Regular,
  Settings24Regular,
} from '@fluentui/react-icons';
import { ApprovalGate } from '../components/ui/agentic';
import { agentweaverLightTheme } from '../theme';
import {
  BrowserRouter,
  Link,
  Navigate,
  Route,
  Routes,
  useLocation,
  useNavigate,
  useParams,
  useSearchParams,
} from 'react-router-dom';
import type { FormEvent, ReactNode } from 'react';
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { AuthProvider, useAuth } from './AuthContext';
import { gatewayClient, GatewayError } from './api';
import type {
  CoordinatorDecisionStateView,
  EffectiveRunSelection,
  KnowledgeRecord,
  ProjectConfiguration,
  ProjectSummary,
  SessionEventEnvelope,
  SessionStatusBlocker,
  SessionStatusSnapshot,
  SessionTreeNode,
  SessionTreeSnapshot,
  UsageRunTotals,
  VersionedProjectConfiguration,
  OwnerRunStatus,
} from './contracts';
import type { RunBinding } from './authProtocol';
import { useRunJournal } from './useRunJournal';

function errorMessage(error: unknown): string {
  if (error instanceof GatewayError) {
    const code = error.code ? ` (${error.code})` : '';
    return `HTTP ${error.status}${code}: ${error.message}`;
  }
  return error instanceof Error ? error.message : 'The request failed unexpectedly.';
}

function errorCode(error: unknown): string | undefined {
  return error instanceof GatewayError ? error.code : undefined;
}

function isSameBinding(
  actual: RunBinding | null | undefined,
  expected: RunBinding | null,
): boolean {
  return actual?.projectId === expected?.projectId && actual?.runId === expected?.runId;
}

function ErrorNotice({ children, code }: { children: ReactNode; code?: string }) {
  return (
    <MessageBar intent="error" role="alert">
      <MessageBarBody>
        {code && <strong>{code}: </strong>}
        {children}
      </MessageBarBody>
    </MessageBar>
  );
}

function Loading({ label = 'Loading from the Gateway…' }: { label?: string }) {
  return <div className="v1-loading"><Spinner label={label} /></div>;
}

function ConsentScreen() {
  const { consent, busy, error, decideConsent } = useAuth();
  if (!consent) return null;
  return (
    <main className="v1-auth-page">
      <section className="v1-auth-card">
        <div className="v1-brand"><img src="/agentweaver.png" alt="" />Agentweaver</div>
        <h1>Review access</h1>
        <p>The Identity Broker is asking you to authorize this registered client.</p>
        <dl className="v1-detail-list">
          <div><dt>Client</dt><dd>{consent.client_id}</dd></div>
          <div><dt>Requested permissions</dt><dd>{consent.requested_scopes.join(', ')}</dd></div>
        </dl>
        <p className="v1-muted">For a run-bound request, the Broker checks the active project/run grant before issuing a token.</p>
        {error && <ErrorNotice>{error}</ErrorNotice>}
        <div className="v1-actions">
          <Button appearance="primary" disabled={busy} onClick={() => void decideConsent(true)}>
            {busy ? 'Processing…' : 'Approve'}
          </Button>
          <Button appearance="secondary" disabled={busy} onClick={() => void decideConsent(false)}>
            Decline
          </Button>
        </div>
      </section>
    </main>
  );
}

function SignInScreen() {
  const { busy, error, configurationError, authorize } = useAuth();
  return (
    <main className="v1-auth-page">
      <section className="v1-auth-card">
        <div className="v1-brand"><img src="/agentweaver.png" alt="" />Agentweaver</div>
        <h1>Sign in</h1>
        <p>Sign in through the Identity Broker to use the Gateway-backed project and run views.</p>
        {configurationError && <ErrorNotice>{configurationError}</ErrorNotice>}
        {error && <ErrorNotice>{error}</ErrorNotice>}
        <Button
          appearance="primary"
          disabled={busy || Boolean(configurationError)}
          onClick={() => void authorize(null)}
        >
          {busy ? 'Waiting for Identity Broker…' : 'Continue with Identity Broker'}
        </Button>
        <p className="v1-muted">
          Access tokens stay in memory only. After a reload, continue sign-in again; the Broker
          recovers its local session and rechecks current grants and project roles.
        </p>
      </section>
    </main>
  );
}

function Shell({ children }: { children: ReactNode }) {
  const location = useLocation();
  const navigate = useNavigate();
  const { session, signOut } = useAuth();
  const projectMatch = location.pathname.match(/^\/projects\/([^/]+)/);
  const projectId = projectMatch ? decodeURIComponent(projectMatch[1]) : undefined;
  const selected = (path: string) =>
    location.pathname === path || location.pathname.startsWith(`${path}/`);

  return (
    <div className="aw-app-shell">
      <nav className="aw-left-nav" aria-label="Primary navigation">
        <div className="aw-rail-chrome">
          <Link to="/projects" aria-label="Agentweaver home" className="aw-rail-brand">
            <img src="/agentweaver.png" alt="" className="aw-rail-brand__icon" />
            <span className="aw-rail-brand__label">Agentweaver</span>
          </Link>
          <Button appearance="subtle" icon={<Dismiss24Regular />} aria-label="Clear browser session" onClick={signOut} />
        </div>
        <div className="aw-rail-header">
          <Badge appearance="tint" color="informative" className="v1-session-badge">
            Broker session in memory
          </Badge>
        </div>
        <div className="aw-rail-scroll">
          <div className="aw-nav-section" role="group" aria-label="Workspace">
            <div className="aw-nav-section__heading">Workspace</div>
            <Link to="/projects" className={`aw-nav-item${selected('/projects') && !projectId ? ' aw-nav-item--selected' : ''}`}>
              <span className="aw-nav-item__icon"><Apps24Regular /></span>
              <span className="aw-nav-item__label">Projects</span>
            </Link>
          </div>
          {projectId && (
            <>
              <hr className="aw-nav-divider" />
              <div className="aw-nav-section" role="group" aria-label="Project">
                <div className="aw-nav-section__heading">Project</div>
                <Link to={`/projects/${encodeURIComponent(projectId)}`} className={`aw-nav-item${location.pathname === `/projects/${projectId}` ? ' aw-nav-item--selected' : ''}`}>
                  <span className="aw-nav-item__icon"><Home24Regular /></span>
                  <span className="aw-nav-item__label">Overview</span>
                </Link>
                <Link to={`/projects/${encodeURIComponent(projectId)}/settings`} className={`aw-nav-item${selected(`/projects/${projectId}/settings`) ? ' aw-nav-item--selected' : ''}`}>
                  <span className="aw-nav-item__icon"><Settings24Regular /></span>
                  <span className="aw-nav-item__label">Configuration</span>
                </Link>
                <Link to={`/projects/${encodeURIComponent(projectId)}/knowledge`} className={`aw-nav-item${selected(`/projects/${projectId}/knowledge`) ? ' aw-nav-item--selected' : ''}`}>
                  <span className="aw-nav-item__icon"><Brain24Regular /></span>
                  <span className="aw-nav-item__label">Knowledge</span>
                </Link>
              </div>
            </>
          )}
          <hr className="aw-nav-divider" />
          <div className="aw-nav-section" role="group" aria-label="Account">
            <div className="aw-nav-section__heading">Account</div>
            <button className="aw-nav-item v1-nav-button" onClick={() => navigate('/projects')}>
              <span className="aw-nav-item__icon"><Chat24Regular /></span>
              <span className="aw-nav-item__label">{session?.binding ? 'Run chat' : 'Run chat opens from a run'}</span>
            </button>
          </div>
        </div>
        <div className="aw-rail-footer"><span className="v1-muted">Agentweaver 1.0 · Gateway</span></div>
      </nav>
      <div className="aw-shell-canvas">
        <main className="aw-shell-content" aria-label="Main content">
          <div className="aw-shell-scroll">
            <div className="v1-content">
              <div className="v1-topbar">
                <div className="v1-breadcrumb">
                  <Link to="/projects">Projects</Link>
                  {projectId && <><ChevronRightRegular /><span>{projectId}</span></>}
                </div>
                <Button appearance="subtle" onClick={signOut}>Clear session</Button>
              </div>
              {children}
            </div>
          </div>
        </main>
      </div>
    </div>
  );
}

function PageHeading({ title, description, actions }: { title: string; description?: string; actions?: ReactNode }) {
  return (
    <header className="v1-page-heading">
      <div><h1>{title}</h1>{description && <p>{description}</p>}</div>
      {actions && <div className="v1-actions">{actions}</div>}
    </header>
  );
}

function Panel({ title, children, actions }: { title: string; children: ReactNode; actions?: ReactNode }) {
  return (
    <section className="v1-panel">
      <div className="v1-panel-heading"><h2>{title}</h2>{actions}</div>
      {children}
    </section>
  );
}

function ProjectsPage() {
  const { session, apiCall, authorize } = useAuth();
  const [projects, setProjects] = useState<ProjectSummary[]>([]);
  const [projectName, setProjectName] = useState('');
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      setProjects(await apiCall((token) => gatewayClient.listProjects(token), null));
      setError(null);
    } catch (reason) {
      setError(reason);
    } finally {
      setLoading(false);
    }
  }, [apiCall]);

  useEffect(() => {
    const timer = window.setTimeout(() => { void load(); }, 0);
    return () => window.clearTimeout(timer);
  }, [load]);

  const create = async (event: FormEvent) => {
    event.preventDefault();
    if (!projectName.trim()) return;
    setBusy(true);
    setError(null);
    try {
      const created = await apiCall((token) => gatewayClient.createProject(token, projectName.trim()), null);
      setProjectName('');
      setNotice(`Project ${created.projectId} was created. The source contract does not auto-assign an Owner; project access remains governed by Projects & Config.`);
      await load();
    } catch (reason) {
      setError(reason);
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <PageHeading title="Projects" description="Projects & Config owns project access, configuration revisions, and immutable accepted run selections." />
      {error != null && <ErrorNotice code={errorCode(error)}>{errorMessage(error)}</ErrorNotice>}
      {notice && <MessageBar intent="info"><MessageBarBody>{notice}</MessageBarBody></MessageBar>}
      <div className="v1-columns">
        <Panel title="Visible projects" actions={<Button appearance="subtle" onClick={() => void load()}>Refresh</Button>}>
          {loading ? <Loading /> : projects.length === 0 ? (
            <p className="v1-muted">No projects are visible to the current identity and role assignments.</p>
          ) : (
            <div className="v1-project-list">
              {projects.map((project) => (
                <article className="v1-project-row" key={project.projectId}>
                  <div>
                    <Link to={`/projects/${encodeURIComponent(project.projectId)}`} className="v1-project-name">{project.name}</Link>
                    <div className="v1-muted">{project.projectId} · revision {project.revision} · configuration {project.configurationRevision}</div>
                  </div>
                  <Badge appearance="tint" color={project.state === 'active' ? 'success' : 'subtle'}>{project.state}</Badge>
                </article>
              ))}
            </div>
          )}
        </Panel>
        <Panel title="Create a project">
          <form className="v1-form" onSubmit={(event) => void create(event)}>
            <Field label="Project name">
              <Input value={projectName} onChange={(_, data) => setProjectName(data.value)} maxLength={120} />
            </Field>
            <Button appearance="primary" type="submit" disabled={busy || !projectName.trim()}>
              {busy ? 'Creating…' : 'Create project'}
            </Button>
            <p className="v1-muted">Project creation does not assign an Owner. Ask a tenant administrator to grant current access if the project is not visible afterward.</p>
          </form>
        </Panel>
      </div>
      {!session && <Button onClick={() => void authorize(null)}>Sign in again</Button>}
    </>
  );
}

function ProjectOverviewPage() {
  const { projectId = '' } = useParams();
  const { session, apiCall } = useAuth();
  const [project, setProject] = useState<ProjectSummary | null>(null);
  const [runId, setRunId] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const [resultScope, setResultScope] = useState<string | null>(null);
  const [projectScope, setProjectScope] = useState<string | null>(null);
  const [errorScope, setErrorScope] = useState<string | null>(null);
  const requestScope = JSON.stringify([session?.accessToken ?? null, projectId]);

  useEffect(() => {
    let active = true;
    const scope = requestScope;
    const timer = window.setTimeout(() => {
      if (!active) return;
      setLoading(true);
      setError(null);
      setErrorScope(null);
      void apiCall((token) => gatewayClient.getProject(token, projectId), null)
        .then((result) => {
          if (!active) return;
          if (result.projectId !== projectId)
            throw new Error('Projects & Config returned a project outside this exact route.');
          setProject(result);
          setProjectScope(scope);
          setResultScope(scope);
        })
        .catch((reason: unknown) => {
          if (!active) return;
          setError(reason);
          setErrorScope(scope);
          setResultScope(scope);
        })
        .finally(() => { if (active) setLoading(false); });
    }, 0);
    return () => {
      active = false;
      window.clearTimeout(timer);
    };
  }, [apiCall, projectId, requestScope]);

  const currentProject = projectScope === requestScope ? project : null;
  const currentError = errorScope === requestScope ? error : null;
  const currentLoading = loading || resultScope !== requestScope;

  return (
    <>
      <PageHeading title={currentLoading ? 'Project' : currentProject?.name ?? 'Project'} description={`Project ID: ${projectId}`} />
      {currentError != null && <ErrorNotice code={errorCode(currentError)}>{errorMessage(currentError)}</ErrorNotice>}
      {currentLoading ? <Loading /> : currentProject && (
        <div className="v1-columns">
          <Panel title="Project state">
            <dl className="v1-detail-list">
              <div><dt>Lifecycle</dt><dd>{currentProject.state}</dd></div>
              <div><dt>Revision</dt><dd>{currentProject.revision}</dd></div>
              <div><dt>Configuration revision</dt><dd>{currentProject.configurationRevision}</dd></div>
            </dl>
            <div className="v1-actions v1-project-actions">
              <Link className="v1-link-button" to={`/projects/${encodeURIComponent(projectId)}/settings`}><Settings24Regular /> Configuration</Link>
              <Link className="v1-link-button" to={`/projects/${encodeURIComponent(projectId)}/knowledge`}><BookToolbox24Regular /> Knowledge</Link>
            </div>
          </Panel>
          <Panel title="Open an existing run">
            <form className="v1-form" onSubmit={(event) => { event.preventDefault(); }}>
              <Field label="Exact run ID">
                <Input value={runId} onChange={(_, data) => setRunId(data.value)} />
              </Field>
              <Button
                appearance="primary"
                disabled={!runId.trim()}
                onClick={() => {
                  if (runId.trim()) window.location.assign(`/projects/${encodeURIComponent(projectId)}/runs/${encodeURIComponent(runId.trim())}`);
                }}
              >
                Open run
              </Button>
              <p className="v1-muted">
                Run creation and scheduling are not exposed by this Gateway contract. Enter an existing run ID;
                the page reads owner snapshots, committed journal events, and usage only.
              </p>
            </form>
          </Panel>
        </div>
      )}
    </>
  );
}

function ProjectConfigurationPage() {
  const { projectId = '' } = useParams();
  const { session, apiCall } = useAuth();
  const [versioned, setVersioned] = useState<VersionedProjectConfiguration | null>(null);
  const [editor, setEditor] = useState('');
  const [loading, setLoading] = useState(true);
  const [savingScope, setSavingScope] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [errorScope, setErrorScope] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [noticeScope, setNoticeScope] = useState<string | null>(null);
  const [loadedScope, setLoadedScope] = useState<string | null>(null);
  const requestScope = JSON.stringify([session?.accessToken ?? null, projectId]);
  const activeScope = useRef(requestScope);
  const loadGeneration = useRef(0);

  const load = useCallback(async () => {
    const scope = requestScope;
    const generation = ++loadGeneration.current;
    setLoading(true);
    setLoadedScope(null);
    setError(null);
    setErrorScope(null);
    setNotice(null);
    setNoticeScope(null);
    try {
      const result = await apiCall((token) => gatewayClient.getProjectConfiguration(token, projectId), null);
      if (activeScope.current !== scope || loadGeneration.current !== generation) return;
      if (result.projectId !== projectId)
        throw new Error('Projects & Config returned configuration for a different project.');
      setVersioned(result);
      setEditor(JSON.stringify(result.configuration, null, 2));
      setLoadedScope(scope);
    } catch (reason) {
      if (activeScope.current !== scope || loadGeneration.current !== generation) return;
      setError(reason);
      setErrorScope(scope);
      setVersioned(null);
      setLoadedScope(scope);
    } finally {
      if (activeScope.current === scope && loadGeneration.current === generation)
        setLoading(false);
    }
  }, [apiCall, projectId, requestScope]);

  useEffect(() => {
    activeScope.current = requestScope;
    loadGeneration.current += 1;
    const timer = window.setTimeout(() => { void load(); }, 0);
    return () => {
      window.clearTimeout(timer);
      loadGeneration.current += 1;
    };
  }, [load, requestScope]);

  const save = async (event: FormEvent) => {
    event.preventDefault();
    const scope = requestScope;
    if (!versioned || loadedScope !== scope || versioned.projectId !== projectId) return;
    let configuration: ProjectConfiguration;
    try {
      const parsed: unknown = JSON.parse(editor);
      if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed))
        throw new Error('Configuration must be a JSON object.');
      configuration = parsed as ProjectConfiguration;
    } catch (reason) {
      setError(reason);
      setErrorScope(scope);
      return;
    }
    setSavingScope(scope);
    setError(null);
    setErrorScope(null);
    setNotice(null);
    setNoticeScope(null);
    try {
      const updated = await apiCall(
        (token) => gatewayClient.updateProjectConfiguration(token, projectId, versioned.revision, configuration),
        null,
      );
      if (activeScope.current !== scope) return;
      if (updated.projectId !== projectId)
        throw new Error('Projects & Config returned an update for a different project.');
      setVersioned(updated);
      setEditor(JSON.stringify(updated.configuration, null, 2));
      setNotice(`Configuration revision ${updated.revision} saved. Existing accepted runs retain their immutable selections.`);
      setNoticeScope(scope);
    } catch (reason) {
      if (activeScope.current !== scope) return;
      setError(reason);
      setErrorScope(scope);
    } finally {
      if (activeScope.current === scope)
        setSavingScope(null);
    }
  };

  const currentError = errorScope === requestScope ? error : null;
  const currentNotice = noticeScope === requestScope ? notice : null;
  const currentConfiguration = loadedScope === requestScope && versioned?.projectId === projectId
    ? versioned
    : null;
  const currentLoading = loading || loadedScope !== requestScope;

  return (
    <>
      <PageHeading title="Project configuration" description="Edit the owner-defined configuration document. This view does not supply or guess provider/model catalog options." />
      {currentError != null && <ErrorNotice code={errorCode(currentError)}>{errorMessage(currentError)}</ErrorNotice>}
      {currentNotice && <MessageBar intent="success"><MessageBarBody>{currentNotice}</MessageBarBody></MessageBar>}
      {currentLoading ? <Loading /> : currentConfiguration && (
        <Panel title={`Revision ${currentConfiguration.revision}`}>
          <form className="v1-form" onSubmit={(event) => void save(event)}>
            <p className="v1-muted">
              Model references and provider IDs are opaque. Only IDs already present in this project or returned by the Gateway are shown.
              The owner validates all changes and rejects unavailable selections. Secret references are metadata; credential values are never entered here.
            </p>
            <Field label="Typed ProjectConfiguration JSON" hint="Keep the complete current document to preserve unrelated settings and source-control SecretRefs.">
              <Textarea
                className="v1-json-editor"
                value={editor}
                onChange={(_, data) => setEditor(data.value)}
                resize="vertical"
                rows={24}
                spellCheck={false}
              />
            </Field>
            <div className="v1-actions">
              <Button appearance="primary" type="submit" disabled={savingScope === requestScope || !editor.trim()}>
                {savingScope === requestScope ? 'Saving…' : 'Append configuration revision'}
              </Button>
              <Button appearance="secondary" type="button" disabled={savingScope === requestScope} onClick={() => void load()}>Reload owner revision</Button>
            </div>
          </form>
        </Panel>
      )}
    </>
  );
}

function KnowledgePage() {
  const { projectId = '' } = useParams();
  const { session, apiCall, authorize } = useAuth();
  const [searchParams, setSearchParams] = useSearchParams();
  const [runId, setRunId] = useState(searchParams.get('runId') ?? '');
  const [agentId, setAgentId] = useState(searchParams.get('agentId') ?? '');
  const [query, setQuery] = useState('');
  const [kind, setKind] = useState('');
  const [includeInactive, setIncludeInactive] = useState(true);
  const [page, setPage] = useState(1);
  const pageSize = 50;
  const [records, setRecords] = useState<KnowledgeRecord[]>([]);
  const [totalCount, setTotalCount] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [noticeScope, setNoticeScope] = useState<string | null>(null);
  const [recordsScope, setRecordsScope] = useState<string | null>(null);
  const [errorScope, setErrorScope] = useState<string | null>(null);
  const [draft, setDraft] = useState({ kind: 'memory', type: '', title: '', content: '', rationale: '', importance: 'normal', tags: '' });
  const binding = useMemo(
    () => runId.trim() ? { projectId, runId: runId.trim() } : null,
    [projectId, runId],
  );
  const correctlyScoped = isSameBinding(session?.binding, binding);
  const requestScope = JSON.stringify([
    session?.accessToken ?? null,
    projectId,
    binding?.runId ?? null,
    agentId.trim(),
    query,
    kind,
    includeInactive,
    page,
  ]);
  const activeScope = useRef(requestScope);

  const load = useCallback(async () => {
    if (!binding || !agentId.trim() || !correctlyScoped) return;
    const scope = requestScope;
    setLoading(true);
    setRecords([]);
    setTotalCount(0);
    setRecordsScope(null);
    setNotice(null);
    setNoticeScope(null);
    setError(null);
    setErrorScope(null);
    try {
      const resultPage = await apiCall(
        (token) => gatewayClient.searchKnowledge(token, projectId, binding.runId, agentId.trim(), {
          query, kind: kind || undefined, includeInactive, page, pageSize,
        }),
        binding,
      );
      if (activeScope.current !== scope) return;
      if (resultPage.items.some((record) => record.projectId !== projectId || record.agentId !== agentId.trim()))
        throw new Error('Knowledge returned records outside the exact project and agent scope.');
      setRecords(resultPage.items);
      setTotalCount(resultPage.totalCount);
      setRecordsScope(scope);
      setError(null);
      setErrorScope(scope);
    } catch (reason) {
      if (activeScope.current !== scope) return;
      setError(reason);
      setErrorScope(scope);
      setRecords([]);
      setTotalCount(0);
      setRecordsScope(scope);
    } finally {
      if (activeScope.current === scope) setLoading(false);
    }
  }, [agentId, apiCall, binding, correctlyScoped, includeInactive, kind, page, pageSize, projectId, query, requestScope]);

  useEffect(() => {
    activeScope.current = requestScope;
    if (!binding || !agentId.trim() || !correctlyScoped) return undefined;
    const timer = window.setTimeout(() => { void load(); }, 0);
    return () => window.clearTimeout(timer);
  }, [binding, agentId, correctlyScoped, load, requestScope]);

  const setScope = (event: FormEvent) => {
    event.preventDefault();
    setPage(1);
    const next = new URLSearchParams(searchParams);
    if (runId.trim()) next.set('runId', runId.trim());
    else next.delete('runId');
    if (agentId.trim()) next.set('agentId', agentId.trim());
    else next.delete('agentId');
    setSearchParams(next);
  };

  const createRecord = async (event: FormEvent) => {
    event.preventDefault();
    if (!binding || !correctlyScoped || !agentId.trim()) return;
    const scope = requestScope;
    const idempotencyKey = crypto.randomUUID();
    try {
      const result = await apiCall((token) => gatewayClient.createKnowledgeRecord(
        token,
        projectId,
        binding.runId,
        agentId.trim(),
        {
          kind: draft.kind,
          type: draft.type.trim(),
          title: draft.title.trim() || null,
          content: draft.content,
          rationale: draft.rationale.trim() || null,
          importance: draft.importance.trim(),
          tags: draft.tags.split(',').map((tag) => tag.trim()).filter(Boolean),
        },
        idempotencyKey,
      ), binding);
      if (activeScope.current !== scope) return;
      setNotice(`Knowledge owner returned ${result.status}; persisted trust and revision state are shown after refresh.`);
      setNoticeScope(scope);
      await load();
    } catch (reason) {
      if (activeScope.current !== scope) return;
      setError(reason);
      setErrorScope(scope);
    }
  };

  const decideProposal = async (record: KnowledgeRecord, promote: boolean) => {
    if (!binding) return;
    const scope = requestScope;
    const idempotencyKey = crypto.randomUUID();
    try {
      const result = promote
        ? await apiCall((token) => gatewayClient.promoteKnowledgeProposal(
          token, projectId, binding.runId, agentId.trim(), record.recordId, record.revision, idempotencyKey,
        ), binding)
        : await apiCall((token) => gatewayClient.rejectKnowledgeProposal(
          token, projectId, binding.runId, agentId.trim(), record.recordId, record.revision, idempotencyKey,
        ), binding);
      if (activeScope.current !== scope) return;
      setNotice(`Knowledge owner returned ${result.status}; proposal trust and delivery state remain owner-authoritative.`);
      setNoticeScope(scope);
      await load();
    } catch (reason) {
      if (activeScope.current !== scope) return;
      setError(reason);
      setErrorScope(scope);
    }
  };

  const archiveRecord = async (record: KnowledgeRecord) => {
    if (!binding) return;
    const scope = requestScope;
    const idempotencyKey = crypto.randomUUID();
    try {
      const result = await apiCall((token) => gatewayClient.updateKnowledgeRecord(
        token,
        projectId,
        binding.runId,
        agentId.trim(),
        record.recordId,
        record.revision,
        {
          type: record.type,
          title: record.title,
          content: record.content,
          rationale: record.rationale,
          importance: record.importance,
          tags: record.tags,
          state: 'archived',
        },
        idempotencyKey,
      ), binding);
      if (activeScope.current !== scope) return;
      setNotice(`Knowledge owner returned ${result.status}; the resulting record state is not inferred locally.`);
      setNoticeScope(scope);
      await load();
    } catch (reason) {
      if (activeScope.current !== scope) return;
      setError(reason);
      setErrorScope(scope);
    }
  };

  const currentRecords = recordsScope === requestScope ? records : [];
  const currentError = errorScope === requestScope ? error : null;
  const currentNotice = noticeScope === requestScope ? notice : null;

  return (
    <>
      <PageHeading title="Knowledge" description="Knowledge is scoped to an exact project, run, and agent. Current role and run bindings are checked by the owner for every request." />
      {currentError != null && <ErrorNotice code={errorCode(currentError)}>{errorMessage(currentError)}</ErrorNotice>}
      {currentNotice && <MessageBar intent="info"><MessageBarBody>{currentNotice}</MessageBarBody></MessageBar>}
      <Panel title="Knowledge scope">
        <form className="v1-form v1-inline-form" onSubmit={setScope}>
          <Field label="Exact run ID"><Input value={runId} onChange={(_, data) => setRunId(data.value)} /></Field>
          <Field label="Exact agent ID"><Input value={agentId} onChange={(_, data) => setAgentId(data.value)} /></Field>
          <Button appearance="primary" type="submit" disabled={!runId.trim() || !agentId.trim()}>Apply scope</Button>
        </form>
        {binding && !correctlyScoped && (
          <div className="v1-scope-action">
            <p className="v1-muted">A run-bound Broker token is required for this exact pair. No project or run identity is inferred from another token.</p>
            <Button appearance="primary" onClick={() => void authorize(binding)}>Sign in for this project and run</Button>
          </div>
        )}
      </Panel>
      {binding && correctlyScoped && agentId.trim() && (
        <>
          <Panel title="Search Knowledge" actions={<Button appearance="subtle" onClick={() => void load()}>Refresh</Button>}>
            <form className="v1-form v1-inline-form" onSubmit={(event) => { event.preventDefault(); void load(); }}>
              <Field label="Search text"><Input value={query} onChange={(_, data) => { setPage(1); setQuery(data.value); }} /></Field>
              <Field label="Kind">
                <select className="v1-select" value={kind} onChange={(event) => { setPage(1); setKind(event.target.value); }}>
                  <option value="">All kinds</option>
                  <option value="memory">Memory</option>
                  <option value="proposal">Proposal</option>
                  <option value="decision">Decision</option>
                  <option value="sessionContext">Session context</option>
                </select>
              </Field>
              <label className="v1-checkbox"><input type="checkbox" checked={includeInactive} onChange={(event) => { setPage(1); setIncludeInactive(event.target.checked); }} /> Include inactive records</label>
              <Button appearance="secondary" type="submit">Search</Button>
            </form>
            <p className="v1-muted">
              {recordsScope === requestScope ? totalCount : 0} record(s) for agent {agentId.trim()} in run {binding.runId}.
              {' '}Page {page} of {Math.max(1, Math.ceil((recordsScope === requestScope ? totalCount : 0) / pageSize))}.
            </p>
            <div className="v1-actions">
              <Button
                appearance="secondary"
                disabled={loading || recordsScope !== requestScope || page <= 1}
                onClick={() => setPage((current) => Math.max(1, current - 1))}
              >
                Previous page
              </Button>
              <Button
                appearance="secondary"
                disabled={loading || recordsScope !== requestScope ||
                  page >= Math.max(1, Math.ceil(totalCount / pageSize))}
                onClick={() => setPage((current) =>
                  Math.min(Math.max(1, Math.ceil(totalCount / pageSize)), current + 1))}
              >
                Next page
              </Button>
            </div>
            {loading || recordsScope !== requestScope ? <Loading /> : currentRecords.length === 0 ? <p className="v1-muted">No matching Knowledge records.</p> : (
              <div className="v1-record-list">
                {currentRecords.map((record) => (
                  <article className="v1-record" key={record.recordId}>
                    <div className="v1-record-title">
                      <div><h3>{record.title || record.type}</h3><p className="v1-muted">{record.kind} · {record.type} · revision {record.revision} · trust: {record.trustState}</p></div>
                      <Badge appearance="tint" color={record.state === 'active' ? 'success' : record.state === 'pending' ? 'warning' : 'subtle'}>{record.state}</Badge>
                    </div>
                    <p className="v1-record-content">{record.content}</p>
                    {record.rationale && <p className="v1-muted">Rationale: {record.rationale}</p>}
                    {record.tags.length > 0 && <p className="v1-muted">Tags: {record.tags.join(', ')}</p>}
                    <div className="v1-actions">
                      {record.kind === 'proposal' && record.state === 'pending' && (
                        <>
                          <Button appearance="primary" onClick={() => void decideProposal(record, true)}>Promote proposal</Button>
                          <Button appearance="secondary" onClick={() => void decideProposal(record, false)}>Reject proposal</Button>
                        </>
                      )}
                      {record.state === 'active' && (
                        <Button appearance="secondary" onClick={() => void archiveRecord(record)}>Archive record</Button>
                      )}
                    </div>
                  </article>
                ))}
              </div>
            )}
          </Panel>
          <Panel title="Create a Knowledge record">
            <form className="v1-form" onSubmit={(event) => void createRecord(event)}>
              <div className="v1-inline-form">
                <Field label="Kind">
                  <select className="v1-select" value={draft.kind} onChange={(event) => setDraft({ ...draft, kind: event.target.value })}>
                    <option value="memory">Memory</option><option value="proposal">Proposal</option>
                    <option value="decision">Decision</option><option value="sessionContext">Session context</option>
                  </select>
                </Field>
                <Field label="Type"><Input value={draft.type} onChange={(_, data) => setDraft({ ...draft, type: data.value })} /></Field>
                <Field label="Importance"><Input value={draft.importance} onChange={(_, data) => setDraft({ ...draft, importance: data.value })} /></Field>
              </div>
              <Field label="Title"><Input value={draft.title} onChange={(_, data) => setDraft({ ...draft, title: data.value })} /></Field>
              <Field label="Content"><Textarea value={draft.content} onChange={(_, data) => setDraft({ ...draft, content: data.value })} rows={5} resize="vertical" /></Field>
              <Field label="Rationale"><Textarea value={draft.rationale} onChange={(_, data) => setDraft({ ...draft, rationale: data.value })} rows={3} resize="vertical" /></Field>
              <Field label="Tags (comma-separated)"><Input value={draft.tags} onChange={(_, data) => setDraft({ ...draft, tags: data.value })} /></Field>
              <Button appearance="primary" type="submit" disabled={!draft.type.trim() || !draft.content.trim() || !draft.importance.trim()}>Create record</Button>
            </form>
          </Panel>
        </>
      )}
    </>
  );
}

function sessionDepth(node: SessionTreeNode, nodes: SessionTreeNode[]): number {
  let depth = 0;
  let current = node;
  const seen = new Set<string>([node.identity.sessionId]);
  while (current.parentSessionId) {
    const parent = nodes.find((candidate) => candidate.identity.sessionId === current.parentSessionId);
    if (!parent || seen.has(parent.identity.sessionId)) break;
    seen.add(parent.identity.sessionId);
    depth++;
    current = parent;
  }
  return depth;
}

function createTopology(
  tree: SessionTreeSnapshot | null,
  statuses: Record<string, SessionStatusSnapshot>,
) {
  if (!tree) return { nodes: [], edges: [] };
  const groups = new Map<number, SessionTreeNode[]>();
  for (const node of tree.nodes) {
    const depth = sessionDepth(node, tree.nodes);
    groups.set(depth, [...(groups.get(depth) ?? []), node]);
  }
  const nodes = [...groups.entries()].flatMap(([depth, items]) => items.map((node, index) => {
    const status = statuses[node.identity.sessionId];
    const description = status
      ? `${status.activity}${status.activityUnavailableCode ? ` (${status.activityUnavailableCode})` : ''} · ${status.lifecycle}${status.blockers.length ? ` · ${status.blockers.length} blocker(s)` : ''}`
      : 'Status unavailable';
    return {
      id: node.identity.sessionId,
      position: { x: depth * 300, y: index * 150 },
      data: { label: `${node.kind}\n${node.identity.sessionId}\n${description}` },
      style: {
        width: 250,
        minHeight: 92,
        borderRadius: 12,
        border: '1px solid var(--colorNeutralStroke1)',
        background: 'var(--colorNeutralBackground1)',
        color: 'var(--colorNeutralForeground1)',
        padding: 12,
        whiteSpace: 'pre-line' as const,
        boxShadow: 'var(--shadow2)',
      },
    };
  }));
  const edges = tree.nodes
    .filter((node) => node.parentSessionId && tree.nodes.some((parent) => parent.identity.sessionId === node.parentSessionId))
    .map((node) => ({
      id: `${node.parentSessionId}->${node.identity.sessionId}`,
      source: node.parentSessionId!,
      target: node.identity.sessionId,
      animated: statuses[node.identity.sessionId]?.activity === 'busy',
      style: { stroke: 'var(--colorNeutralStroke1)' },
    }));
  return { nodes, edges };
}

function eventSummary(event: SessionEventEnvelope): string {
  const payload = event.payload;
  if (event.kind === 'turn') {
    const role = typeof payload.role === 'string' ? payload.role : 'turn';
    return `${role} content is retained as an opaque journal object reference; the Gateway does not return that object content.`;
  }
  if (event.kind === 'toolCall') {
    const name = typeof payload.toolName === 'string' ? payload.toolName : 'Tool call';
    const state = typeof payload.state === 'string' ? payload.state : 'state unavailable';
    return `${name} · ${state}`;
  }
  if (event.kind === 'decisionAccepted') {
    const type = typeof payload.decisionType === 'string' ? payload.decisionType : 'Decision';
    const selected = typeof payload.selectedOption === 'string' ? payload.selectedOption : 'selection content unavailable';
    return `${type} · ${selected}`;
  }
  if (event.kind === 'addressedMessage') {
    const id = typeof payload.messageId === 'string' ? payload.messageId : 'message reference';
    const status = typeof payload.status === 'string' ? ` · owner journal status ${payload.status}` : '';
    return `Addressed-message receipt ${id}${status}; receipt is not proof of delivery, task completion, or approval.`;
  }
  if (event.kind === 'policyEvaluation') {
    const outcome = typeof payload.outcome === 'string' ? payload.outcome : 'unknown';
    const reason = typeof payload.reasonCode === 'string' ? payload.reasonCode : 'reason unavailable';
    return `Policy evidence · ${outcome} · ${reason}`;
  }
  return `${event.kind} event; content is available only through the exact fields returned by the journal DTO.`;
}

function JournalList({ events }: { events: SessionEventEnvelope[] }) {
  if (events.length === 0) return <p className="v1-muted">No committed run events are available yet.</p>;
  return (
    <ol className="v1-event-list">
      {events.slice().reverse().map((event) => (
        <li key={event.eventId}>
          <div className="v1-event-meta"><Badge appearance="outline">{event.kind}</Badge><span>Position {event.position}</span><time>{new Date(event.occurredAt).toLocaleString()}</time></div>
          <p>{eventSummary(event)}</p>
          <code>{event.identity.sessionId} · {event.eventId}</code>
        </li>
      ))}
    </ol>
  );
}

function SnapshotDetail({ runStatus }: { runStatus: OwnerRunStatus }) {
  return (
    <dl className="v1-detail-list">
      <div><dt>Logical run execution</dt><dd>{runStatus.executionState}</dd></div>
      <div><dt>Execution fence</dt><dd>{runStatus.executionFence}</dd></div>
      <div><dt>State version</dt><dd>{runStatus.stateVersion}</dd></div>
      <div><dt>Logical turn ordinal</dt><dd>{runStatus.logicalTurnOrdinal}</dd></div>
      {runStatus.causeCode && <div><dt>Owner cause</dt><dd>{runStatus.causeCode}</dd></div>}
      {runStatus.reference && <div><dt>Owner reference</dt><dd>{runStatus.reference}</dd></div>}
    </dl>
  );
}

function ActivityView({
  runStatus,
  statuses,
}: {
  runStatus: OwnerRunStatus | null;
  statuses: Record<string, SessionStatusSnapshot>;
}) {
  return (
    <div className="v1-stack">
      {runStatus && <Panel title="Run owner snapshot"><SnapshotDetail runStatus={runStatus} /></Panel>}
      <Panel title="Session owner snapshots">
        {Object.values(statuses).length === 0 ? <p className="v1-muted">No session status snapshots are available.</p> : (
          <div className="v1-record-list">
            {Object.values(statuses).map((status) => (
              <article className="v1-record" key={status.identity.sessionId}>
                <div className="v1-record-title">
                  <div><h3>{status.kind} · {status.identity.sessionId}</h3><p className="v1-muted">Root: {status.rootSessionId}{status.parentSessionId ? ` · Parent: ${status.parentSessionId}` : ''}</p></div>
                  <Badge appearance="tint" color={status.activity === 'busy' ? 'informative' : status.activity === 'idle' ? 'success' : 'warning'}>
                    {status.activity}{status.activityUnavailableCode ? ` · ${status.activityUnavailableCode}` : ''}
                  </Badge>
                </div>
                <p className="v1-muted">Lifecycle: {status.lifecycle} · detached: {String(status.detached)} · fence {status.executionFence} · version {status.stateVersion}</p>
                <p className="v1-muted">Runtime effects: {status.runtimeEffectsState}{status.runtimeEffectsUnavailableCode ? ` · ${status.runtimeEffectsUnavailableCode}` : ''}</p>
                <p className="v1-muted">Run owner state: {status.runExecution.state} · state version {status.runExecution.stateVersion}</p>
                {status.runExecution.causeCode && <p className="v1-muted">Cause: {status.runExecution.causeCode}{status.runExecution.reference ? ` · ${status.runExecution.reference}` : ''}</p>}
              </article>
            ))}
          </div>
        )}
      </Panel>
    </div>
  );
}

interface PendingMessage {
  id: string;
  from: string;
  to: string;
  text: string;
  status: string;
}

function ChatView({
  projectId,
  runId,
  nodes,
  journalEvents,
  apiCall,
  snapshotBusy,
}: {
  projectId: string;
  runId: string;
  nodes: SessionTreeNode[];
  journalEvents: SessionEventEnvelope[];
  apiCall: AuthReturn['apiCall'];
  snapshotBusy: boolean;
}) {
  const activeNodes = useMemo(() => nodes.filter((node) => node.lifecycle === 'active'), [nodes]);
  const [selectedFromSession, setSelectedFromSession] = useState('');
  const [selectedToSession, setSelectedToSession] = useState('');
  const [text, setText] = useState('');
  const [pending, setPending] = useState<PendingMessage[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const fromSession = activeNodes.some((node) => node.identity.sessionId === selectedFromSession)
    ? selectedFromSession
    : activeNodes[0]?.identity.sessionId ?? '';
  const toSession = activeNodes.some((node) =>
    node.identity.sessionId === selectedToSession && selectedToSession !== fromSession)
    ? selectedToSession
    : activeNodes.find((node) => node.identity.sessionId !== fromSession)?.identity.sessionId ?? '';

  const send = async (event: FormEvent) => {
    event.preventDefault();
    if (!fromSession || !toSession || fromSession === toSession || !text.trim()) return;
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const idempotencyKey = crypto.randomUUID();
      const result = await apiCall((token) => gatewayClient.sendMessage(
        token, projectId, runId, fromSession, toSession, text.trim(), idempotencyKey, 'immediate',
      ), { projectId, runId });
      setPending((items) => [...items, {
        id: result.ownerMessageId,
        from: fromSession,
        to: toSession,
        text: text.trim(),
        status: result.status,
      }]);
      setText('');
      setNotice(`The owner accepted message ${result.ownerMessageId}. This receipt does not prove delivery or run completion.`);
    } catch (reason) {
      setError(reason);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="v1-stack">
      {error != null && <ErrorNotice code={errorCode(error)}>{errorMessage(error)}</ErrorNotice>}
      {notice && <MessageBar intent="info"><MessageBarBody>{notice}</MessageBarBody></MessageBar>}
      <Panel title="Run journal activity">
        <p className="v1-muted">
          The Gateway journal exposes ordered event DTOs and opaque object references. It has no object-content read route;
          this view does not invent or recover missing transcript text.
        </p>
        <JournalList events={journalEvents} />
      </Panel>
      <Panel title="Addressed message">
        <p className="v1-muted">
          This sends an immediate owner message between existing sessions. It does not create a session, schedule a child, or
          treat acceptance as delivery. Message text below is only held in this browser after the owner accepts it.
        </p>
        {activeNodes.length < 2 ? (
          <p className="v1-muted">At least two existing active sessions are required to send a message. Creating or scheduling one is not available through this view.</p>
        ) : (
          <form className="v1-form" onSubmit={(event) => void send(event)}>
            <div className="v1-inline-form">
              <Field label="From session">
                <select className="v1-select" value={fromSession} onChange={(event) => setSelectedFromSession(event.target.value)}>
                  {activeNodes.map((node) => <option key={node.identity.sessionId} value={node.identity.sessionId}>{node.kind} · {node.identity.sessionId}</option>)}
                </select>
              </Field>
              <Field label="To session">
                <select className="v1-select" value={toSession} onChange={(event) => setSelectedToSession(event.target.value)}>
                  {activeNodes.map((node) => <option key={node.identity.sessionId} value={node.identity.sessionId}>{node.kind} · {node.identity.sessionId}</option>)}
                </select>
              </Field>
            </div>
            <Field label="Message">
              <Textarea value={text} onChange={(_, data) => setText(data.value)} rows={4} resize="vertical" />
            </Field>
            <Button appearance="primary" type="submit" disabled={snapshotBusy || busy || !text.trim() || !fromSession || !toSession || fromSession === toSession}>
              {busy ? 'Sending…' : 'Send immediately'}
            </Button>
          </form>
        )}
        {pending.length > 0 && (
          <ol className="v1-chat-list">
            {pending.map((message) => (
              <li key={message.id}>
                <div><Badge appearance="outline">{message.status}</Badge><span>{message.from} → {message.to}</span></div>
                <p>{message.text}</p>
                <code>{message.id} · This local echo is not durable transcript evidence.</code>
              </li>
            ))}
          </ol>
        )}
      </Panel>
    </div>
  );
}

type AuthReturn = ReturnType<typeof useAuth>;

function BlockerAction({
  projectId,
  runId,
  status,
  blocker,
  decisions,
  apiCall,
  refresh,
  snapshotBusy,
}: {
  projectId: string;
  runId: string;
  status: SessionStatusSnapshot;
  blocker: SessionStatusBlocker;
  decisions?: CoordinatorDecisionStateView;
  apiCall: AuthReturn['apiCall'];
  refresh: () => Promise<void>;
  snapshotBusy: boolean;
}) {
  const [answer, setAnswer] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const currentGate = decisions?.pendingGate;
  const exactGate = currentGate?.requestId === blocker.requestId;
  const canAct = exactGate && !busy && !snapshotBusy;

  const submit = async (approve?: boolean, choiceId?: string, freeformAnswer?: string) => {
    if (!exactGate || !decisions) return;
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const idempotencyKey = crypto.randomUUID();
      let result: Awaited<ReturnType<typeof gatewayClient.answerGate>>;
      if (blocker.kind === 'awaitingInput') {
        result = await apiCall((token) => gatewayClient.answerGate(
          token, projectId, runId, status.identity.sessionId, blocker.requestId, decisions.stateVersion,
          { choiceId, freeformAnswer }, idempotencyKey,
        ), { projectId, runId });
      } else {
        result = await apiCall((token) => gatewayClient.resolveGate(
          token, projectId, runId, status.identity.sessionId, blocker.requestId, decisions.stateVersion, Boolean(approve),
          idempotencyKey,
        ), { projectId, runId });
      }
      if (!result.accepted) {
        const details = result.issues
          .map((issue) => `${issue.code} at ${issue.path}: ${issue.message}`)
          .join(' ');
        setNotice(
          `The owner did not accept request ${blocker.requestId} at state version ${result.stateVersion}.` +
          (details ? ` ${details}` : ''),
        );
        await refresh();
        return;
      }
      setNotice(`The owner accepted the request for ${blocker.requestId}. A refreshed owner snapshot determines the resulting state.`);
      await refresh();
    } catch (reason) {
      setError(reason);
      if (reason instanceof GatewayError && reason.status === 409) {
        setNotice('This request was stale or changed. The latest owner snapshots were reloaded; no approval state was inferred.');
        await refresh();
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <article className="v1-record">
      <div className="v1-record-title">
        <div><h3>{blocker.kind}</h3><p className="v1-muted">Request ID: <code>{blocker.requestId}</code></p></div>
        <Badge appearance="tint" color={blocker.kind === 'awaitingInput' ? 'informative' : 'warning'}>Pending in owner snapshot</Badge>
      </div>
      {blocker.prompt && <p>{blocker.prompt}</p>}
      {blocker.kind === 'awaitingInput' ? (
        <div className="v1-form">
          {blocker.choices.map((choice) => (
            <Button key={choice} appearance={answer === choice ? 'primary' : 'secondary'} disabled={!canAct} onClick={() => setAnswer(choice)}>
              {choice}
            </Button>
          ))}
          {blocker.allowsFreeform && (
            <Field label="Freeform answer">
              <Textarea value={answer} onChange={(_, data) => setAnswer(data.value)} rows={3} disabled={!canAct} />
            </Field>
          )}
          <Button
            appearance="primary"
            disabled={!canAct || (!blocker.choices.includes(answer) && !(blocker.allowsFreeform && answer.trim()))}
            onClick={() => void submit(undefined, blocker.choices.includes(answer) ? answer : undefined, blocker.choices.includes(answer) ? undefined : answer)}
          >
            {busy ? 'Submitting…' : 'Answer exact request'}
          </Button>
        </div>
      ) : (
        <ApprovalGate
          stepId={blocker.requestId}
          body={blocker.kind === 'awaitingOutcomeConfirmation'
            ? 'Confirm the exact pending outcome gate.'
            : blocker.kind === 'awaitingPlanApproval'
              ? 'Confirm the exact pending work-plan gate.'
              : 'Confirm the exact pending approval gate.'}
          disclaimer="The owner validates this request ID and expected decision-state version. Only a refreshed owner snapshot can show its next state."
          disabled={!canAct}
          onApprove={() => void submit(true)}
          onDeny={() => void submit(false)}
        />
      )}
      {!exactGate && <p className="v1-warning">No matching pending gate with this exact request ID was returned by the decision snapshot. Actions are disabled until the owner snapshots converge.</p>}
      {error != null && <ErrorNotice code={errorCode(error)}>{errorMessage(error)}</ErrorNotice>}
      {notice && <p className="v1-muted">{notice}</p>}
    </article>
  );
}

function ApprovalsView({
  projectId,
  runId,
  statuses,
  decisions,
  apiCall,
  refresh,
  snapshotBusy,
}: {
  projectId: string;
  runId: string;
  statuses: Record<string, SessionStatusSnapshot>;
  decisions: Record<string, CoordinatorDecisionStateView>;
  apiCall: AuthReturn['apiCall'];
  refresh: () => Promise<void>;
  snapshotBusy: boolean;
}) {
  const pending = Object.values(statuses).flatMap((status) =>
    status.blockers.map((blocker) => ({ status, blocker })));
  return (
    <Panel title="Pending outcome, plan, question, and approval requests">
      {pending.length === 0 ? <p className="v1-muted">No pending requests were returned by current session status snapshots.</p> : (
        <div className="v1-record-list">
          {pending.map(({ status, blocker }) => (
            <BlockerAction
              key={`${status.identity.sessionId}/${blocker.requestId}`}
              projectId={projectId}
              runId={runId}
              status={status}
              blocker={blocker}
              decisions={decisions[status.identity.sessionId]}
              apiCall={apiCall}
              refresh={refresh}
              snapshotBusy={snapshotBusy}
            />
          ))}
        </div>
      )}
    </Panel>
  );
}

function SelectionView({ selection }: { selection: EffectiveRunSelection | null }) {
  if (!selection) return <p className="v1-muted">No accepted run-selection snapshot was returned by Projects & Config.</p>;
  return (
    <div className="v1-stack">
      <p className="v1-muted">This is the immutable accepted selection, not proof that a resource was provisioned or bound.</p>
      <dl className="v1-detail-list">
        <div><dt>Model selection reference</dt><dd><code>{selection.modelSelection.reference}</code></dd></div>
        <div><dt>Configuration revision</dt><dd>{selection.projectConfigurationRevision}</dd></div>
        <div><dt>Platform defaults revision</dt><dd>{selection.platformRuntimeRevision}</dd></div>
        <div><dt>Selection context revision</dt><dd><code>{selection.contextRevision}</code></dd></div>
        <div><dt>Runtime model ID</dt><dd>Unavailable in the current Gateway read contract; the selection reference is not treated as a runtime SDK model ID.</dd></div>
        <div><dt>Provisioned provider pin</dt><dd>Unavailable in the current Gateway read contract; candidates below are not presented as resource bindings.</dd></div>
      </dl>
      <div className="v1-record-list">
        {selection.providers.map((provider, index) => (
          <article className="v1-record" key={`${provider.seam}/${provider.meterSource ?? ''}/${index}`}>
            <h3>{provider.seam}{provider.meterSource ? ` · ${provider.meterSource}` : ''}</h3>
            <p className="v1-muted">Cardinality: {provider.cardinality}</p>
            {provider.candidates.map((candidate) => (
              <div className="v1-candidate" key={`${candidate.providerId}/${candidate.optionsRevision}`}>
                <strong>{candidate.providerId}</strong>
                <span>Adapter {candidate.adapterVersion} · options schema {candidate.optionsSchemaVersion} · revision {candidate.optionsRevision}</span>
                <span>Hosting: {candidate.hosting}{candidate.layer ? ` · layer ${candidate.layer}` : ''}</span>
                <span>Capabilities: {candidate.advertisedCapabilities.length ? candidate.advertisedCapabilities.join(', ') : 'none reported'}</span>
              </div>
            ))}
          </article>
        ))}
      </div>
    </div>
  );
}

function UsageView({ usage }: { usage: UsageRunTotals | null }) {
  if (!usage) return <p className="v1-muted">No usage totals were returned by Events & Sessions.</p>;
  const measurement = (value: number | null | undefined) => value == null ? 'not reported' : String(value);
  return (
    <div className="v1-stack">
      <p>Accounting state: <Badge appearance="tint" color={usage.isFullyPriced ? 'success' : 'warning'}>{usage.isFullyPriced ? 'fully priced' : 'contains unpriced usage'}</Badge></p>
      <p className="v1-muted">{usage.events} usage event(s). Null measurements remain “not reported,” never zero.</p>
      <div className="v1-record-list">
        {usage.amounts.map((amount) => (
          <article className="v1-record" key={`${amount.meterSource}/${amount.unit}`}>
            <h3>{amount.meterSource}</h3>
            <p>Priced amount: <strong>{String(amount.amount)} {amount.unit}</strong></p>
            <p className="v1-muted">{amount.pricedEvents} priced event(s) · {amount.unpricedEvents} unpriced event(s)</p>
          </article>
        ))}
        {usage.agents.map((agent) => (
          <article className="v1-record" key={agent.agentId}>
            <h3>{agent.agentId}</h3>
            <p className="v1-muted">{agent.events} usage event(s) · {agent.isFullyPriced ? 'fully priced' : 'includes unpriced usage'}</p>
            <p className="v1-muted">
              Input {measurement(agent.inputTokens)} · output {measurement(agent.outputTokens)} · cached {measurement(agent.cachedTokens)}
              {' · '}reasoning {measurement(agent.reasoningTokens)} · requests {measurement(agent.requestCount)}
              {' · '}duration ms {measurement(agent.durationMilliseconds)}
            </p>
            {agent.amounts.map((amount) => (
              <p key={`${agent.agentId}/${amount.meterSource}/${amount.unit}`} className="v1-muted">
                {amount.meterSource}: {String(amount.amount)} {amount.unit}; {amount.unpricedEvents} unpriced event(s)
              </p>
            ))}
          </article>
        ))}
      </div>
    </div>
  );
}

function RunPage() {
  const { projectId = '', runId = '' } = useParams();
  const binding = useMemo(() => ({ projectId, runId }), [projectId, runId]);
  const { session, apiCall, authorize } = useAuth();
  const authorized = isSameBinding(session?.binding, binding);
  const snapshotScope = useMemo(() => ({
    projectId,
    runId,
    accessToken: session?.accessToken ?? '',
  }), [projectId, runId, session?.accessToken]);
  const activeSnapshotScope = useRef(snapshotScope);
  const journal = useRunJournal(authorized ? session?.accessToken : undefined, projectId, runId);
  const [runStatus, setRunStatus] = useState<OwnerRunStatus | null>(null);
  const [tree, setTree] = useState<SessionTreeSnapshot | null>(null);
  const [statuses, setStatuses] = useState<Record<string, SessionStatusSnapshot>>({});
  const [decisions, setDecisions] = useState<Record<string, CoordinatorDecisionStateView>>({});
  const [snapshotErrors, setSnapshotErrors] = useState<Record<string, string>>({});
  const [selection, setSelection] = useState<EffectiveRunSelection | null>(null);
  const [usage, setUsage] = useState<UsageRunTotals | null>(null);
  const [tab, setTab] = useState('topology');
  const [selectedSession, setSelectedSession] = useState('');
  const [snapshotBusy, setSnapshotBusy] = useState(true);
  const [snapshotMessage, setSnapshotMessage] = useState<string | null>(null);
  const [loadedSnapshotScope, setLoadedSnapshotScope] = useState<typeof snapshotScope | null>(null);
  const refreshGeneration = useRef(0);
  const refreshRef = useRef<() => Promise<void>>(async () => undefined);

  useLayoutEffect(() => {
    activeSnapshotScope.current = snapshotScope;
    refreshGeneration.current += 1;
  }, [snapshotScope]);

  const refresh = useCallback(async () => {
    if (!authorized) return;
    const scope = snapshotScope;
    const generation = ++refreshGeneration.current;
    const isCurrent = () =>
      refreshGeneration.current === generation && activeSnapshotScope.current === scope;
    setSnapshotBusy(true);
    const nextErrors: Record<string, string> = {};
    const [runResult, selectionResult, usageResult] = await Promise.allSettled([
      apiCall((token) => gatewayClient.getRunStatus(token, projectId, runId), binding),
      apiCall((token) => gatewayClient.getRunSelection(token, projectId, runId), binding),
      apiCall((token) => gatewayClient.getRunUsage(token, projectId, runId), binding),
    ]);
    if (!isCurrent()) return;
    if (runResult.status === 'rejected') {
      nextErrors.run = errorMessage(runResult.reason);
      setRunStatus(null);
      setTree(null);
      setStatuses({});
      setDecisions({});
      if (selectionResult.status === 'fulfilled' &&
          selectionResult.value.projectId === projectId && selectionResult.value.runId === runId)
        setSelection(selectionResult.value);
      else {
        setSelection(null);
        nextErrors.selection = selectionResult.status === 'rejected'
          ? errorMessage(selectionResult.reason)
          : 'Projects & Config returned a selection for a different run.';
      }
      if (usageResult.status === 'fulfilled' &&
          usageResult.value.projectId === projectId && usageResult.value.runId === runId)
        setUsage(usageResult.value);
      else {
        setUsage(null);
        nextErrors.usage = usageResult.status === 'rejected'
          ? errorMessage(usageResult.reason)
          : 'Events & Sessions returned usage for a different run.';
      }
      setSnapshotErrors(nextErrors);
      setLoadedSnapshotScope(scope);
      setSnapshotBusy(false);
      return;
    }
    const currentRun = runResult.value;
    if (currentRun.projectId !== projectId || currentRun.runId !== runId) {
      setRunStatus(null);
      setTree(null);
      setStatuses({});
      setDecisions({});
      setSelection(null);
      setUsage(null);
      setSnapshotErrors({ run: 'The Orchestrator returned a snapshot for a different project or run.' });
      setLoadedSnapshotScope(scope);
      setSnapshotBusy(false);
      return;
    }
    setRunStatus(currentRun);
    if (selectionResult.status === 'fulfilled' &&
        selectionResult.value.projectId === projectId && selectionResult.value.runId === runId)
      setSelection(selectionResult.value);
    else {
      setSelection(null);
      nextErrors.selection = selectionResult.status === 'rejected'
        ? errorMessage(selectionResult.reason)
        : 'Projects & Config returned a selection for a different run.';
    }
    if (usageResult.status === 'fulfilled' &&
        usageResult.value.projectId === projectId && usageResult.value.runId === runId)
      setUsage(usageResult.value);
    else {
      setUsage(null);
      nextErrors.usage = usageResult.status === 'rejected'
        ? errorMessage(usageResult.reason)
        : 'Events & Sessions returned usage for a different run.';
    }

    try {
      const nextTree = await apiCall(
        (token) => gatewayClient.getSessionTree(token, projectId, runId, currentRun.rootSessionId),
        binding,
      );
      if (!isCurrent()) return;
      if (nextTree.rootSessionId !== currentRun.rootSessionId ||
          nextTree.nodes.some((node) =>
            node.identity.projectId !== projectId || node.identity.runId !== runId)) {
        throw new Error('The Orchestrator returned a session tree outside this exact run.');
      }
      setTree(nextTree);
      const sessionResults = await Promise.allSettled(nextTree.nodes.map((node) =>
        apiCall((token) => gatewayClient.getSessionStatus(token, projectId, runId, node.identity.sessionId), binding)));
      if (!isCurrent()) return;
      const nextStatuses: Record<string, SessionStatusSnapshot> = {};
      const decisionSessions: string[] = [];
      sessionResults.forEach((result, index) => {
        const sessionId = nextTree.nodes[index].identity.sessionId;
        if (result.status === 'fulfilled' &&
            result.value.identity.projectId === projectId &&
            result.value.identity.runId === runId &&
            result.value.identity.sessionId === sessionId) {
          nextStatuses[sessionId] = result.value;
          if (result.value.blockers.length > 0 || sessionId === currentRun.rootSessionId)
            decisionSessions.push(sessionId);
        } else {
          nextErrors[`session:${sessionId}`] = result.status === 'rejected'
            ? errorMessage(result.reason)
            : 'The Orchestrator returned status for a different session.';
        }
      });
      setStatuses(nextStatuses);
      const decisionResults = await Promise.allSettled(decisionSessions.map((sessionId) =>
        apiCall((token) => gatewayClient.getDecisions(token, projectId, runId, sessionId), binding)));
      if (!isCurrent()) return;
      const nextDecisions: Record<string, CoordinatorDecisionStateView> = {};
      decisionResults.forEach((result, index) => {
        const sessionId = decisionSessions[index];
        if (result.status === 'fulfilled') nextDecisions[sessionId] = result.value;
        else nextErrors[`decisions:${sessionId}`] = errorMessage(result.reason);
      });
      setDecisions(nextDecisions);
      if (!selectedSession || !nextTree.nodes.some((node) => node.identity.sessionId === selectedSession))
        setSelectedSession(currentRun.rootSessionId);
    } catch (reason) {
      if (!isCurrent()) return;
      nextErrors.topology = errorMessage(reason);
      setTree(null);
      setStatuses({});
      setDecisions({});
    }
    if (!isCurrent()) return;
    setSnapshotErrors(nextErrors);
    setLoadedSnapshotScope(scope);
    setSnapshotBusy(false);
  }, [apiCall, authorized, binding, projectId, runId, selectedSession, snapshotScope]);

  useEffect(() => {
    refreshRef.current = refresh;
  }, [refresh]);
  useEffect(() => {
    if (!authorized) return undefined;
    let cancelled = false;
    let timer = 0;
    const poll = async () => {
      await refreshRef.current();
      if (!cancelled) timer = window.setTimeout(() => { void poll(); }, 5000);
    };
    void poll();
    return () => {
      cancelled = true;
      refreshGeneration.current += 1;
      window.clearTimeout(timer);
    };
  }, [authorized, projectId, runId, session?.accessToken]);

  if (!authorized) {
    return (
      <>
        <PageHeading title="Run" description={`Project ${projectId} · run ${runId}`} />
        <Panel title="Run-bound sign-in">
          <p>This page needs a Broker access token bound to this exact project and run. The Broker validates the active grant; a different token is not reused.</p>
          <Button appearance="primary" onClick={() => void authorize(binding)}>Sign in for this project and run</Button>
        </Panel>
      </>
    );
  }

  const snapshotIsCurrent = loadedSnapshotScope === snapshotScope;
  const currentRunStatus = snapshotIsCurrent ? runStatus : null;
  const currentTree = snapshotIsCurrent ? tree : null;
  const currentStatuses = snapshotIsCurrent ? statuses : {};
  const currentDecisions = snapshotIsCurrent ? decisions : {};
  const currentSelection = snapshotIsCurrent ? selection : null;
  const currentUsage = snapshotIsCurrent ? usage : null;
  const currentSnapshotErrors = snapshotIsCurrent ? snapshotErrors : {};
  const currentSnapshotMessage = snapshotIsCurrent ? snapshotMessage : null;
  const nodes = currentTree?.nodes ?? [];
  const graph = createTopology(currentTree, currentStatuses);
  const nodeById = new Map(nodes.map((node) => [node.identity.sessionId, node]));
  const selectedNode = nodeById.get(selectedSession);
  const selectedStatus = currentStatuses[selectedSession];
  const topologyNodeClick = (_event: unknown, node: { id: string }) => setSelectedSession(node.id);

  const runAction = async (node: SessionTreeNode, action: 'detach' | 'archive') => {
    if (!currentRunStatus || snapshotBusy) return;
    const idempotencyKey = crypto.randomUUID();
    setSnapshotMessage(null);
    try {
      if (action === 'detach') {
        await apiCall((token) => gatewayClient.detachSession(
          token, projectId, runId, node.identity.sessionId, currentRunStatus.executionFence, idempotencyKey,
        ), binding);
      } else if (node.parentSessionId) {
        await apiCall((token) => gatewayClient.archiveChild(
          token, projectId, runId, node.parentSessionId!, node.identity.sessionId, currentRunStatus.executionFence, idempotencyKey,
        ), binding);
      }
      setSnapshotMessage(`${action} was accepted by the owner. The view waits for the next owner snapshot before displaying a state change.`);
      await refreshRef.current();
    } catch (reason) {
      if (reason instanceof GatewayError && reason.status === 409) {
        setSnapshotMessage(`${errorMessage(reason)} The latest owner snapshot was reloaded; no lifecycle or detach state was inferred.`);
        await refreshRef.current();
      } else {
        setSnapshotMessage(errorMessage(reason));
      }
    }
  };

  return (
    <>
      <PageHeading
        title="Coordinator run"
        description={`Project ${projectId} · run ${runId} · root session ${currentRunStatus?.rootSessionId ?? 'loading'}`}
        actions={<Button appearance="secondary" disabled={snapshotBusy} onClick={() => void refreshRef.current()}>Refresh owner snapshots</Button>}
      />
      {currentSnapshotMessage && <MessageBar intent="info"><MessageBarBody>{currentSnapshotMessage}</MessageBarBody></MessageBar>}
      {Object.entries(currentSnapshotErrors).map(([key, message]) => (
        <ErrorNotice key={key} code={key}>{message}</ErrorNotice>
      ))}
      {(snapshotBusy || !snapshotIsCurrent) && !currentRunStatus && <Loading />}
      {currentRunStatus && (
        <>
          <Panel title="Authoritative run snapshot">
            <SnapshotDetail runStatus={currentRunStatus} />
            <p className="v1-muted">The status comes from the Orchestrator owner. It does not indicate AgentHost health or infer pod state.</p>
          </Panel>
          <div className="v1-run-tabs">
            <TabList selectedValue={tab} onTabSelect={(_, data) => setTab(String(data.value))} aria-label="Run views">
              <Tab value="topology" icon={<Flowchart24Regular />}>Topology</Tab>
              <Tab value="chat" icon={<Chat24Regular />}>Chat</Tab>
              <Tab value="approvals">Outcomes & approvals</Tab>
              <Tab value="activity">Activity</Tab>
              <Tab value="selection">Selection</Tab>
              <Tab value="usage">Usage</Tab>
            </TabList>
          </div>
          {tab === 'topology' && (
            <div className="v1-stack">
              <Panel title="Coordinator topology">
                {currentTree && nodes.length > 0 ? (
                  <div className="v1-topology" aria-label="Coordinator session topology">
                    <ReactFlowView nodes={graph.nodes} edges={graph.edges} onNodeClick={topologyNodeClick} />
                  </div>
                ) : <p className="v1-muted">No session tree nodes were returned by the Orchestrator.</p>}
                <p className="v1-muted">Node activity, lifecycle, blockers, detach, and archive controls are derived only from Orchestrator snapshots.</p>
              </Panel>
              {selectedNode && selectedStatus && (
                <Panel title={`Selected session · ${selectedNode.identity.sessionId}`}>
                  <p className="v1-muted">{selectedNode.kind} · {selectedStatus.activity}{selectedStatus.activityUnavailableCode ? ` (${selectedStatus.activityUnavailableCode})` : ''} · {selectedStatus.lifecycle} · detached {String(selectedStatus.detached)}</p>
                  <div className="v1-actions">
                    {selectedNode.parentSessionId && !selectedNode.detached && selectedNode.lifecycle === 'active' && (
                      <Button appearance="secondary" disabled={snapshotBusy} onClick={() => void runAction(selectedNode, 'detach')}>Detach child</Button>
                    )}
                    {selectedNode.parentSessionId && !selectedNode.detached &&
                      selectedNode.lifecycle === 'completed' && selectedStatus.runExecution.state === 'completed' && (
                        <Button appearance="secondary" disabled={snapshotBusy} onClick={() => void runAction(selectedNode, 'archive')}>Archive completed child</Button>
                      )}
                    {selectedStatus.parentSessionId && <Link className="v1-link-button" to={`/projects/${encodeURIComponent(projectId)}/knowledge?runId=${encodeURIComponent(runId)}&agentId=${encodeURIComponent(selectedNode.identity.sessionId)}`}><Brain24Regular /> Knowledge for exact session ID</Link>}
                  </div>
                  {selectedStatus.blockers.length > 0 && <p className="v1-warning">This session has {selectedStatus.blockers.length} owner-reported blocker(s). Open Outcomes & approvals to respond to the exact request IDs.</p>}
                </Panel>
              )}
            </div>
          )}
          {tab === 'chat' && <ChatView key={`${projectId}/${runId}/${session?.accessToken ?? ''}`} projectId={projectId} runId={runId} nodes={nodes} journalEvents={journal.events} apiCall={apiCall} snapshotBusy={snapshotBusy} />}
          {tab === 'approvals' && (
            <ApprovalsView projectId={projectId} runId={runId} statuses={currentStatuses} decisions={currentDecisions} apiCall={apiCall} refresh={() => refreshRef.current()} snapshotBusy={snapshotBusy} />
          )}
          {tab === 'activity' && (
            <div className="v1-stack">
              <Panel title={`Committed event journal · ${journal.connected ? 'live' : journal.loading ? 'loading' : 'reconnecting'}`}>
                {journal.error && <ErrorNotice code={errorCode(journal.error)}>{errorMessage(journal.error)}</ErrorNotice>}
                <p className="v1-muted">Events are replayed in journal order, then streamed from the replay cursor. Duplicate event IDs are removed and positions remain sorted.</p>
                <JournalList events={journal.events} />
              </Panel>
              <ActivityView runStatus={currentRunStatus} statuses={currentStatuses} />
            </div>
          )}
          {tab === 'selection' && <Panel title="Accepted run selection"><SelectionView selection={currentSelection} /></Panel>}
          {tab === 'usage' && <Panel title="Run usage and accounting"><UsageView usage={currentUsage} /></Panel>}
        </>
      )}
    </>
  );
}

function ReactFlowView({
  nodes,
  edges,
  onNodeClick,
}: {
  nodes: ReturnType<typeof createTopology>['nodes'];
  edges: ReturnType<typeof createTopology>['edges'];
  onNodeClick: (event: unknown, node: { id: string }) => void;
}) {
  return (
    <ReactFlow
      nodes={nodes}
      edges={edges}
      fitView
      fitViewOptions={{ padding: 0.2 }}
      minZoom={0.35}
      maxZoom={1.5}
      onNodeClick={onNodeClick}
      nodesConnectable={false}
      nodesDraggable={false}
      elementsSelectable
      proOptions={{ hideAttribution: true }}
    >
      <Background color="var(--colorNeutralStroke2)" />
      <Controls />
      <MiniMap pannable zoomable />
    </ReactFlow>
  );
}

function CallbackPage() {
  const { session, error, busy } = useAuth();
  if (session) return <Navigate to="/projects" replace />;
  return (
    <div className="v1-auth-page">
      <section className="v1-auth-card">
        <h1>Completing sign-in</h1>
        {busy ? <Loading label="Exchanging Broker authorization code…" /> : error ? <ErrorNotice>{error}</ErrorNotice> : <p className="v1-muted">Waiting for an Identity Broker response.</p>}
      </section>
    </div>
  );
}

function AuthenticatedRoutes() {
  const { session, consent } = useAuth();
  if (consent) return <ConsentScreen />;
  if (!session) return <SignInScreen />;
  return (
    <Routes>
      <Route path="/" element={<Navigate to="/projects" replace />} />
      <Route path="/auth/callback" element={<CallbackPage />} />
      <Route path="/projects" element={<Shell><ProjectsPage /></Shell>} />
      <Route path="/projects/:projectId" element={<Shell><ProjectOverviewPage /></Shell>} />
      <Route path="/projects/:projectId/settings" element={<Shell><ProjectConfigurationPage /></Shell>} />
      <Route path="/projects/:projectId/knowledge" element={<Shell><KnowledgePage /></Shell>} />
      <Route path="/projects/:projectId/runs/:runId" element={<Shell><RunPage /></Shell>} />
      <Route path="/projects/:projectId/orchestrations/:runId" element={<CoordinatorRunRedirect />} />
      <Route path="*" element={<Navigate to="/projects" replace />} />
    </Routes>
  );
}

function CoordinatorRunRedirect() {
  const { projectId = '', runId = '' } = useParams();
  return <Navigate to={`/projects/${encodeURIComponent(projectId)}/runs/${encodeURIComponent(runId)}`} replace />;
}

export default function App() {
  return (
    <FluentProvider theme={agentweaverLightTheme}>
      <AuthProvider>
        <BrowserRouter>
          <AuthenticatedRoutes />
        </BrowserRouter>
      </AuthProvider>
    </FluentProvider>
  );
}
