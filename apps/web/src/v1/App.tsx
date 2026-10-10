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
import {
  parseAuthorizationCallbackParameters,
} from './authProtocol';
import { gatewayClient, GatewayError } from './api';
import { CopilotPopupCallbackPage, CopilotUserConnectionPanel } from './CopilotUserConnectionPanel';
import { COPILOT_CALLBACK_PATH } from './copilotCallback';
import { RemoteMcpOAuthPopupCallbackPage } from './RemoteMcpOAuthCallbackPage';
import {
  isRemoteMcpOAuthCallbackMessage,
  REMOTE_MCP_OAUTH_CALLBACK_PATH,
} from './remoteMcpOAuthCallback';
import {
  isCurrentRepoAppPopupCallback,
  isRepoAppConnectedCallbackMessage,
  REPO_APP_CALLBACK_MESSAGE_TYPE,
  REPO_APP_CALLBACK_PATH,
  repoAppBrokerClient,
  RepoAppBrokerError,
} from './repoApp';
import type { RepoAppConnectionStatus, RepoAppRepository } from './repoApp';
import type {
  CoordinatorDecisionStateView,
  EffectiveRunSelection,
  KnowledgeRecord,
  KnowledgeRecordRevision,
  KnowledgeRecordTransferBundle,
  ProjectConfiguration,
  ProjectSummary,
  SessionEventEnvelope,
  SessionStatusBlocker,
  SessionStatusSnapshot,
  SessionTreeNode,
  SessionTreeSnapshot,
  SourceControlRepositoryPinView,
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
  return error instanceof GatewayError || error instanceof RepoAppBrokerError
    ? error.code
    : undefined;
}

function isSameBinding(
  actual: RunBinding | null | undefined,
  expected: RunBinding | null,
): boolean {
  return actual?.projectId === expected?.projectId && actual?.runId === expected?.runId;
}

const RUN_TAB_VALUES = ['topology', 'chat', 'approvals', 'activity', 'selection', 'usage'] as const;
type RunTab = typeof RUN_TAB_VALUES[number];
const REPO_APP_POPUP_TIMEOUT_MS = 10 * 60 * 1000;
const REPO_APP_POPUP_POLL_INTERVAL_MS = 500;

function isRunTab(value: string | null): value is RunTab {
  return value !== null && RUN_TAB_VALUES.includes(value as RunTab);
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
            <button
              className="aw-nav-item v1-nav-button"
              disabled={!session?.binding}
              onClick={() => {
                if (!session?.binding) return;
                navigate(
                  `/projects/${encodeURIComponent(session.binding.projectId)}/runs/${encodeURIComponent(session.binding.runId)}?view=chat`,
                );
              }}
            >
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
      setProjects(await apiCall(
        (token, tenantSelector) => gatewayClient.listProjects(token, tenantSelector),
        null,
      ));
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
      const created = await apiCall(
        (token, tenantSelector) => gatewayClient.createProject(token, projectName.trim(), tenantSelector),
        null,
      );
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
      void apiCall(
        (token, tenantSelector) => gatewayClient.getProject(token, projectId, tenantSelector),
        null,
      )
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

function ProjectRepoAppConnectionPanel() {
  const [connection, setConnection] = useState<RepoAppConnectionStatus | null>(null);
  const [repositories, setRepositories] = useState<RepoAppRepository[]>([]);
  const [statusLoading, setStatusLoading] = useState(true);
  const [repositoriesLoading, setRepositoriesLoading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [notice, setNotice] = useState<{ intent: 'info' | 'success' | 'warning'; text: string } | null>(null);
  const popupRequestRef = useRef<{
    popup: Window;
    generation: number;
    abortController: AbortController;
  } | null>(null);
  const popupMonitorRef = useRef<number | null>(null);
  const popupTimeoutRef = useRef<number | null>(null);
  const connectGenerationRef = useRef(0);
  const statusGenerationRef = useRef(0);
  const statusUnavailable = !statusLoading && connection === null && error != null;
  const stopPopupMonitor = useCallback(() => {
    if (popupMonitorRef.current !== null)
      window.clearInterval(popupMonitorRef.current);
    popupMonitorRef.current = null;
    if (popupTimeoutRef.current !== null)
      window.clearTimeout(popupTimeoutRef.current);
    popupTimeoutRef.current = null;
  }, []);

  const loadConnection = useCallback(async () => {
    const generation = ++statusGenerationRef.current;
    setStatusLoading(true);
    try {
      const result = await repoAppBrokerClient.getStatus();
      if (generation !== statusGenerationRef.current) return;
      setConnection(result);
      setError(null);
    } catch (reason) {
      if (generation !== statusGenerationRef.current) return;
      setConnection(null);
      setError(reason);
    } finally {
      if (generation === statusGenerationRef.current) setStatusLoading(false);
    }
  }, []);

  const loadRepositories = useCallback(async () => {
    setRepositoriesLoading(true);
    setError(null);
    try {
      const result = await repoAppBrokerClient.getRepositories();
      setRepositories(result.repositories);
    } catch (reason) {
      setRepositories([]);
      setError(reason);
    } finally {
      setRepositoriesLoading(false);
    }
  }, []);

  useEffect(() => {
    const timer = window.setTimeout(() => { void loadConnection(); }, 0);
    return () => window.clearTimeout(timer);
  }, [loadConnection]);

  useEffect(() => {
    const onMessage = (event: MessageEvent<unknown>) => {
      const pending = popupRequestRef.current;
      if (!pending || !isCurrentRepoAppPopupCallback(
        event,
        window.location.origin,
        pending.popup,
        pending.generation,
        connectGenerationRef.current,
      ))
        return;
      pending.popup.close();
      pending.abortController.abort();
      popupRequestRef.current = null;
      connectGenerationRef.current += 1;
      stopPopupMonitor();
      setNotice({
        intent: 'info',
        text: 'GitHub returned to Agentweaver. Checking the connection status with the Identity Broker.',
      });
      setBusy(false);
      void loadConnection();
    };
    window.addEventListener('message', onMessage);
    return () => {
      window.removeEventListener('message', onMessage);
      connectGenerationRef.current += 1;
      statusGenerationRef.current += 1;
      const pending = popupRequestRef.current;
      popupRequestRef.current = null;
      if (pending) {
        pending.abortController.abort();
        if (!pending.popup.closed) pending.popup.close();
      }
      stopPopupMonitor();
    };
  }, [loadConnection, stopPopupMonitor]);

  const connect = async () => {
    if (statusUnavailable) return;
    const generation = ++connectGenerationRef.current;
    const requestId = `agentweaver-repo-app-${generation}-${window.crypto?.randomUUID?.() ?? Date.now().toString(36)}`;
    const popup = window.open(
      'about:blank',
      requestId,
      'popup,width=560,height=720',
    );
    if (!popup) {
      setError(new Error('Allow pop-ups to connect GitHub without losing this session.'));
      return;
    }
    popup.name = requestId;
    const abortController = new AbortController();
    popupRequestRef.current = { popup, generation, abortController };
    setBusy(true);
    setError(null);
    setNotice(null);
    popupTimeoutRef.current = window.setTimeout(() => {
      const pending = popupRequestRef.current;
      if (pending?.popup !== popup || pending.generation !== generation) return;
      pending.abortController.abort();
      if (!popup.closed) popup.close();
      popupRequestRef.current = null;
      connectGenerationRef.current += 1;
      stopPopupMonitor();
      setBusy(false);
      setNotice({
        intent: 'warning',
        text: 'GitHub authorization timed out before the Identity Broker could start it. Start the connection again.',
      });
    }, REPO_APP_POPUP_TIMEOUT_MS);
    popupMonitorRef.current = window.setInterval(() => {
      const current = popupRequestRef.current;
      if (popup.closed && current?.popup === popup && current.generation === generation) {
        current.abortController.abort();
        stopPopupMonitor();
        popupRequestRef.current = null;
        connectGenerationRef.current += 1;
        setBusy(false);
        setNotice({
          intent: 'warning',
          text: 'The GitHub popup closed before the Identity Broker completed the request. Start the connection again.',
        });
      }
    }, REPO_APP_POPUP_POLL_INTERVAL_MS);
    try {
      await repoAppBrokerClient.beginConnect(popup, abortController.signal);
      const pending = popupRequestRef.current;
      if (pending?.generation === generation) {
        setNotice({ intent: 'info', text: 'Complete GitHub authorization in the tracked browser window.' });
      }
    } catch (reason) {
      const pending = popupRequestRef.current;
      if (pending?.popup === popup && pending.generation === generation) {
        popup.close();
        pending.abortController.abort();
        popupRequestRef.current = null;
        stopPopupMonitor();
        setBusy(false);
        setError(reason);
      }
    }
  };

  const disconnect = async () => {
    if (!connection?.connectionId || !connection.connectionRevision) {
      setError(new Error('The Broker did not return a connection revision; refresh status before disconnecting.'));
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const next = await repoAppBrokerClient.disconnect(
        connection.connectionId,
        connection.connectionRevision,
      );
      setConnection(next);
      setRepositories([]);
      setNotice({ intent: 'success', text: 'GitHub Repo App was disconnected from this identity.' });
    } catch (reason) {
      setError(reason);
    } finally {
      setBusy(false);
    }
  };
  const connected = connection?.state === 'connected';

  return (
    <Panel title="GitHub Repo App">
      <p className="v1-muted">
        Connect a GitHub account for repository access. Status, repository metadata, and selection codes come directly from the Identity Broker; provider credentials stay with the owner.
      </p>
      {statusLoading ? <Loading label="Checking GitHub Repo App connection…" /> : statusUnavailable ? (
        <p className="v1-muted">GitHub Repo App connection status is unavailable; no connection state is assumed.</p>
      ) : connected ? (
        <p>
          Connected as <strong>{connection.githubLogin ?? 'GitHub user'}</strong>
          {connection.connectionId && <> · Identity connection <code>{connection.connectionId}</code></>}
          {connection.localReadiness !== 'access_token_available' && (
            <> · {connection.localReadiness.replaceAll('_', ' ')}</>
          )}
        </p>
      ) : (
        <p className="v1-muted">
          {connection?.state === 'not_connected'
            ? 'No GitHub Repo App connection is available for this identity.'
            : 'The GitHub Repo App connection requires attention before it can be used.'}
        </p>
      )}
      <p className="v1-muted">
        To use this connection in a project, set <code>sourceControl.authMode</code> to <code>githubApp</code> and
        set <code>sourceControl.appConnectionId</code> to the matching Identity connection ID. Existing configurations
        that omit <code>authMode</code> remain in legacy secret mode.
      </p>
      {error != null && <ErrorNotice code={errorCode(error)}>{errorMessage(error)}</ErrorNotice>}
      {notice && <MessageBar intent={notice.intent}><MessageBarBody>{notice.text}</MessageBarBody></MessageBar>}
      <div className="v1-actions">
        <Button
          appearance="primary"
          disabled={busy || statusLoading || statusUnavailable}
          onClick={() => void connect()}
        >
          {statusUnavailable ? 'Connection status unavailable' : busy ? 'Opening GitHub…' : connected ? 'Reauthorize GitHub Repo App' : 'Connect GitHub Repo App'}
        </Button>
        {connected && (
          <>
            <Button appearance="secondary" disabled={busy || statusLoading || repositoriesLoading} onClick={() => void loadRepositories()}>
              {repositoriesLoading ? 'Loading repositories…' : 'Browse repositories'}
            </Button>
            <Button appearance="secondary" disabled={busy || statusLoading} onClick={() => void disconnect()}>
              Disconnect GitHub
            </Button>
          </>
        )}
        <Button appearance="secondary" disabled={statusLoading || busy} onClick={() => void loadConnection()}>
          Refresh status
        </Button>
      </div>
      {repositories.length > 0 && (
        <ul className="v1-list" aria-label="GitHub repositories">
          {repositories.map((repository) => (
            <li key={`${repository.installationId}/${repository.repositoryId}`}>
              <strong>{repository.fullName}</strong>
              {repository.isPrivate ? ' · private' : ' · public'}
              {' · default branch '}{repository.defaultBranch}
            </li>
          ))}
        </ul>
      )}
      {!repositoriesLoading && repositories.length === 0 && connected && (
        <p className="v1-muted">No repository metadata is currently loaded.</p>
      )}
    </Panel>
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
      const result = await apiCall(
        (token, tenantSelector) => gatewayClient.getProjectConfiguration(token, projectId, tenantSelector),
        null,
      );
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
        (token, tenantSelector) => gatewayClient.updateProjectConfiguration(
          token, projectId, versioned.revision, configuration, tenantSelector,
        ),
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
      <ProjectRepoAppConnectionPanel />
      <CopilotUserConnectionPanel projectId={projectId} />
      {currentError != null && <ErrorNotice code={errorCode(currentError)}>{errorMessage(currentError)}</ErrorNotice>}
      {currentNotice && <MessageBar intent="success"><MessageBarBody>{currentNotice}</MessageBarBody></MessageBar>}
      {currentLoading ? <Loading /> : currentConfiguration && (
        <Panel title={`Revision ${currentConfiguration.revision}`}>
          <form className="v1-form" onSubmit={(event) => void save(event)}>
            <p className="v1-muted">
              Model references and provider IDs are opaque. Only IDs already present in this project or returned by the Gateway are shown.
              The owner validates all changes and rejects unavailable selections. Secret references are metadata; credential values are never entered here.
            </p>
            <Field label="Typed ProjectConfiguration JSON" hint="Keep the complete current document to preserve unrelated settings, GitHub App connection references, and legacy source-control SecretRefs.">
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

function isKnowledgeRecordInScope(value: unknown, projectId: string, agentId: string): boolean {
  return typeof value === 'object' && value !== null &&
    (value as { projectId?: unknown }).projectId === projectId &&
    (value as { agentId?: unknown }).agentId === agentId &&
    typeof (value as { recordId?: unknown }).recordId === 'string' &&
    (value as { recordId: string }).recordId.length > 0;
}

function assertKnowledgeRecordsInScope(records: unknown, projectId: string, agentId: string): asserts records is KnowledgeRecord[] {
  if (!Array.isArray(records) || records.some((record) => !isKnowledgeRecordInScope(record, projectId, agentId)))
    throw new Error('Knowledge returned records outside the exact project and agent scope.');
}

function assertKnowledgeMutationScope(
  result: unknown,
  projectId: string,
  agentId: string,
): asserts result is { record?: KnowledgeRecord | null; proposal?: KnowledgeRecord | null; decision?: KnowledgeRecord | null } {
  if (typeof result !== 'object' || result === null)
    throw new Error('Knowledge returned an invalid mutation result.');
  const mutation = result as {
    record?: unknown;
    proposal?: unknown;
    decision?: unknown;
  };
  for (const record of [mutation.record, mutation.proposal, mutation.decision]) {
    if (record != null && !isKnowledgeRecordInScope(record, projectId, agentId))
      throw new Error('Knowledge returned a mutation record outside the exact project and agent scope.');
  }
}

function assertKnowledgeTransferBundleScope(
  value: unknown,
  projectId: string,
  runId: string,
  agentId: string,
): asserts value is KnowledgeRecordTransferBundle {
  if (typeof value !== 'object' || value === null || Array.isArray(value))
    throw new Error('Knowledge transfer bundle must be a versioned JSON object.');
  const bundle = value as Partial<KnowledgeRecordTransferBundle> & { runId?: unknown };
  if (bundle.format !== 'agentweaver.knowledge-transfer.v1' || bundle.schemaVersion !== 1)
    throw new Error('Knowledge transfer bundle has an unsupported format or schema version.');
  if (bundle.projectId !== projectId || bundle.agentId !== agentId ||
    (bundle.runId !== undefined && bundle.runId !== runId))
    throw new Error('Knowledge transfer bundle is outside the exact project, run, and agent scope.');
  if (!Array.isArray(bundle.records) || bundle.records.some((entry) => {
    if (typeof entry !== 'object' || entry === null || !('record' in entry) || !('revisions' in entry))
      return true;
    const transferEntry = entry as KnowledgeRecordTransferBundle['records'][number];
    return !isKnowledgeRecordInScope(transferEntry.record, projectId, agentId) ||
      typeof transferEntry.record.recordId !== 'string' ||
      !Array.isArray(transferEntry.revisions) ||
      transferEntry.revisions.some((revision) =>
        typeof revision !== 'object' || revision === null || revision.recordId !== transferEntry.record.recordId);
  }))
    throw new Error('Knowledge transfer bundle contains records or revisions outside its exact scope.');
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
  const [draft, setDraft] = useState({ kind: 'memory', type: '', title: '', content: '', rationale: '', importance: 'medium', tags: '' });
  const [editRecordId, setEditRecordId] = useState<string | null>(null);
  const [editRecordScope, setEditRecordScope] = useState<string | null>(null);
  const [editDraft, setEditDraft] = useState({
    type: '',
    title: '',
    content: '',
    rationale: '',
    importance: 'medium',
    tags: '',
    reason: '',
  });
  const [supersedeTargets, setSupersedeTargets] = useState<Record<string, string>>({});
  const [revisionRecordId, setRevisionRecordId] = useState<string | null>(null);
  const [revisionItems, setRevisionItems] = useState<KnowledgeRecordRevision[]>([]);
  const [revisionTotalCount, setRevisionTotalCount] = useState(0);
  const [revisionPage, setRevisionPage] = useState(1);
  const [revisionRequestScope, setRevisionRequestScope] = useState<string | null>(null);
  const [revisionScope, setRevisionScope] = useState<string | null>(null);
  const [revisionLoading, setRevisionLoading] = useState(false);
  const activeRevisionScope = useRef<string | null>(null);
  const [transferText, setTransferText] = useState('');
  const [importConfirmed, setImportConfirmed] = useState(false);
  const [exportedBundleJson, setExportedBundleJson] = useState<string | null>(null);
  const [transferScope, setTransferScope] = useState<string | null>(null);
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
  const closeRevisionHistory = () => {
    activeRevisionScope.current = null;
    setRevisionRecordId(null);
    setRevisionRequestScope(null);
    setRevisionScope(null);
    setRevisionLoading(false);
  };
  const closeCorrection = () => {
    setEditRecordId(null);
    setEditRecordScope(null);
  };
  const isCurrentRevisionRequest = (recordId: string) =>
    revisionRecordId === recordId &&
    revisionRequestScope === JSON.stringify([requestScope, recordId, revisionPage]);

  const load = useCallback(async (preserveNotice = false) => {
    if (!binding || !agentId.trim() || !correctlyScoped) return;
    const scope = requestScope;
    setLoading(true);
    setRecords([]);
    setTotalCount(0);
    setRecordsScope(null);
    if (!preserveNotice) {
      setNotice(null);
      setNoticeScope(null);
    }
    setError(null);
    setErrorScope(null);
    try {
      const resultPage = await apiCall(
        (token, tenantSelector) => gatewayClient.searchKnowledge(token, projectId, binding.runId, agentId.trim(), {
          query, kind: kind || undefined, includeInactive, page, pageSize,
        }, tenantSelector),
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
    setTransferText('');
    setImportConfirmed(false);
    setExportedBundleJson(null);
    setTransferScope(null);
    setSearchParams(next);
  };

  const createRecord = async (event: FormEvent) => {
    event.preventDefault();
    if (!binding || !correctlyScoped || !agentId.trim()) return;
    const scope = requestScope;
    const idempotencyKey = crypto.randomUUID();
    try {
      const result = await apiCall((token, tenantSelector) => gatewayClient.createKnowledgeRecord(
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
        tenantSelector,
      ), binding);
      if (activeScope.current !== scope) return;
      assertKnowledgeMutationScope(result, projectId, agentId.trim());
      setNotice(`Knowledge owner returned ${result.status}; persisted trust and revision state are shown after refresh.`);
      setNoticeScope(scope);
      closeRevisionHistory();
      await load(true);
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
        ? await apiCall((token, tenantSelector) => gatewayClient.promoteKnowledgeProposal(
          token, projectId, binding.runId, agentId.trim(), record.recordId, record.revision, idempotencyKey, tenantSelector,
        ), binding)
        : await apiCall((token, tenantSelector) => gatewayClient.rejectKnowledgeProposal(
          token, projectId, binding.runId, agentId.trim(), record.recordId, record.revision, idempotencyKey, tenantSelector,
        ), binding);
      if (activeScope.current !== scope) return;
      assertKnowledgeMutationScope(result, projectId, agentId.trim());
      setNotice(`Knowledge owner returned ${result.status}; proposal trust and delivery state remain owner-authoritative.`);
      setNoticeScope(scope);
      closeRevisionHistory();
      await load(true);
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
      const result = await apiCall((token, tenantSelector) => gatewayClient.updateKnowledgeRecord(
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
        tenantSelector,
      ), binding);
      if (activeScope.current !== scope) return;
      assertKnowledgeMutationScope(result, projectId, agentId.trim());
      setNotice(`Knowledge owner returned ${result.status}; the resulting record state is not inferred locally.`);
      setNoticeScope(scope);
      closeCorrection();
      closeRevisionHistory();
      await load(true);
    } catch (reason) {
      if (activeScope.current !== scope) return;
      setError(reason);
      setErrorScope(scope);
    }
  };

  const startCorrection = (record: KnowledgeRecord) => {
    setEditRecordId(record.recordId);
    setEditRecordScope(requestScope);
    setEditDraft({
      type: record.type,
      title: record.title ?? '',
      content: record.content,
      rationale: record.rationale ?? '',
      importance: ['low', 'medium', 'high'].includes(record.importance) ? record.importance : 'medium',
      tags: record.tags.join(', '),
      reason: '',
    });
  };

  const saveCorrection = async (event: FormEvent, record: KnowledgeRecord) => {
    event.preventDefault();
    if (!binding || editRecordScope !== requestScope) return;
    const scope = requestScope;
    const idempotencyKey = crypto.randomUUID();
    try {
      const result = await apiCall((token, tenantSelector) => gatewayClient.updateKnowledgeRecord(
        token,
        projectId,
        binding.runId,
        agentId.trim(),
        record.recordId,
        record.revision,
        {
          type: editDraft.type.trim(),
          title: editDraft.title.trim() || null,
          content: editDraft.content,
          rationale: editDraft.rationale.trim() || null,
          importance: editDraft.importance,
          tags: editDraft.tags.split(',').map((tag) => tag.trim()).filter(Boolean),
          state: record.state,
          reason: editDraft.reason.trim() || null,
        },
        idempotencyKey,
        tenantSelector,
      ), binding);
      if (activeScope.current !== scope) return;
      assertKnowledgeMutationScope(result, projectId, agentId.trim());
      setNotice(`Knowledge owner returned ${result.status}; the corrected record is shown after refresh.`);
      setNoticeScope(scope);
      closeCorrection();
      closeRevisionHistory();
      await load(true);
    } catch (reason) {
      if (activeScope.current !== scope) return;
      setError(reason);
      setErrorScope(scope);
    }
  };

  const decideDecision = async (record: KnowledgeRecord) => {
    if (!binding) return;
    const scope = requestScope;
    const idempotencyKey = crypto.randomUUID();
    try {
      const result = await apiCall((token, tenantSelector) => gatewayClient.approveKnowledgeDecision(
        token,
        projectId,
        binding.runId,
        agentId.trim(),
        record.recordId,
        record.revision,
        null,
        idempotencyKey,
        tenantSelector,
      ), binding);
      if (activeScope.current !== scope) return;
      assertKnowledgeMutationScope(result, projectId, agentId.trim());
      setNotice(`Knowledge owner returned ${result.status}; the Decision trust state is shown after refresh.`);
      setNoticeScope(scope);
      closeRevisionHistory();
      await load(true);
    } catch (reason) {
      if (activeScope.current !== scope) return;
      setError(reason);
      setErrorScope(scope);
    }
  };

  const supersedeDecision = async (record: KnowledgeRecord, replacement: KnowledgeRecord) => {
    if (!binding) return;
    if (record.kind !== 'decision' || record.state !== 'active' ||
      replacement.kind !== 'decision' || replacement.state !== 'active' ||
      replacement.recordId === record.recordId ||
      !isKnowledgeRecordInScope(replacement, projectId, agentId.trim())) {
      setError(new Error('Choose another Active Decision in the exact project and agent scope.'));
      setErrorScope(requestScope);
      return;
    }
    const scope = requestScope;
    const idempotencyKey = crypto.randomUUID();
    try {
      const result = await apiCall((token, tenantSelector) => gatewayClient.updateKnowledgeRecord(
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
          state: 'superseded',
          supersededByRecordId: replacement.recordId,
        },
        idempotencyKey,
        tenantSelector,
      ), binding);
      if (activeScope.current !== scope) return;
      assertKnowledgeMutationScope(result, projectId, agentId.trim());
      setNotice(`Knowledge owner returned ${result.status}; the superseding Decision link is shown after refresh.`);
      setNoticeScope(scope);
      closeRevisionHistory();
      await load(true);
    } catch (reason) {
      if (activeScope.current !== scope) return;
      setError(reason);
      setErrorScope(scope);
    }
  };

  const loadRevisions = async (record: KnowledgeRecord, requestedPage = 1) => {
    if (!binding) return;
    const scope = requestScope;
    const revisionRequestScope = JSON.stringify([scope, record.recordId, requestedPage]);
    activeRevisionScope.current = revisionRequestScope;
    setRevisionRecordId(record.recordId);
    setRevisionRequestScope(revisionRequestScope);
    setRevisionItems([]);
    setRevisionTotalCount(0);
    setRevisionPage(requestedPage);
    setRevisionScope(null);
    setRevisionLoading(true);
    try {
      const resultPage = await apiCall((token, tenantSelector) => gatewayClient.readKnowledgeRevisions(
        token,
        projectId,
        binding.runId,
        agentId.trim(),
        record.recordId,
        { page: requestedPage, pageSize },
        tenantSelector,
      ), binding);
      if (activeScope.current !== scope || activeRevisionScope.current !== revisionRequestScope) return;
      if (resultPage.items.some((revision) => revision.recordId !== record.recordId))
        throw new Error('Knowledge returned revisions outside the requested record scope.');
      setRevisionItems(resultPage.items);
      setRevisionTotalCount(resultPage.totalCount);
      setRevisionScope(revisionRequestScope);
      setError(null);
      setErrorScope(scope);
    } catch (reason) {
      if (activeScope.current !== scope || activeRevisionScope.current !== revisionRequestScope) return;
      setError(reason);
      setErrorScope(scope);
      setRevisionItems([]);
      setRevisionTotalCount(0);
      setRevisionScope(revisionRequestScope);
    } finally {
      if (activeScope.current === scope && activeRevisionScope.current === revisionRequestScope)
        setRevisionLoading(false);
    }
  };

  const restoreRevision = async (record: KnowledgeRecord, revision: KnowledgeRecordRevision) => {
    if (!binding) return;
    const scope = requestScope;
    const idempotencyKey = crypto.randomUUID();
    try {
      const result = await apiCall((token, tenantSelector) => gatewayClient.restoreKnowledgeRecord(
        token,
        projectId,
        binding.runId,
        agentId.trim(),
        record.recordId,
        record.revision,
        revision.revision,
        null,
        idempotencyKey,
        tenantSelector,
      ), binding);
      if (activeScope.current !== scope) return;
      assertKnowledgeMutationScope(result, projectId, agentId.trim());
      setNotice(`Knowledge owner returned ${result.status}; the restored revision is shown after refresh.`);
      setNoticeScope(scope);
      closeRevisionHistory();
      closeCorrection();
      await load(true);
    } catch (reason) {
      if (activeScope.current !== scope) return;
      setError(reason);
      setErrorScope(scope);
    }
  };

  const exportKnowledge = async () => {
    if (!binding) return;
    const scope = requestScope;
    try {
      const bundle = await apiCall((token, tenantSelector) => gatewayClient.exportKnowledgeRecords(
        token,
        projectId,
        binding.runId,
        agentId.trim(),
        tenantSelector,
      ), binding);
      if (activeScope.current !== scope) return;
      assertKnowledgeTransferBundleScope(bundle, projectId, binding.runId, agentId.trim());
      const serialized = JSON.stringify(bundle);
      if (new TextEncoder().encode(serialized).byteLength > 1024 * 1024)
        throw new Error('The exported Knowledge bundle exceeds the 1 MiB transfer limit.');
      setExportedBundleJson(serialized);
      setTransferScope(scope);
      setNotice('Exported a versioned Knowledge bundle for the exact project, run, and agent scope.');
      setNoticeScope(scope);
      setError(null);
      setErrorScope(scope);
    } catch (reason) {
      if (activeScope.current !== scope) return;
      setError(reason);
      setErrorScope(scope);
      setExportedBundleJson(null);
      setTransferScope(null);
    }
  };

  const importKnowledge = async (event: FormEvent) => {
    event.preventDefault();
    if (!binding || !importConfirmed) return;
    const scope = requestScope;
    try {
      if (new TextEncoder().encode(transferText).byteLength > 1024 * 1024)
        throw new Error('The Knowledge import bundle exceeds the 1 MiB transfer limit.');
      let bundle: unknown;
      try {
        bundle = JSON.parse(transferText);
      } catch {
        throw new Error('Enter a valid versioned Knowledge transfer bundle in JSON format.');
      }
      assertKnowledgeTransferBundleScope(bundle, projectId, binding.runId, agentId.trim());
      if (new TextEncoder().encode(JSON.stringify(bundle)).byteLength > 1024 * 1024)
        throw new Error('The Knowledge import bundle exceeds the 1 MiB transfer limit.');
      const result = await apiCall((token, tenantSelector) => gatewayClient.importKnowledgeRecords(
        token,
        projectId,
        binding.runId,
        agentId.trim(),
        bundle,
        crypto.randomUUID(),
        tenantSelector,
      ), binding);
      if (activeScope.current !== scope) return;
      assertKnowledgeRecordsInScope(result.records, projectId, agentId.trim());
      setNotice(`Knowledge owner returned ${result.records.length} imported record(s); persisted state is shown after refresh.`);
      setNoticeScope(scope);
      setImportConfirmed(false);
      setTransferText('');
      closeRevisionHistory();
      await load(true);
    } catch (reason) {
      if (activeScope.current !== scope) return;
      setError(reason);
      setErrorScope(scope);
    }
  };

  const currentRecords = recordsScope === requestScope ? records : [];
  const currentError = errorScope === requestScope ? error : null;
  const currentNotice = noticeScope === requestScope ? notice : null;
  const currentExportedBundle = transferScope === requestScope ? exportedBundleJson : null;
  const downloadExportedBundle = () => {
    if (!currentExportedBundle) return;
    const objectUrl = URL.createObjectURL(new Blob([currentExportedBundle], { type: 'application/json' }));
    const link = document.createElement('a');
    link.href = objectUrl;
    link.download = `agentweaver-knowledge-${projectId}-${agentId.trim()}.json`;
    document.body.appendChild(link);
    link.click();
    link.remove();
    window.setTimeout(() => URL.revokeObjectURL(objectUrl), 0);
  };

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
                <select className="v1-select" aria-label="Search Knowledge kind" value={kind} onChange={(event) => { setPage(1); setKind(event.target.value); }}>
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
                      {record.state === 'active' &&
                        (record.kind === 'memory' || record.kind === 'sessionContext') && (
                        <Button appearance="secondary" onClick={() => startCorrection(record)}>Correct record</Button>
                      )}
                      {record.kind === 'proposal' && record.state === 'pending' && (
                        <>
                          <Button appearance="primary" onClick={() => void decideProposal(record, true)}>Promote proposal</Button>
                          <Button appearance="secondary" onClick={() => void decideProposal(record, false)}>Reject proposal</Button>
                        </>
                      )}
                      {record.kind === 'decision' && record.state === 'active' &&
                        (record.trustState === 'pending' || record.trustState === 'legacy') && (
                          <Button appearance="primary" onClick={() => void decideDecision(record)}>Approve Decision</Button>
                        )}
                      {record.kind === 'decision' && record.state === 'active' && (
                        <div className="v1-inline-form">
                          <Field label={`Replacement Decision for ${record.title || record.type}`}>
                            <select
                              className="v1-select"
                              aria-label={`Replacement Decision for ${record.title || record.type}`}
                              value={supersedeTargets[JSON.stringify([requestScope, record.recordId])] ?? ''}
                              onChange={(event) => setSupersedeTargets((current) => ({
                                ...current,
                                [JSON.stringify([requestScope, record.recordId])]: event.target.value,
                              }))}
                            >
                              <option value="">Select another active Decision</option>
                              {currentRecords.filter((candidate) =>
                                candidate.kind === 'decision' &&
                                candidate.state === 'active' &&
                                candidate.recordId !== record.recordId &&
                                candidate.projectId === projectId &&
                                candidate.agentId === agentId.trim()).map((candidate) => (
                                  <option key={candidate.recordId} value={candidate.recordId}>
                                    {candidate.title || candidate.type} · revision {candidate.revision}
                                  </option>
                                ))}
                            </select>
                          </Field>
                          <Button
                            appearance="secondary"
                            disabled={!currentRecords.some((candidate) =>
                              candidate.recordId === supersedeTargets[JSON.stringify([requestScope, record.recordId])] &&
                              candidate.kind === 'decision' &&
                              candidate.state === 'active' &&
                              candidate.recordId !== record.recordId &&
                              candidate.projectId === projectId &&
                              candidate.agentId === agentId.trim())}
                            onClick={() => {
                              const replacement = currentRecords.find((candidate) =>
                                candidate.recordId === supersedeTargets[JSON.stringify([requestScope, record.recordId])]);
                              if (replacement) void supersedeDecision(record, replacement);
                            }}
                          >
                            Supersede with selected Decision
                          </Button>
                        </div>
                      )}
                      {record.supersededByRecordId && (
                        <p className="v1-muted">Superseded by Decision {record.supersededByRecordId}.</p>
                      )}
                      {record.state === 'active' &&
                        (record.kind === 'memory' || record.kind === 'sessionContext') && (
                        <Button appearance="secondary" onClick={() => void archiveRecord(record)}>Archive record</Button>
                      )}
                      <Button
                        appearance="secondary"
                        onClick={() => {
                          if (isCurrentRevisionRequest(record.recordId)) {
                            closeRevisionHistory();
                          } else {
                            void loadRevisions(record);
                          }
                        }}
                      >
                        {isCurrentRevisionRequest(record.recordId) ? 'Hide revision history' : 'View revision history'}
                      </Button>
                    </div>
                    {editRecordId === record.recordId && editRecordScope === requestScope && (
                      <form className="v1-form" aria-label="Correct Knowledge record" onSubmit={(event) => void saveCorrection(event, record)}>
                        <Field label="Correction type"><Input value={editDraft.type} onChange={(_, data) => setEditDraft({ ...editDraft, type: data.value })} /></Field>
                        <Field label="Correction title"><Input value={editDraft.title} onChange={(_, data) => setEditDraft({ ...editDraft, title: data.value })} /></Field>
                        <Field label="Correction content"><Textarea value={editDraft.content} onChange={(_, data) => setEditDraft({ ...editDraft, content: data.value })} rows={5} resize="vertical" /></Field>
                        <Field label="Correction rationale"><Textarea value={editDraft.rationale} onChange={(_, data) => setEditDraft({ ...editDraft, rationale: data.value })} rows={3} resize="vertical" /></Field>
                        <Field label="Correction importance">
                          <select className="v1-select" aria-label="Correction importance" value={editDraft.importance} onChange={(event) => setEditDraft({ ...editDraft, importance: event.target.value })}>
                            <option value="low">Low</option><option value="medium">Medium</option><option value="high">High</option>
                          </select>
                        </Field>
                        <Field label="Correction tags (comma-separated)"><Input value={editDraft.tags} onChange={(_, data) => setEditDraft({ ...editDraft, tags: data.value })} /></Field>
                        <Field label="Correction reason"><Input value={editDraft.reason} onChange={(_, data) => setEditDraft({ ...editDraft, reason: data.value })} /></Field>
                        <div className="v1-actions">
                          <Button appearance="primary" type="submit" disabled={!editDraft.type.trim() || !editDraft.content.trim()}>Save correction</Button>
                          <Button appearance="secondary" type="button" onClick={closeCorrection}>Cancel correction</Button>
                        </div>
                      </form>
                    )}
                    {isCurrentRevisionRequest(record.recordId) && (
                      <section aria-label={`Revision history for ${record.title || record.type}`}>
                        <h4>Revision history</h4>
                        {revisionLoading || revisionScope !== JSON.stringify([requestScope, record.recordId, revisionPage]) ? <Loading /> : (
                          <>
                            <p className="v1-muted">
                              {revisionTotalCount} revision(s) · Page {revisionPage} of {Math.max(1, Math.ceil(revisionTotalCount / pageSize))}
                            </p>
                            <div className="v1-actions">
                              <Button
                                appearance="secondary"
                                disabled={revisionLoading || revisionPage <= 1}
                                onClick={() => void loadRevisions(record, Math.max(1, revisionPage - 1))}
                              >
                                Previous revisions
                              </Button>
                              <Button
                                appearance="secondary"
                                disabled={revisionLoading || revisionPage >= Math.max(1, Math.ceil(revisionTotalCount / pageSize))}
                                onClick={() => void loadRevisions(record, Math.min(Math.max(1, Math.ceil(revisionTotalCount / pageSize)), revisionPage + 1))}
                              >
                                Next revisions
                              </Button>
                            </div>
                            {revisionItems.length === 0 ? <p className="v1-muted">No revisions were returned.</p> : (
                              <div className="v1-record-list">
                                {revisionItems.map((revision) => (
                                  <article className="v1-record" key={revision.revisionId}>
                                    <div className="v1-record-title">
                                      <div><h5>Revision {revision.revision} · {revision.changeKind || revision.state}</h5><p className="v1-muted">{revision.createdAt}</p></div>
                                      <Badge appearance="tint" color={revision.state === 'active' ? 'success' : 'subtle'}>{revision.state}</Badge>
                                    </div>
                                    <p className="v1-record-content">{revision.content}</p>
                                    {revision.rationale && <p className="v1-muted">Rationale: {revision.rationale}</p>}
                                    {revision.reason && <p className="v1-muted">Change reason: {revision.reason}</p>}
                                    <Button
                                      appearance="secondary"
                                      disabled={revision.revision === record.revision}
                                      onClick={() => void restoreRevision(record, revision)}
                                    >
                                      Restore revision {revision.revision}
                                    </Button>
                                  </article>
                                ))}
                              </div>
                            )}
                          </>
                        )}
                      </section>
                    )}
                  </article>
                ))}
              </div>
            )}
          </Panel>
          <Panel title="Create a Knowledge record">
            <form className="v1-form" onSubmit={(event) => void createRecord(event)}>
              <div className="v1-inline-form">
                <Field label="Kind">
                  <select className="v1-select" aria-label="Knowledge record kind" value={draft.kind} onChange={(event) => setDraft({ ...draft, kind: event.target.value })}>
                    <option value="memory">Memory</option><option value="proposal">Proposal</option>
                    <option value="sessionContext">Session context</option>
                  </select>
                </Field>
                <Field label="Type"><Input value={draft.type} onChange={(_, data) => setDraft({ ...draft, type: data.value })} /></Field>
                <Field label="Importance">
                  <select className="v1-select" aria-label="Knowledge record importance" value={draft.importance} onChange={(event) => setDraft({ ...draft, importance: event.target.value })}>
                    <option value="low">Low</option><option value="medium">Medium</option><option value="high">High</option>
                  </select>
                </Field>
              </div>
              <Field label="Title"><Input value={draft.title} onChange={(_, data) => setDraft({ ...draft, title: data.value })} /></Field>
              <Field label="Content"><Textarea value={draft.content} onChange={(_, data) => setDraft({ ...draft, content: data.value })} rows={5} resize="vertical" /></Field>
              <Field label="Rationale"><Textarea value={draft.rationale} onChange={(_, data) => setDraft({ ...draft, rationale: data.value })} rows={3} resize="vertical" /></Field>
              <Field label="Tags (comma-separated)"><Input value={draft.tags} onChange={(_, data) => setDraft({ ...draft, tags: data.value })} /></Field>
              <Button appearance="primary" type="submit" disabled={!draft.type.trim() || !draft.content.trim() || !draft.importance.trim()}>Create record</Button>
            </form>
          </Panel>
          <Panel title="Knowledge transfer">
            <p className="v1-muted">Transfer uses a versioned bundle for the exact project and agent. The current run is included in each Gateway request; imports are limited to 1 MiB.</p>
            <div className="v1-actions">
              <Button appearance="secondary" onClick={() => void exportKnowledge()}>Export scoped Knowledge</Button>
            </div>
            {currentExportedBundle && (
              <>
                <Field label="Exported Knowledge bundle">
                  <Textarea value={currentExportedBundle} readOnly rows={8} resize="vertical" />
                </Field>
                <Button appearance="secondary" onClick={downloadExportedBundle}>
                  Download versioned bundle
                </Button>
              </>
            )}
            <form className="v1-form" onSubmit={(event) => void importKnowledge(event)}>
              <Field label="Versioned Knowledge bundle JSON">
                <Textarea
                  value={transferText}
                  onChange={(_, data) => { setTransferText(data.value); setImportConfirmed(false); }}
                  rows={8}
                  resize="vertical"
                />
              </Field>
              <label className="v1-checkbox">
                <input type="checkbox" checked={importConfirmed} onChange={(event) => setImportConfirmed(event.target.checked)} />
                I confirm this versioned bundle matches the current project and agent scope.
              </label>
              <Button appearance="primary" type="submit" disabled={!importConfirmed || !transferText.trim()}>Import scoped Knowledge</Button>
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
      const result = await apiCall((token, tenantSelector) => gatewayClient.sendMessage(
        token, projectId, runId, fromSession, toSession, text.trim(), idempotencyKey, 'immediate', tenantSelector,
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
        result = await apiCall((token, tenantSelector) => gatewayClient.answerGate(
          token, projectId, runId, status.identity.sessionId, blocker.requestId, decisions.stateVersion,
          { choiceId, freeformAnswer }, idempotencyKey, tenantSelector,
        ), { projectId, runId });
      } else {
        result = await apiCall((token, tenantSelector) => gatewayClient.resolveGate(
          token, projectId, runId, status.identity.sessionId, blocker.requestId, decisions.stateVersion, Boolean(approve),
          idempotencyKey, tenantSelector,
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

function repoAppRepositoryKey(repository: RepoAppRepository): string {
  return `${repository.installationId}/${repository.repositoryId}`;
}

function RunGitHubAppPanel({
  projectId,
  runId,
  sessionId,
  appConnectionId,
}: {
  projectId: string;
  runId: string;
  sessionId: string;
  appConnectionId: string;
}) {
  const { apiCall } = useAuth();
  const binding = useMemo(() => ({ projectId, runId }), [projectId, runId]);
  const [connection, setConnection] = useState<RepoAppConnectionStatus | null>(null);
  const [repositories, setRepositories] = useState<RepoAppRepository[]>([]);
  const [repositoriesLoaded, setRepositoriesLoaded] = useState(false);
  const [selectedRepository, setSelectedRepository] = useState('');
  const [pinnedRepository, setPinnedRepository] = useState<SourceControlRepositoryPinView | null>(null);
  const [statusLoading, setStatusLoading] = useState(true);
  const [repositoriesLoading, setRepositoriesLoading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [notice, setNotice] = useState<{ intent: 'info' | 'success' | 'warning'; text: string } | null>(null);
  const popupRef = useRef<Window | null>(null);
  const connectionMatches = connection?.state === 'connected' &&
    connection.connectionId === appConnectionId;
  const statusUnavailable = !statusLoading && connection === null && error != null;

  const loadConnection = useCallback(async (): Promise<RepoAppConnectionStatus | null> => {
    setStatusLoading(true);
    try {
      const next = await repoAppBrokerClient.getStatus();
      setConnection(next);
      if (next.state !== 'connected' || next.connectionId !== appConnectionId) {
        setRepositories([]);
        setRepositoriesLoaded(false);
      }
      setError(null);
      return next;
    } catch (reason) {
      setConnection(null);
      setError(reason);
      return null;
    } finally {
      setStatusLoading(false);
    }
  }, [appConnectionId]);

  const loadRepositories = useCallback(async () => {
    setRepositoriesLoading(true);
    setError(null);
    try {
      const result = await repoAppBrokerClient.getRepositories();
      setRepositories(result.repositories);
      setRepositoriesLoaded(true);
      setSelectedRepository((current) =>
        result.repositories.some((repository) => repoAppRepositoryKey(repository) === current)
          ? current
          : result.repositories[0] ? repoAppRepositoryKey(result.repositories[0]) : '');
    } catch (reason) {
      setRepositories([]);
      setRepositoriesLoaded(false);
      setError(reason);
    } finally {
      setRepositoriesLoading(false);
    }
  }, []);

  useEffect(() => {
    const timer = window.setTimeout(() => { void loadConnection(); }, 0);
    return () => window.clearTimeout(timer);
  }, [loadConnection]);

  useEffect(() => {
    const onMessage = (event: MessageEvent<unknown>) => {
      if (event.origin !== window.location.origin ||
          event.source !== popupRef.current ||
          !isRepoAppConnectedCallbackMessage(event.data))
        return;

      popupRef.current?.close();
      popupRef.current = null;
      setNotice({
        intent: 'info',
        text: 'GitHub returned to Agentweaver. Refreshing repository access with the Identity Broker.',
      });
      void loadConnection().then((next) => {
        if (next?.state === 'connected' && next.connectionId === appConnectionId)
          void loadRepositories();
        else
          setNotice({
            intent: 'warning',
            text: 'The installation callback was received, but the Identity Broker did not confirm the configured connection.',
          });
      });
    };
    window.addEventListener('message', onMessage);
    return () => window.removeEventListener('message', onMessage);
  }, [appConnectionId, loadConnection, loadRepositories]);

  const openPopup = (): Window | null => {
    const popup = window.open(
      'about:blank',
      'agentweaver-github-repo-app',
      'popup,width=560,height=720',
    );
    if (!popup) {
      setError(new Error('Allow pop-ups to connect GitHub without losing this run session.'));
      return null;
    }
    popup.name = 'agentweaver-github-repo-app';
    popupRef.current = popup;
    setError(null);
    setNotice(null);
    return popup;
  };

  const installRepoApp = async () => {
    const popup = openPopup();
    if (!popup) return;
    setBusy(true);
    try {
      await repoAppBrokerClient.beginInstallationSetup(popup);
      setNotice({ intent: 'info', text: 'Complete GitHub App installation in the new browser window.' });
    } catch (reason) {
      popup.close();
      popupRef.current = null;
      setError(reason);
    } finally {
      setBusy(false);
    }
  };

  const pinRepository = async () => {
    if (!selectedRepository) return;
    setBusy(true);
    setError(null);
    setPinnedRepository(null);
    try {
      const repository = repositories.find(
        (candidate) => repoAppRepositoryKey(candidate) === selectedRepository,
      );
      if (!repository) throw new Error('Select a repository returned by the Identity Broker.');
      const selection = await repoAppBrokerClient.createRepositorySelection(
        repository.installationId,
        repository.repositoryId,
      );
      const pinned = await apiCall(
        (token, tenantSelector) => gatewayClient.pinSourceControlRepository(
          token, projectId, runId, sessionId, tenantSelector, selection.code,
        ),
        binding,
      );
      setPinnedRepository(pinned);
      setNotice({ intent: 'success', text: `Repository ${pinned.repository} was pinned for this run.` });
    } catch (reason) {
      setError(reason);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Panel title="GitHub App repository access">
      <p className="v1-muted">
        The accepted run uses Identity App connection <code>{appConnectionId || 'not configured'}</code>.
        Repository metadata and selection codes come from the Broker; the Gateway receives the opaque code only on this run-bound pin request.
      </p>
      {statusLoading ? <Loading label="Checking GitHub App connection…" /> : statusUnavailable ? (
        <p className="v1-muted">GitHub App connection status is unavailable; no connection state is assumed.</p>
      ) : connection?.state === 'connected' ? (
        <p>
          Connected as <strong>{connection.githubLogin ?? 'GitHub user'}</strong>
          {connection.connectionId && <> · Identity connection <code>{connection.connectionId}</code></>}
        </p>
      ) : (
        <p className="v1-muted">
          {connection?.state === 'not_connected'
            ? 'No GitHub App user connection is available for this identity.'
            : 'The GitHub App user connection requires attention before it can be used.'}
        </p>
      )}
      {connection?.state === 'connected' && connection.connectionId !== appConnectionId && (
        <MessageBar intent="warning">
          <MessageBarBody>
            This project references a different App connection. Update <code>sourceControl.appConnectionId</code> in{' '}
            <Link to={`/projects/${encodeURIComponent(projectId)}/settings`}>project settings</Link> to use the displayed Identity connection.
          </MessageBarBody>
        </MessageBar>
      )}
      {error != null && <ErrorNotice code={errorCode(error)}>{errorMessage(error)}</ErrorNotice>}
      {notice && <MessageBar intent={notice.intent}><MessageBarBody>{notice.text}</MessageBarBody></MessageBar>}
      <div className="v1-actions">
        <Button appearance="secondary" disabled={statusLoading || busy} onClick={() => void loadConnection()}>
          Refresh connection status
        </Button>
        {connectionMatches && (
          <>
            <Button appearance="secondary" disabled={busy} onClick={() => void installRepoApp()}>
              Install GitHub App for this run
            </Button>
            <Button appearance="secondary" disabled={busy || repositoriesLoading} onClick={() => void loadRepositories()}>
              {repositoriesLoading ? 'Loading repositories…' : 'Load repositories'}
            </Button>
          </>
        )}
      </div>
      {!statusLoading && !statusUnavailable && !connectionMatches && (
        <p>
          <Link className="v1-link-button" to={`/projects/${encodeURIComponent(projectId)}/settings`}>
            Connect GitHub Repo App and configure this project
          </Link>
        </p>
      )}
      {connectionMatches && repositories.length > 0 && (
        <div className="v1-form">
          <Field label="Repository">
            <select
              className="v1-select"
              aria-label="Repository"
              value={selectedRepository}
              onChange={(event) => setSelectedRepository(event.currentTarget.value)}
            >
              {repositories.map((repository) => (
                <option
                  key={repoAppRepositoryKey(repository)}
                  value={repoAppRepositoryKey(repository)}
                >
                  {repository.fullName}{repository.isPrivate ? ' · private' : ''}
                </option>
              ))}
            </select>
          </Field>
          <Button appearance="primary" disabled={busy || !selectedRepository} onClick={() => void pinRepository()}>
            {busy ? 'Pinning…' : 'Pin repository to this run'}
          </Button>
        </div>
      )}
      {repositoriesLoaded && repositories.length === 0 && connectionMatches && !repositoriesLoading && (
        <p className="v1-muted">No accessible repositories were returned by the Identity Broker. Install the GitHub App for this run, then reload the repository list.</p>
      )}
      {pinnedRepository && (
        <p className="v1-muted">Pinned repository: {pinnedRepository.repository} · {pinnedRepository.defaultBranch}</p>
      )}
    </Panel>
  );
}

function RunPage() {
  const { projectId = '', runId = '' } = useParams();
  const binding = useMemo(() => ({ projectId, runId }), [projectId, runId]);
  const { session, apiCall, authorize, authorizationContext } = useAuth();
  const [searchParams, setSearchParams] = useSearchParams();
  const requestedTab = searchParams.get('view');
  const tab: RunTab = isRunTab(requestedTab) ? requestedTab : 'topology';
  const authorized = isSameBinding(session?.binding, binding);
  const snapshotScope = useMemo(() => ({
    projectId,
    runId,
    accessToken: session?.accessToken ?? '',
  }), [projectId, runId, session?.accessToken]);
  const activeSnapshotScope = useRef(snapshotScope);
  const runTenantSelector = authorized ? authorizationContext?.tenantId : undefined;
  const journal = useRunJournal(
    authorized ? session?.accessToken : undefined,
    projectId,
    runId,
    runTenantSelector,
  );
  const [runStatus, setRunStatus] = useState<OwnerRunStatus | null>(null);
  const [tree, setTree] = useState<SessionTreeSnapshot | null>(null);
  const [statuses, setStatuses] = useState<Record<string, SessionStatusSnapshot>>({});
  const [decisions, setDecisions] = useState<Record<string, CoordinatorDecisionStateView>>({});
  const [snapshotErrors, setSnapshotErrors] = useState<Record<string, string>>({});
  const [selection, setSelection] = useState<EffectiveRunSelection | null>(null);
  const [usage, setUsage] = useState<UsageRunTotals | null>(null);
  const [selectedSession, setSelectedSession] = useState('');
  const [snapshotBusy, setSnapshotBusy] = useState(true);
  const [snapshotMessage, setSnapshotMessage] = useState<string | null>(null);
  const [loadedSnapshotScope, setLoadedSnapshotScope] = useState<typeof snapshotScope | null>(null);
  const refreshGeneration = useRef(0);
  const refreshRef = useRef<() => Promise<void>>(async () => undefined);

  useEffect(() => {
    if (requestedTab === null || isRunTab(requestedTab)) return;
    const normalizedParams = new URLSearchParams(searchParams);
    normalizedParams.set('view', 'topology');
    setSearchParams(normalizedParams, { replace: true });
  }, [requestedTab, searchParams, setSearchParams]);

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
      apiCall((token, tenantSelector) => gatewayClient.getRunStatus(token, projectId, runId, tenantSelector), binding),
      apiCall((token, tenantSelector) => gatewayClient.getRunSelection(token, projectId, runId, tenantSelector), binding),
      apiCall((token, tenantSelector) => gatewayClient.getRunUsage(token, projectId, runId, tenantSelector), binding),
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
        (token, tenantSelector) => gatewayClient.getSessionTree(
          token, projectId, runId, currentRun.rootSessionId, tenantSelector,
        ),
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
        apiCall((token, tenantSelector) => gatewayClient.getSessionStatus(
          token, projectId, runId, node.identity.sessionId, tenantSelector,
        ), binding)));
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
        apiCall((token, tenantSelector) => gatewayClient.getDecisions(
          token, projectId, runId, sessionId, tenantSelector,
        ), binding)));
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
        await apiCall((token, tenantSelector) => gatewayClient.detachSession(
          token, projectId, runId, node.identity.sessionId, currentRunStatus.executionFence, idempotencyKey, tenantSelector,
        ), binding);
      } else if (node.parentSessionId) {
        await apiCall((token, tenantSelector) => gatewayClient.archiveChild(
          token, projectId, runId, node.parentSessionId!, node.identity.sessionId, currentRunStatus.executionFence, idempotencyKey, tenantSelector,
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
            <TabList
              selectedValue={tab}
              onTabSelect={(_, data) => {
                const nextTab = String(data.value);
                if (!isRunTab(nextTab)) return;
                const nextParams = new URLSearchParams(searchParams);
                nextParams.set('view', nextTab);
                setSearchParams(nextParams);
              }}
              aria-label="Run views"
            >
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
          {tab === 'selection' && (
            <div className="v1-stack">
              <Panel title="Accepted run selection"><SelectionView selection={currentSelection} /></Panel>
              {currentSelection?.projectConfiguration.sourceControl?.authMode === 'githubApp' && (
                <RunGitHubAppPanel
                  projectId={projectId}
                  runId={runId}
                  sessionId={currentRunStatus.rootSessionId}
                  appConnectionId={currentSelection.projectConfiguration.sourceControl.appConnectionId ?? ''}
                />
              )}
            </div>
          )}
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

function IdentityPopupCallbackPage() {
  const [callback] = useState(() => {
    const callbackSearch = window.__AGENTWEAVER_IDENTITY_CALLBACK__;
    const opener = window.opener;
    if (!opener || opener === window || typeof callbackSearch !== 'string')
      return { message: undefined, status: 'Sending the sign-in response to Agentweaver…' };
    const message = parseAuthorizationCallbackParameters(callbackSearch);
    return {
      message,
      status: message
        ? 'Sending the sign-in response to Agentweaver…'
        : 'The sign-in response is invalid. Return to Agentweaver and try again.',
    };
  });

  useEffect(() => {
    const callbackSearch = window.__AGENTWEAVER_IDENTITY_CALLBACK__;
    window.__AGENTWEAVER_IDENTITY_CALLBACK__ = undefined;
    const opener = window.opener;
    if (!opener || opener === window || typeof callbackSearch !== 'string') return;

    if (!callback.message) {
      window.close();
      return;
    }
    opener.postMessage(callback.message, window.location.origin);
  }, [callback.message]);

  return (
    <div className="v1-auth-page">
      <section className="v1-auth-card">
        <h1>Completing sign-in</h1>
        <p className="v1-muted">{callback.status}</p>
      </section>
    </div>
  );
}

function RepoAppCallbackRelay() {
  const [searchParams] = useSearchParams();
  const authorizationOutcome = searchParams.get('repo_app_auth');
  const installationOutcome = searchParams.get('repo_app_install');

  useEffect(() => {
    const outcome = authorizationOutcome ?? installationOutcome;
    const kind = authorizationOutcome
      ? 'authorization'
      : installationOutcome
        ? 'installation'
        : null;
    if (!kind || !outcome || !window.opener || window.opener === window) return;
    window.opener.postMessage(
      { type: 'agentweaver.repo-app.callback', kind, outcome },
      window.location.origin,
    );
    window.close();
  }, [authorizationOutcome, installationOutcome]);

  return null;
}

function RepoAppPopupCallbackPage() {
  const [message] = useState(() => {
    const opener = window.opener;
    return window.__AGENTWEAVER_REPO_APP_CALLBACK__ === true && opener && opener !== window
      ? 'The callback is notifying its opener. Only the opener’s Identity Broker status check can confirm the connection.'
      : 'The Broker callback was received, but no tracked account window is available. Connection status is unverified; return to account settings and refresh status.';
  });

  useEffect(() => {
    const callbackWasCaptured = window.__AGENTWEAVER_REPO_APP_CALLBACK__ === true;
    window.__AGENTWEAVER_REPO_APP_CALLBACK__ = undefined;
    const opener = window.opener;
    if (!callbackWasCaptured || !opener || opener === window) return;

    const callback = {
      type: REPO_APP_CALLBACK_MESSAGE_TYPE,
    };
    if (!isRepoAppConnectedCallbackMessage(callback)) return;
    opener.postMessage(callback, window.location.origin);
    window.close();
  }, []);

  return (
    <div className="v1-auth-page">
      <section className="v1-auth-card">
        <h1>GitHub authorization returned</h1>
        <p className="v1-muted">{message}</p>
      </section>
    </div>
  );
}

function RemoteMcpOAuthCallbackRelay() {
  const { apiCall } = useAuth();
  const [notice, setNotice] = useState<string | null>(null);

  useEffect(() => {
    const onMessage = (event: MessageEvent<unknown>) => {
      const callback = event.data;
      if (event.origin !== window.location.origin ||
          !event.source ||
          event.source === window ||
          !isRemoteMcpOAuthCallbackMessage(callback))
        return;

      setNotice('The Remote MCP provider returned. Checking the authorization with Identity.');
      void apiCall(
        (token, tenantSelector) =>
          gatewayClient.completeRemoteMcpOAuthCallback(token, callback, tenantSelector),
        null,
      ).then(() => {
        setNotice(callback.error === 'access_denied'
          ? 'The Remote MCP authorization was canceled.'
          : 'The Remote MCP authorization was received by Identity.');
      }).catch((reason: unknown) => {
        setNotice(callback.error === 'access_denied' &&
          errorCode(reason) === 'remote_mcp_oauth_consent_denied'
          ? 'The Remote MCP authorization was canceled.'
          : `The Remote MCP authorization could not be completed: ${errorMessage(reason)}`);
      });
    };
    window.addEventListener('message', onMessage);
    return () => window.removeEventListener('message', onMessage);
  }, [apiCall]);

  return notice
    ? <div className="v1-content" role="status" aria-live="polite">{notice}</div>
    : null;
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
  const repoAppCallback = window.location.pathname === REPO_APP_CALLBACK_PATH &&
    window.__AGENTWEAVER_REPO_APP_CALLBACK__ === true;
  const identityPopupCallback = window.location.pathname === '/auth/callback' &&
    window.opener && window.opener !== window;
  return (
    <FluentProvider theme={agentweaverLightTheme}>
      {window.location.pathname === REMOTE_MCP_OAUTH_CALLBACK_PATH
        ? <RemoteMcpOAuthPopupCallbackPage />
        : window.location.pathname === COPILOT_CALLBACK_PATH
        ? <CopilotPopupCallbackPage />
        : repoAppCallback
        ? <RepoAppPopupCallbackPage />
        : identityPopupCallback
          ? <IdentityPopupCallbackPage />
        : (
          <AuthProvider>
            <BrowserRouter>
              <RepoAppCallbackRelay />
              <RemoteMcpOAuthCallbackRelay />
              <AuthenticatedRoutes />
            </BrowserRouter>
          </AuthProvider>
        )}
    </FluentProvider>
  );
}
