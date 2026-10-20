/**
 * Owner-facing v1 browser application for Gateway-backed projects and runs.
 * The route tree owns navigation; AuthContext owns Broker identity, while page
 * components load and mutate project, knowledge, and run data through the Gateway.
 */
import '@xyflow/react/dist/style.css';
import { ReactFlow, Background, Controls, MiniMap } from '@xyflow/react';
import '../components/shell/shell.css';
import './styles.css';
import { FluentProvider, Button, Badge, Field, Input, Textarea, TabList, Tab, MessageBar, MessageBarBody, Radio, RadioGroup, Text } from '@fluentui/react-components';
import {
  AddRegular,
  ArrowClockwiseRegular,
  Brain24Regular,
  Chat24Regular,
  Flowchart24Regular,
} from '@fluentui/react-icons';
import { agentweaverLightTheme } from '../theme';
import { PageSection } from '../components/ui/PageSection';
import { Tile, TileGrid } from '../components/ui/TileGrid';
import { AppDialog } from '../components/ui/AppDialog';
import { EmptyState } from '../components/ui/States';
import { SignInView } from '../pages/SignInPage';
import { RetainedShell as Shell } from './RetainedShell';
import { iconForRole, workflowNodeTypes } from '../components/WorkflowGraphPanel';
import type { WorkflowNodeData } from '../components/WorkflowGraphPanel';
import { ConfigurationFields } from './ConfigurationFields';
import { needsModelCredential } from './modelCredential';
import { ConfigurationHistory } from './ConfigurationHistory';
import { RemoteMcpConnectionPanel } from './RemoteMcpConnectionPanel';
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
import type { FormEvent } from 'react';
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { AuthProvider, useAuth } from './AuthContext';
import { ConsentScreen } from './ConsentScreen';
import {
  parseAuthorizationCallbackParameters,
} from './authProtocol';
import { gatewayClient, GatewayError } from './api';
import { CopilotPopupCallbackPage, CopilotUserConnectionPanel } from './CopilotUserConnectionPanel';
import { SkillsMarketplacePanel } from './SkillsMarketplacePanel';
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
import { errorCode, errorMessage } from './errors';
import { ErrorNotice, Loading, PageHeading, Panel } from './pageParts';
import { rememberRun } from './recentRuns';
import { DecisionList } from './DecisionView';
import { NewChatComposer } from './NewChatComposer';
import { PlatformComponentsTable } from './PlatformComponentsTable';
import { isRunFinished, runState } from './runState';
import { DashboardPage } from './DashboardPage';
import { OverviewPage } from './OverviewPage';
import { OrchestrationsPage } from './OrchestrationsPage';
import { AgentsView } from './AgentsView';
import { accessSummary, permissionsForProject, useAuthorizationContext, useProjectConfiguration, useProjectSummary } from './projectData';
import { useBrowserRunList } from './runListData';
import { CONFIGURATION_SECTIONS, SETTINGS_SECTIONS, settingsSection } from './settingsSections';
import { AccessSection, DangerZoneSection, GeneralSection, RepositoryFields, RunLimitsFields } from './ProjectSettingsSections';

/** Checks that a callback or response still refers to the selected project/run pair. */
function isSameBinding(
  actual: RunBinding | null | undefined,
  expected: RunBinding | null,
): boolean {
  return actual?.projectId === expected?.projectId && actual?.runId === expected?.runId;
}

/** Run page tabs in display order. Chat is first, matching the 0.x run page. */
const RUN_TAB_VALUES = ['chat', 'topology', 'approvals', 'activity', 'changes', 'usage', 'selection'] as const;
/** URL-backed views available on the run page. */
type RunTab = typeof RUN_TAB_VALUES[number];
/** Maximum lifetime for a GitHub App setup popup. */
const REPO_APP_POPUP_TIMEOUT_MS = 10 * 60 * 1000;
/** Poll period while waiting for the popup's authorization result. */
const REPO_APP_POPUP_POLL_INTERVAL_MS = 500;

/** Narrows the URL's view query to a supported run tab. */
function isRunTab(value: string | null): value is RunTab {
  return value !== null && RUN_TAB_VALUES.includes(value as RunTab);
}

/** Starts Broker authorization and explains how the Web session is restored. */
function SignInScreen() {
  const { busy, error, configurationError, authorize } = useAuth();
  return (
    <SignInView
      heading="Sign in"
      sessionError={configurationError}
      error={error}
      action={<Button
          appearance="primary"
          disabled={busy || Boolean(configurationError)}
          onClick={() => void authorize(null)}
        >
          {busy ? 'Signing in…' : 'Sign in'}
        </Button>}
    />
  );
}

/** Splits `owner/name` or a github.com URL into repository parts, or returns null. */
function parseRepository(value: string): { owner: string; name: string } | null {
  const match = value.trim().replace(/^https?:\/\/github\.com\//i, '').replace(/\.git$/i, '').match(/^([\w.-]+)\/([\w.-]+)\/?$/);
  return match ? { owner: match[1], name: match[2] } : null;
}

/**
 * Lists the owner's projects and creates new ones. "GitHub repository" creates
 * the project, then saves the repository in its configuration. That second
 * write needs access to the new project, so a caller without a grant gets the
 * project without the repository and a note to add it later.
 */
function ProjectsPage() {
  const { apiCall } = useAuth();
  const navigate = useNavigate();
  const [projects, setProjects] = useState<ProjectSummary[]>([]);
  const [projectName, setProjectName] = useState('');
  const [startFrom, setStartFrom] = useState<'blank' | 'github'>('blank');
  const [repositoryInput, setRepositoryInput] = useState('');
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [createOpen, setCreateOpen] = useState(false);
  const authorization = useAuthorizationContext();

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

  const repository = startFrom === 'github' ? parseRepository(repositoryInput) : null;
  const createDisabled = busy || !projectName.trim() || (startFrom === 'github' && !repository);

  const create = async (event: FormEvent) => {
    event.preventDefault();
    if (createDisabled) return;
    setBusy(true);
    setError(null);
    try {
      const created = await apiCall(
        (token, tenantSelector) => gatewayClient.createProject(token, projectName.trim(), tenantSelector),
        null,
      );
      let message = `Project ${created.name} was created.`;
      if (repository) {
        try {
          await apiCall(async (token, tenantSelector) => {
            const current = await gatewayClient.getProjectConfiguration(token, created.projectId, tenantSelector);
            const sourceControl = { ...(current.configuration.sourceControl ?? {}), repository, authMode: 'githubApp' as const };
            return gatewayClient.updateProjectConfiguration(
              token, created.projectId, current.revision, { ...current.configuration, sourceControl }, tenantSelector,
            );
          }, null);
          message += ` It uses ${repository.owner}/${repository.name}.`;
        } catch {
          message += ` The repository was not saved. Add it in Settings > Repository after you have access.`;
        }
      }
      setProjectName('');
      setRepositoryInput('');
      setStartFrom('blank');
      setNotice(message);
      setCreateOpen(false);
      await load();
    } catch (reason) {
      setError(reason);
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <PageHeading
        title="Projects"
        description="Create a workspace for the team, skills, workflows, runs, and approvals for your work."
        actions={<Button appearance="primary" icon={<AddRegular />} onClick={() => setCreateOpen(true)}>Create project</Button>}
      />
      {error != null && <ErrorNotice code={errorCode(error)}>{errorMessage(error)}</ErrorNotice>}
      {notice && <MessageBar intent="info"><MessageBarBody>{notice}</MessageBarBody></MessageBar>}
      {loading ? <Loading label="Loading projects" /> : projects.length === 0 ? (
            <EmptyState title="No projects yet" description="Create a project or ask a tenant administrator for access to an existing project." />
          ) : (
            <TileGrid aria-label="Projects">
              {projects.map((project) => (
                <Tile
                  key={project.projectId}
                  primary={project.name}
                  secondary={project.projectId}
                  badges={<Badge appearance="tint" size="small" color={project.state === 'active' ? 'success' : 'subtle'}>{project.state}</Badge>}
                  meta={<><Badge appearance="outline" size="small">Revision {project.revision}</Badge>{authorization.data && <Badge appearance="outline" size="small">{accessSummary(permissionsForProject(authorization.data, project.projectId))}</Badge>}</>}
                  actions={<Button appearance="primary" size="small" onClick={() => navigate(`/projects/${encodeURIComponent(project.projectId)}`)}>Open</Button>}
                  actionsAlwaysVisible
                  onClick={() => navigate(`/projects/${encodeURIComponent(project.projectId)}`)}
                />
              ))}
            </TileGrid>
          )}
      <AppDialog open={createOpen} onOpenChange={setCreateOpen} title="Create a project">
          <form className="v1-form" onSubmit={(event) => void create(event)}>
            {error != null && <ErrorNotice>{errorMessage(error)}</ErrorNotice>}
            <Field label="Project name">
              <Input value={projectName} onChange={(_, data) => setProjectName(data.value)} maxLength={120} />
            </Field>
            <Field label="Start from">
              <RadioGroup layout="horizontal" value={startFrom} onChange={(_, data) => setStartFrom(data.value as 'blank' | 'github')}>
                <Radio value="blank" label="Blank project" />
                <Radio value="github" label="GitHub repository" />
              </RadioGroup>
            </Field>
            {startFrom === 'github' && (
              <Field
                label="Repository"
                hint="For example, octo/agentweaver or its github.com address."
                validationState={repositoryInput.trim() && !repository ? 'error' : 'none'}
                validationMessage={repositoryInput.trim() && !repository ? 'Enter the repository as owner/name.' : undefined}
              >
                <Input value={repositoryInput} onChange={(_, data) => setRepositoryInput(data.value)} />
              </Field>
            )}
            <Button appearance="primary" type="submit" disabled={createDisabled}>
              {busy ? 'Creating…' : 'Create project'}
            </Button>
            <p className="v1-muted">Creating a project does not make you its owner. If you can't open it afterward, ask a tenant administrator for access.</p>
            <Button appearance="transparent" onClick={() => setCreateOpen(false)}>Cancel</Button>
          </form>
      </AppDialog>
    </>
  );
}

/** Manages a project's GitHub App installation and repository connection. */
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
        text: 'GitHub returned to Agentweaver. Checking the connection status.',
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
        text: 'GitHub authorization timed out before it started. Start the connection again.',
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
          text: 'The GitHub popup closed before the request finished. Start the connection again.',
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

/** Edits project configuration and reports rejected or stale updates. */
function ProjectConfigurationPage({ view = 'settings' }: { view?: 'settings' | 'skills' | 'agents' }) {
  const { projectId = '' } = useParams();
  const { session, apiCall } = useAuth();
  const [searchParams] = useSearchParams();
  const section = settingsSection(searchParams.get('section'));
  const project = useProjectSummary(view === 'settings' ? projectId : '');
  const [versioned, setVersioned] = useState<VersionedProjectConfiguration | null>(null);
  const [editor, setEditor] = useState('');
  const [configurationDraft, setConfigurationDraft] = useState<ProjectConfiguration | null>(null);
  const [loading, setLoading] = useState(true);
  const [savingScope, setSavingScope] = useState<string | null>(null);
  const [skillAssignmentPending, setSkillAssignmentPending] = useState(false);
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
        throw new Error('The server returned configuration for a different project.');
      setVersioned(result);
      setEditor(JSON.stringify(result.configuration, null, 2));
      setConfigurationDraft(result.configuration);
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

  /** Saves a full configuration document against the loaded revision. Resolves true on success. */
  const persist = async (configuration: ProjectConfiguration): Promise<boolean> => {
    const scope = requestScope;
    if (!versioned || loadedScope !== scope || versioned.projectId !== projectId || skillAssignmentPending) return false;
    if (needsModelCredential(configuration, versioned.configuration.modelSelection)) return false;
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
      if (activeScope.current !== scope) return false;
      if (updated.projectId !== projectId)
        throw new Error('The server returned an update for a different project.');
      setVersioned(updated);
      setEditor(JSON.stringify(updated.configuration, null, 2));
      setConfigurationDraft(updated.configuration);
      setNotice(`Configuration revision ${updated.revision} saved. Runs that already started keep their settings.`);
      setNoticeScope(scope);
      return true;
    } catch (reason) {
      if (activeScope.current !== scope) return false;
      setError(reason);
      setErrorScope(scope);
      return false;
    } finally {
      if (activeScope.current === scope)
        setSavingScope(null);
    }
  };

  const save = async (event: FormEvent) => {
    event.preventDefault();
    let configuration: ProjectConfiguration;
    try {
      const parsed: unknown = JSON.parse(editor);
      if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed))
        throw new Error('Configuration must be a JSON object.');
      configuration = parsed as ProjectConfiguration;
    } catch (reason) {
      setError(reason);
      setErrorScope(requestScope);
      return;
    }
    await persist(configuration);
  };

  const currentError = errorScope === requestScope ? error : null;
  const currentNotice = noticeScope === requestScope ? notice : null;
  const currentConfiguration = loadedScope === requestScope && versioned?.projectId === projectId
    ? versioned
    : null;
  const currentLoading = loading || loadedScope !== requestScope;
  const skillAgents = useMemo(() => {
    if (!currentConfiguration) return [];
    const names = new Map(currentConfiguration.configuration.agentCharters.map((item) => [item.agentId, item.name]));
    return currentConfiguration.configuration.casting.map((item) => ({
      agentId: item.agentId,
      displayName: names.get(item.agentId) || item.agentId,
    }));
  }, [currentConfiguration]);
  const skillActions = useMemo(() => ({
    listSources: (scope: string) => apiCall(
      (token, tenantSelector) => gatewayClient.listMarketplaceSources(token, scope, tenantSelector),
      null,
    ),
    createSource: (scope: string, request: Parameters<typeof gatewayClient.createMarketplaceSource>[2]) =>
      apiCall((token, tenantSelector) =>
        gatewayClient.createMarketplaceSource(token, scope, request, tenantSelector), null),
    updateSource: (
      scope: string,
      sourceId: string,
      request: Parameters<typeof gatewayClient.updateMarketplaceSource>[3],
    ) => apiCall((token, tenantSelector) =>
      gatewayClient.updateMarketplaceSource(token, scope, sourceId, request, tenantSelector), null),
    removeSource: (scope: string, sourceId: string, expectedRevision: number) =>
      apiCall((token, tenantSelector) =>
        gatewayClient.removeMarketplaceSource(token, scope, sourceId, expectedRevision, tenantSelector), null),
    browseSource: (
      scope: string,
      sourceId: string,
      request: Parameters<typeof gatewayClient.browseMarketplaceSource>[3],
    ) => apiCall((token, tenantSelector) =>
      gatewayClient.browseMarketplaceSource(token, scope, sourceId, request, tenantSelector), null),
    previewMarketplaceSkill: (
      scope: string,
      sourceId: string,
      request: Parameters<typeof gatewayClient.previewMarketplaceSkillContent>[3],
    ) => apiCall((token, tenantSelector) =>
      gatewayClient.previewMarketplaceSkillContent(token, scope, sourceId, request, tenantSelector), null),
    importMarketplaceSkill: (
      scope: string,
      sourceId: string,
      request: Parameters<typeof gatewayClient.importMarketplaceSkillContent>[3],
    ) => apiCall((token, tenantSelector) =>
      gatewayClient.importMarketplaceSkillContent(token, scope, sourceId, request, tenantSelector), null),
    previewSkillContent: (candidate: Parameters<typeof gatewayClient.previewSkillContent>[1]) =>
      apiCall((token, tenantSelector) =>
        gatewayClient.previewSkillContent(token, candidate, tenantSelector), null),
    importSkillContent: (
      scope: string,
      request: Parameters<typeof gatewayClient.importSkillContent>[2],
    ) => apiCall((token, tenantSelector) =>
      gatewayClient.importSkillContent(token, scope, request, tenantSelector), null),
    listSkillInventory: (
      scope: string,
      request: Parameters<typeof gatewayClient.listProjectSkillInventory>[2],
    ) => apiCall((token, tenantSelector) =>
      gatewayClient.listProjectSkillInventory(token, scope, request, tenantSelector), null),
    updateSkillAssignment: (
      scope: string,
      skillId: string,
      request: Parameters<typeof gatewayClient.updateSkillAssignment>[3],
    ) => apiCall((token, tenantSelector) =>
      gatewayClient.updateSkillAssignment(token, scope, skillId, request, tenantSelector), null),
  }), [apiCall]);
  const assignmentDisabledReason = skillAssignmentPending
    ? 'Wait for the current assignment update to finish.'
    : savingScope === requestScope
      ? 'Wait for the current configuration save to finish.'
      : currentConfiguration && editor !== JSON.stringify(currentConfiguration.configuration, null, 2)
        ? 'Save or reload the edited project configuration before assigning a skill.'
        : null;

  const busy = savingScope === requestScope || skillAssignmentPending;
  const draftInSync = Boolean(configurationDraft && editor === JSON.stringify(configurationDraft, null, 2));
  const fieldProps = configurationDraft ? {
    configuration: configurationDraft,
    readOnly: busy || !draftInSync,
    onChange: (next: ProjectConfiguration) => { setConfigurationDraft(next); setEditor(JSON.stringify(next, null, 2)); },
  } : null;
  const saveDisabled = busy || !editor.trim()
    || Boolean(currentConfiguration && editor === JSON.stringify(currentConfiguration.configuration, null, 2))
    || Boolean(configurationDraft && draftInSync && currentConfiguration
      && needsModelCredential(configurationDraft, currentConfiguration.configuration.modelSelection));
  const statusMessages = (
    <>
      {currentError != null && <ErrorNotice code={errorCode(currentError)}>{errorMessage(currentError)}</ErrorNotice>}
      {currentNotice && <MessageBar intent="success"><MessageBarBody>{currentNotice}</MessageBarBody></MessageBar>}
    </>
  );

  if (view === 'agents') return (
    <>
      <PageHeading title="Agents" description="The cast working on this project." page="Agents" />
      {statusMessages}
      {currentLoading ? <Loading label="Loading agents" /> : currentConfiguration && (
        <AgentsView projectId={projectId} configuration={currentConfiguration.configuration} readOnly={busy} onSave={persist} />
      )}
    </>
  );

  if (view === 'skills') return (
    <>
      <PageHeading title="Skills" description="Reusable instructions your agents can use. Browse a marketplace, preview a skill, then import and assign it." page="Skills" />
      {statusMessages}
      {currentLoading ? <Loading label="Loading skills" /> : currentConfiguration && (
        <SkillsMarketplacePanel
          projectId={projectId}
          configurationRevision={currentConfiguration.revision}
          assignments={currentConfiguration.configuration.skills}
          agents={skillAgents}
          actions={session ? skillActions : null}
          assignmentDisabledReason={assignmentDisabledReason}
          onAssignmentPendingChange={setSkillAssignmentPending}
          onConfigurationChanged={(updated) => {
            if (activeScope.current !== requestScope || updated.projectId !== projectId) return;
            setVersioned(updated);
            setEditor(JSON.stringify(updated.configuration, null, 2));
            setConfigurationDraft(updated.configuration);
            setError(null);
            setErrorScope(null);
          }}
        />
      )}
    </>
  );

  const base = `/projects/${encodeURIComponent(projectId)}/settings`;
  return (
    <>
      <PageHeading title="Project settings" description="Configure how this project runs." page="Settings" />
      <div className="v1-settings-layout">
        <nav className="v1-settings-rail" aria-label="Settings sections">
          {SETTINGS_SECTIONS.map((item) => (
            <Link key={item.key} to={`${base}?section=${item.key}`}
              className={item.key === 'danger' ? 'v1-settings-rail__danger' : undefined}
              aria-current={item.key === section ? 'page' : undefined}>
              {item.label}
            </Link>
          ))}
        </nav>
        <div className="v1-stack v1-settings-pane">
          {statusMessages}
          {section === 'general' && (project.data
            ? <GeneralSection key={project.data.revision} project={project.data} onUpdated={project.replace} />
            : project.error != null ? <ErrorNotice code={errorCode(project.error)}>{errorMessage(project.error)}</ErrorNotice> : <Loading />)}
          {section === 'access' && <AccessSection projectId={projectId} />}
          {section === 'danger' && (project.data
            ? <DangerZoneSection project={project.data} onUpdated={project.replace} />
            : project.error != null ? <ErrorNotice code={errorCode(project.error)}>{errorMessage(project.error)}</ErrorNotice> : <Loading />)}
          {section === 'connections' && (
            <>
              <PageSection title="Connections" description="Connect the accounts and tools this project needs." />
              <ProjectRepoAppConnectionPanel />
              <CopilotUserConnectionPanel projectId={projectId} />
              <RemoteMcpConnectionPanel key={requestScope} projectId={projectId} configurationRevision={currentConfiguration?.revision ?? null} />
            </>
          )}
          {section === 'history' && (currentLoading ? <Loading /> : currentConfiguration && (
            <ConfigurationHistory key={`${requestScope}/${currentConfiguration.revision}`}
              projectId={projectId} currentRevision={currentConfiguration.revision} />
          ))}
          {CONFIGURATION_SECTIONS.has(section) && (currentLoading ? <Loading /> : currentConfiguration && fieldProps && (
            <form className="v1-form" onSubmit={(event) => void save(event)}>
              {section === 'repository' && <RepositoryFields {...fieldProps} />}
              {section === 'models' && <ConfigurationFields {...fieldProps} part="models" savedSelection={currentConfiguration.configuration.modelSelection} />}
              {section === 'components' && <ConfigurationFields {...fieldProps} part="components" savedSelection={currentConfiguration.configuration.modelSelection} />}
              {section === 'limits' && <RunLimitsFields {...fieldProps} />}
              {section === 'advanced' && (
                <PageSection title="Advanced" description="Edit the full configuration as JSON. Keep every field to preserve other settings and saved references. Do not enter passwords or tokens.">
                  <Field label="Configuration JSON">
                    <Textarea
                      className="v1-json-editor"
                      value={editor}
                      onChange={(_, data) => { if (!skillAssignmentPending) setEditor(data.value); }}
                      readOnly={skillAssignmentPending}
                      resize="vertical"
                      rows={24}
                      spellCheck={false}
                    />
                  </Field>
                  {!draftInSync && <p className="v1-muted">Save or reload before you use the other sections.</p>}
                </PageSection>
              )}
              <div className="v1-actions">
                <Button appearance="primary" type="submit" disabled={saveDisabled}>
                  {savingScope === requestScope ? 'Saving…' : 'Save changes'}
                </Button>
                <Button appearance="secondary" type="button" disabled={busy} onClick={() => void load()}>Reload</Button>
                <Text size={200}>Revision {currentConfiguration.revision}</Text>
              </div>
            </form>
          ))}
        </div>
      </div>
    </>
  );
}

/** Validates that one returned knowledge record is bound to the requested project and agent. */
function isKnowledgeRecordInScope(value: unknown, projectId: string, agentId: string): boolean {
  return typeof value === 'object' && value !== null &&
    (value as { projectId?: unknown }).projectId === projectId &&
    (value as { agentId?: unknown }).agentId === agentId &&
    typeof (value as { recordId?: unknown }).recordId === 'string' &&
    (value as { recordId: string }).recordId.length > 0;
}

/** Rejects malformed or cross-agent knowledge lists before they reach the page state. */
function assertKnowledgeRecordsInScope(records: unknown, projectId: string, agentId: string): asserts records is KnowledgeRecord[] {
  if (!Array.isArray(records) || records.some((record) => !isKnowledgeRecordInScope(record, projectId, agentId)))
    throw new Error('Knowledge returned records outside the exact project and agent scope.');
}

/** Verifies a mutation receipt matches the project and agent whose record was changed. */
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

/** Ensures an imported or exported knowledge bundle cannot cross project/agent scope. */
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

/** Lists, edits, versions, and transfers knowledge records within a selected project agent. */
function KnowledgePage() {
  const { projectId = '' } = useParams();
  const { session, apiCall, authorize, ensureSession } = useAuth();
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
  const recentRuns = useBrowserRunList(projectId);
  const knowledgeConfiguration = useProjectConfiguration(projectId);
  /** Recent runs in this browser, plus the run from the URL so a deep link keeps its choice. */
  const runOptions = useMemo(() => {
    const options = recentRuns.items.map((item) => ({ value: item.runId, label: `${item.runId} · ${runState(item.executionState).label}` }));
    const current = searchParams.get('runId');
    if (current && !options.some((option) => option.value === current)) options.unshift({ value: current, label: current });
    return options;
  }, [recentRuns.items, searchParams]);
  /** Agents from the project configuration, plus the agent from the URL (for example, a session opened from Topology). */
  const agentOptions = useMemo(() => {
    const options = (knowledgeConfiguration.data?.configuration.agentCharters ?? [])
      .map((agent) => ({ value: agent.agentId, label: `${agent.name} · ${agent.role}` }));
    const current = searchParams.get('agentId');
    if (current && !options.some((option) => option.value === current)) options.unshift({ value: current, label: current });
    return options;
  }, [knowledgeConfiguration.data, searchParams]);
  const correctlyScoped = isSameBinding(session?.binding, binding);
  const silentBind = useSilentRunSession(binding, correctlyScoped, ensureSession);
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
      setNotice(`Record created (${result.status}). The list shows its saved state.`);
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
      setNotice(`Proposal updated (${result.status}). The list shows its current state.`);
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
      setNotice(`Record archived (${result.status}). The list shows its current state.`);
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
      setNotice(`Correction saved (${result.status}). The list shows the corrected record.`);
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
      setNotice(`Decision approved (${result.status}). The list shows its trust state.`);
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
      setNotice(`Decision replaced (${result.status}). The list shows the new link.`);
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
      setNotice(`Revision restored (${result.status}). The list shows the restored record.`);
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
      setNotice(`Imported ${result.records.length} record(s). The list shows their saved state.`);
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
      <PageHeading title="Memories" description="What your agents remember and decide. Choose a run and an agent to see their records." page="Memories" />
      {currentError != null && <ErrorNotice code={errorCode(currentError)}>{errorMessage(currentError)}</ErrorNotice>}
      {currentNotice && <MessageBar intent="info"><MessageBarBody>{currentNotice}</MessageBarBody></MessageBar>}
      <Panel title="Show memories for">
        {runOptions.length === 0 ? (
          <EmptyState title="No runs yet" description="Memories belong to a run. Open a run from Orchestrations, then come back here."
            action={<Link className="v1-link-button" to={`/projects/${encodeURIComponent(projectId)}/orchestrations`}>Open Orchestrations</Link>} />
        ) : (
          <form className="v1-form v1-inline-form" onSubmit={setScope}>
            <Field label="Run">
              <select className="v1-select" value={runId} onChange={(event) => setRunId(event.target.value)}>
                <option value="">Choose a run</option>
                {runOptions.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
              </select>
            </Field>
            <Field label="Agent">
              <select className="v1-select" value={agentId} onChange={(event) => setAgentId(event.target.value)}>
                <option value="">Choose an agent</option>
                {agentOptions.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
              </select>
            </Field>
            <Button appearance="primary" type="submit" disabled={!runId.trim() || !agentId.trim()}>Show memories</Button>
          </form>
        )}
        {binding && !correctlyScoped && (silentBind === 'failed' ? (
          <div className="v1-scope-action">
            <p className="v1-muted">Each run needs its own sign-in. Sign in again to read this run's memories.</p>
            <Button appearance="primary" onClick={() => void authorize(binding)}>Sign in for this project and run</Button>
          </div>
        ) : <Loading label="Opening this run's memories…" />)}
      </Panel>
      {binding && correctlyScoped && agentId.trim() && (
        <>
          <TabList selectedValue={kind} onTabSelect={(_, data) => { setPage(1); setKind(String(data.value)); }} aria-label="Memory kind">
            <Tab value="">All</Tab>
            <Tab value="memory">Memories</Tab>
            <Tab value="decision">Decisions</Tab>
            <Tab value="proposal">Proposals</Tab>
            <Tab value="sessionContext">Session context</Tab>
          </TabList>
          <Panel title="Search" actions={<Button appearance="subtle" icon={<ArrowClockwiseRegular />} onClick={() => void load()}>Refresh</Button>}>
            <form className="v1-form v1-inline-form" onSubmit={(event) => { event.preventDefault(); void load(); }}>
              <Field label="Search text"><Input value={query} onChange={(_, data) => { setPage(1); setQuery(data.value); }} /></Field>
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
            {loading || recordsScope !== requestScope ? <Loading /> : currentRecords.length === 0 ? <p className="v1-muted">No matching records.</p> : (
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

/** Finds a session's depth by following parent links in the current session tree. */
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

/** Converts the session tree and run statuses into the nodes and edges used by React Flow. */
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
      type: 'workflow',
      position: { x: depth * 300, y: index * 150 },
      data: {
        def: { key: node.kind, label: node.kind, roleDescription: node.kind, Icon: iconForRole(node.kind) },
        state: {
          status: status?.activity === 'busy' ? 'started' : node.lifecycle === 'completed' ? 'completed' : 'pending',
          message: description,
        },
        statusLabelOverride: status ? `${status.activity} · ${status.lifecycle}` : 'Status unavailable',
        agentName: node.identity.sessionId,
        dir: 'LR',
      } satisfies WorkflowNodeData,
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

/** Produces a compact activity label for a Broker journal event. */
function eventSummary(event: SessionEventEnvelope): string {
  const payload = event.payload;
  if (event.kind === 'turn') {
    const role = typeof payload.role === 'string' ? payload.role : 'turn';
    return `${role} content is stored separately. This page cannot show it.`;
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

/** Renders ordered session events with their compact event summaries. */
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

/** Shows the current run snapshot and status-specific blocking information. */
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

/** Combines the run journal and status snapshot for the activity tab. */
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

/** One locally queued chat message awaiting a Gateway response. */
interface PendingMessage {
  id: string;
  from: string;
  to: string;
  text: string;
  status: string;
}

/**
 * Renders run chat. Messages go to `targetSessionId`: the coordinator unless the user
 * selected another active session in the run tree. The v1 message API delivers between
 * sessions in a run, so the page sends through another active session (the coordinator
 * when it is not the recipient). Pending messages show until the journal reports them.
 */
function ChatView({
  projectId,
  runId,
  nodes,
  targetSessionId,
  journalEvents,
  apiCall,
  snapshotBusy,
}: {
  projectId: string;
  runId: string;
  nodes: SessionTreeNode[];
  targetSessionId: string;
  journalEvents: SessionEventEnvelope[];
  apiCall: AuthReturn['apiCall'];
  snapshotBusy: boolean;
}) {
  const activeNodes = useMemo(() => nodes.filter((node) => node.lifecycle === 'active'), [nodes]);
  const [pending, setPending] = useState<PendingMessage[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const target = activeNodes.find((node) => node.identity.sessionId === targetSessionId);
  const sender = activeNodes.find((node) => node.kind === 'coordinator' && node !== target) ??
    activeNodes.find((node) => node !== target);
  const targetName = target ? sessionName(target) : 'Coordinator';

  const send = async (text: string, idempotencyKey: string) => {
    if (!target || !sender) return;
    setNotice(null);
    const result = await apiCall((token, tenantSelector) => gatewayClient.sendMessage(
      token, projectId, runId, sender.identity.sessionId, target.identity.sessionId, text, idempotencyKey, 'immediate', tenantSelector,
    ), { projectId, runId });
    setPending((items) => [...items, { id: result.ownerMessageId, from: sender.identity.sessionId, to: targetName, text, status: result.status }]);
    setNotice('Message sent. It shows in the activity list when the agent receives it.');
  };

  return (
    <div className="v1-stack">
      {notice && <MessageBar intent="info"><MessageBarBody>{notice}</MessageBarBody></MessageBar>}
      <Panel title="Activity" description="Events from this run, oldest first. Message text is not stored in the event list.">
        <JournalList events={journalEvents} />
      </Panel>
      <Panel title={`Message the ${targetName.toLowerCase()}`} description="Choose another session in the run tree to message it instead.">
        {!target || !sender ? (
          <p className="v1-muted">You can send a message when the run has another active session.</p>
        ) : (
          <NewChatComposer onSubmit={send} blocked={snapshotBusy} />
        )}
        {pending.length > 0 && (
          <ol className="v1-chat-list" aria-label="Messages sent from this page">
            {pending.map((message) => (
              <li key={message.id}>
                <div><Badge appearance="outline">{message.status}</Badge><span>To {message.to}</span></div>
                <p>{message.text}</p>
                <code>Shown from this page only. Reloading clears it.</code>
              </li>
            ))}
          </ol>
        )}
      </Panel>
    </div>
  );
}

/** The shared authentication hook's return type, used by callback-bearing view props. */
type AuthReturn = ReturnType<typeof useAuth>;

/** Shows the settings this run started with. Later configuration changes do not affect it. */
function SelectionView({ selection }: { selection: EffectiveRunSelection | null }) {
  if (!selection) return <p className="v1-muted">The service returned no settings for this run.</p>;
  return (
    <div className="v1-stack">
      <p className="v1-muted">These are the settings this run started with. Changing project settings does not change them.</p>
      <dl className="v1-detail-list">
        <div><dt>Model</dt><dd><code>{selection.modelSelection.reference}</code></dd></div>
        <div><dt>Project configuration revision</dt><dd>{selection.projectConfigurationRevision}</dd></div>
        <div><dt>Platform defaults revision</dt><dd>{selection.platformRuntimeRevision}</dd></div>
      </dl>
      <PlatformComponentsTable components={selection.providers} />
    </div>
  );
}

/** Displays run-level and per-agent usage, preserving missing measurements as unavailable. */
function UsageView({ usage }: { usage: UsageRunTotals | null }) {
  if (!usage) return <p className="v1-muted">No usage was reported for this run.</p>;
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

/** Builds a stable key for one installation/repository pair returned by the Broker. */
function repoAppRepositoryKey(repository: RepoAppRepository): string {
  return `${repository.installationId}/${repository.repositoryId}`;
}

/** Loads Broker repository metadata and pins the selected opaque repository code to this run. */
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
      // A popup message alone is not connection proof: require same-origin and the exact tracked popup,
      // then re-read status from Identity before allowing repository access.
      if (event.origin !== window.location.origin ||
          event.source !== popupRef.current ||
          !isRepoAppConnectedCallbackMessage(event.data))
        return;

      popupRef.current?.close();
      popupRef.current = null;
      setNotice({
        intent: 'info',
        text: 'GitHub returned to Agentweaver. Refreshing repository access.',
      });
      void loadConnection().then((next) => {
        if (next?.state === 'connected' && next.connectionId === appConnectionId)
          void loadRepositories();
        else
          setNotice({
            intent: 'warning',
            text: 'The installation callback was received, but the connection was not confirmed.',
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
      if (!repository) throw new Error('Select a repository from the list.');
      // The Broker-issued code represents the selected installation/repository; do not send
      // the user's repository list or a client-constructed identifier to the Gateway.
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

/**
 * Coordinates the owner snapshots for one project/run, then renders topology, chat,
 * approvals, activity, selection, and usage. Responses are accepted only for the
 * current run binding and generation; mutations wait for refreshed owner state.
 */
function RunPage() {
  const { projectId = '', runId = '' } = useParams();
  const binding = useMemo(() => ({ projectId, runId }), [projectId, runId]);
  const { session, apiCall, authorize, ensureSession, authorizationContext } = useAuth();
  const [searchParams, setSearchParams] = useSearchParams();
  const requestedTab = searchParams.get('view');
  const tab: RunTab = isRunTab(requestedTab) ? requestedTab : 'chat';
  const authorized = isSameBinding(session?.binding, binding);
  const silentBind = useSilentRunSession(binding, authorized, ensureSession);
  const now = useTickingNow(authorized);
  const snapshotScope = useMemo(() => ({
    projectId,
    runId,
    accessToken: session?.accessToken ?? '',
  }), [projectId, runId, session?.accessToken]);
  const activeSnapshotScope = useRef(snapshotScope);
  const tenantId = authorizationContext?.tenantId;
  const runTenantSelector = authorized ? tenantId : undefined;
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
    normalizedParams.set('view', 'chat');
    setSearchParams(normalizedParams, { replace: true });
  }, [requestedTab, searchParams, setSearchParams]);

  useLayoutEffect(() => {
    // Invalidate outstanding reads synchronously when the access-token or route scope changes.
    activeSnapshotScope.current = snapshotScope;
    refreshGeneration.current += 1;
  }, [snapshotScope]);

  const refresh = useCallback(async () => {
    if (!authorized) return;
    const scope = snapshotScope;
    const generation = ++refreshGeneration.current;
    // Every awaited snapshot below is discarded unless both the request generation and
    // the exact token/project/run scope still match.
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
          : 'The run settings belong to a different run.';
      }
      if (usageResult.status === 'fulfilled' &&
          usageResult.value.projectId === projectId && usageResult.value.runId === runId)
        setUsage(usageResult.value);
      else {
        setUsage(null);
        nextErrors.usage = usageResult.status === 'rejected'
          ? errorMessage(usageResult.reason)
          : 'The usage totals belong to a different run.';
      }
      setSnapshotErrors(nextErrors);
      setLoadedSnapshotScope(scope);
      setSnapshotBusy(false);
      return;
    }
    const currentRun = runResult.value;
    // Fail closed if the owner returns a valid-looking snapshot for another route binding.
    if (currentRun.projectId !== projectId || currentRun.runId !== runId) {
      setRunStatus(null);
      setTree(null);
      setStatuses({});
      setDecisions({});
      setSelection(null);
      setUsage(null);
      setSnapshotErrors({ run: 'The service returned a different run. Refresh to try again.' });
      setLoadedSnapshotScope(scope);
      setSnapshotBusy(false);
      return;
    }
    setRunStatus(currentRun);
    // Only a confirmed snapshot for this exact run goes into the browser's recent-run list.
    if (tenantId)
      rememberRun(tenantId, { projectId, runId, executionState: currentRun.executionState });
    if (selectionResult.status === 'fulfilled' &&
        selectionResult.value.projectId === projectId && selectionResult.value.runId === runId)
      setSelection(selectionResult.value);
    else {
      setSelection(null);
      nextErrors.selection = selectionResult.status === 'rejected'
        ? errorMessage(selectionResult.reason)
        : 'The run settings belong to a different run.';
    }
    if (usageResult.status === 'fulfilled' &&
        usageResult.value.projectId === projectId && usageResult.value.runId === runId)
      setUsage(usageResult.value);
    else {
      setUsage(null);
      nextErrors.usage = usageResult.status === 'rejected'
        ? errorMessage(usageResult.reason)
        : 'The usage totals belong to a different run.';
    }

    try {
      const nextTree = await apiCall(
        (token, tenantSelector) => gatewayClient.getSessionTree(
          token, projectId, runId, currentRun.rootSessionId, tenantSelector,
        ),
        binding,
      );
      if (!isCurrent()) return;
      // The tree must agree with the root and route scope before its nodes are rendered or queried.
      if (nextTree.rootSessionId !== currentRun.rootSessionId ||
          nextTree.nodes.some((node) =>
            node.identity.projectId !== projectId || node.identity.runId !== runId)) {
        throw new Error('The session tree belongs to a different run.');
      }
      setTree(nextTree);
      const sessionResults = await Promise.allSettled(nextTree.nodes.map((node) =>
        apiCall((token, tenantSelector) => gatewayClient.getSessionStatus(
          token, projectId, runId, node.identity.sessionId, tenantSelector,
        ), binding)));
      if (!isCurrent()) return;
      const nextStatuses: Record<string, SessionStatusSnapshot> = {};
      // Fetch decision state only for the root and sessions with blockers; this limits reads
      // while still allowing every actionable request to be matched to its owner state.
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
            : 'The status belongs to a different session.';
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
  }, [apiCall, authorized, binding, projectId, runId, selectedSession, snapshotScope, tenantId]);

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
        <PageHeading title="Orchestration" description={`Run ${runId}`} page="Orchestrations" />
        {silentBind === 'failed' ? (
          <Panel title="Sign in to open this run">
            <p>Each run needs its own sign-in. Sign in again to open this run.</p>
            <Button appearance="primary" onClick={() => void authorize(binding)}>Sign in for this project and run</Button>
          </Panel>
        ) : <Loading label="Opening this run…" />}
      </>
    );
  }

  const snapshotIsCurrent = loadedSnapshotScope === snapshotScope;
  const currentRunStatus = snapshotIsCurrent ? runStatus : null;
  const currentTree = snapshotIsCurrent ? tree : null;
  const currentStatuses = snapshotIsCurrent ? statuses : {};
  const currentDecisions = snapshotIsCurrent ? decisions : {};
  const pendingDecisionCount = Object.values(currentStatuses).reduce((total, status) => total + (status?.blockers.length ?? 0), 0);
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
    // The owner uses the execution fence to reject stale lifecycle changes; each user action
    // also receives a unique idempotency key so a transport retry cannot repeat the mutation.
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

  const rootNode = currentTree?.nodes.find((node) => node.identity.sessionId === currentTree.rootSessionId);
  const startedAt = rootNode ? Date.parse(rootNode.createdAt) : Number.NaN;
  const finished = isRunFinished(currentRunStatus?.executionState);
  const streamLabel = journal.connected ? 'Live' : journal.loading ? 'Connecting' : 'Reconnecting';
  const chatTarget = nodeById.get(selectedSession)?.lifecycle === 'active'
    ? selectedSession
    : currentTree?.rootSessionId ?? '';
  // Only the settings and usage tabs read these snapshots, so their errors show in those tabs.
  const { selection: selectionError, usage: usageError, ...pageErrors } = currentSnapshotErrors;

  return (
    <>
      <PageHeading
        title="Orchestration"
        description={Number.isNaN(startedAt) ? `Run ${runId}` : `Run ${runId} · Started ${new Date(startedAt).toLocaleString()}`}
        page="Orchestrations"
        actions={<Button appearance="secondary" aria-label="Refresh run" icon={<ArrowClockwiseRegular />} disabled={snapshotBusy} onClick={() => void refreshRef.current()}>Refresh</Button>}
      />
      {currentSnapshotMessage && <MessageBar intent="info"><MessageBarBody>{currentSnapshotMessage}</MessageBarBody></MessageBar>}
      {Object.entries(pageErrors).map(([key, message]) => (
        <ErrorNotice key={key} code={key}>{message}</ErrorNotice>
      ))}
      {(snapshotBusy || !snapshotIsCurrent) && !currentRunStatus && <Loading />}
      {currentRunStatus && (
        <>
          <div className="v1-run-strip" aria-label="Run progress">
            <Badge appearance="tint" color={runState(currentRunStatus.executionState).color}>{runState(currentRunStatus.executionState).label}</Badge>
            <span className="v1-run-chip">{nodes.length === 1 ? '1 session' : `${nodes.length} sessions`}</span>
            {!finished && !Number.isNaN(startedAt) && <span className="v1-run-chip">{formatElapsed(now - startedAt)} elapsed</span>}
            {pendingDecisionCount > 0 && <span className="v1-run-chip v1-run-chip-strong">{pendingDecisionCount} waiting for you</span>}
            <span className="v1-run-chip" role="status">{streamLabel}</span>
            <details>
              <summary>Run details</summary>
              <SnapshotDetail runStatus={currentRunStatus} />
              <p className="v1-muted">This status describes the run, not the health of the computers running it.</p>
            </details>
          </div>
          <div className="v1-run-body">
            <RunTree
              tree={currentTree}
              statuses={currentStatuses}
              selectedSession={selectedSession}
              onSelect={setSelectedSession}
            />
            <section className="v1-run-pane" aria-label="Selected session">
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
              <Tab value="chat" icon={<Chat24Regular />}>Chat</Tab>
              <Tab value="topology" icon={<Flowchart24Regular />}>Topology</Tab>
              <Tab value="approvals">Approvals{pendingDecisionCount > 0 ? ` (${pendingDecisionCount})` : ''}</Tab>
              <Tab value="activity">Activity</Tab>
              <Tab value="changes">Changes</Tab>
              <Tab value="usage">Usage</Tab>
              <Tab value="selection">Run settings</Tab>
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
                <p className="v1-muted">Select a session to see its activity and blockers. Available actions depend on its current state.</p>
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
                    {selectedStatus.parentSessionId && <Link className="v1-link-button" to={`/projects/${encodeURIComponent(projectId)}/knowledge?runId=${encodeURIComponent(runId)}&agentId=${encodeURIComponent(selectedNode.identity.sessionId)}`}><Brain24Regular /> Memories for this session</Link>}
                  </div>
                  {selectedStatus.blockers.length > 0 && <p className="v1-warning">This session has {selectedStatus.blockers.length} owner-reported blocker(s). Open Approvals to respond.</p>}
                </Panel>
              )}
            </div>
          )}
          {tab === 'chat' && <ChatView key={`${projectId}/${runId}/${session?.accessToken ?? ''}`} projectId={projectId} runId={runId} nodes={nodes} targetSessionId={chatTarget} journalEvents={journal.events} apiCall={apiCall} snapshotBusy={snapshotBusy} />}
          {tab === 'approvals' && (
            <DecisionList projectId={projectId} runId={runId} statuses={currentStatuses} decisions={currentDecisions} apiCall={apiCall} refresh={() => refreshRef.current()} snapshotBusy={snapshotBusy} />
          )}
          {tab === 'activity' && (
            <div className="v1-stack">
              <Panel title={`Committed event journal · ${journal.connected ? 'live' : journal.loading ? 'loading' : 'reconnecting'}`}>
                {journal.error && <ErrorNotice code={errorCode(journal.error)}>{errorMessage(journal.error)}</ErrorNotice>}
                <p className="v1-muted">Earlier events load first. New events appear as they happen, in order, without duplicates.</p>
                <JournalList events={journal.events} />
              </Panel>
              <ActivityView runStatus={currentRunStatus} statuses={currentStatuses} />
            </div>
          )}
          {tab === 'selection' && (
            <div className="v1-stack">
              <Panel title="Run settings">
                {selectionError && <ErrorNotice code="selection">{selectionError}</ErrorNotice>}
                <SelectionView selection={currentSelection} />
              </Panel>
            </div>
          )}
          {tab === 'changes' && (
            <div className="v1-stack">
              {currentSelection?.projectConfiguration.sourceControl?.authMode === 'githubApp' ? (
                <RunGitHubAppPanel
                  projectId={projectId}
                  runId={runId}
                  sessionId={currentRunStatus.rootSessionId}
                  appConnectionId={currentSelection.projectConfiguration.sourceControl.appConnectionId ?? ''}
                />
              ) : (
                <EmptyState title="No repository changes" description="This run does not use a GitHub App repository connection, so it has no branch or pull request to show." />
              )}
            </div>
          )}
          {tab === 'usage' && (
            <Panel title="Usage">
              {usageError && <ErrorNotice code="usage">{usageError}</ErrorNotice>}
              <UsageView usage={currentUsage} />
            </Panel>
          )}
            </section>
          </div>
        </>
      )}
    </>
  );
}

/** Kind names shown in the run tree and chat, matching the 0.x run tree. */
const SESSION_KIND_LABELS: Record<SessionTreeNode['kind'], string> = {
  coordinator: 'Coordinator',
  childWork: 'Agent',
  scribe: 'Scribe',
  operatorChat: 'Chat',
  childRun: 'Child run',
};

/** Short display name for one session. */
function sessionName(node: SessionTreeNode): string {
  return SESSION_KIND_LABELS[node.kind] ?? node.kind;
}

/** Formats a duration in the 0.x elapsed style, for example "1h 4m" or "42s". */
function formatElapsed(milliseconds: number): string {
  const seconds = Math.max(0, Math.floor(milliseconds / 1000));
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  if (hours > 0) return `${hours}h ${minutes}m`;
  if (minutes > 0) return `${minutes}m ${seconds % 60}s`;
  return `${seconds}s`;
}

/** Current time that updates every second while `enabled`, for elapsed counters. */
function useTickingNow(enabled: boolean): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!enabled) return undefined;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [enabled]);
  return now;
}

/**
 * Gets a session for this exact run without a prompt, once per binding.
 * Returns 'pending' while the attempt runs and 'failed' when the user must sign in.
 */
function useSilentRunSession(
  binding: RunBinding | null,
  authorized: boolean,
  ensureSession: AuthReturn['ensureSession'],
): 'pending' | 'failed' {
  const key = binding ? `${binding.projectId}/${binding.runId}` : '';
  const [failedKey, setFailedKey] = useState<string | null>(null);
  useEffect(() => {
    // A held session activates at once; a new one is requested once, and concurrent calls share it.
    if (!binding || authorized || failedKey === key) return;
    void ensureSession(binding).then((ok) => {
      if (!ok) setFailedKey(key);
    });
  }, [authorized, binding, ensureSession, failedKey, key]);
  // Leaving the run makes the unbound session active again for project pages.
  useEffect(() => () => { void ensureSession(null); }, [ensureSession]);
  return failedKey === key ? 'failed' : 'pending';
}

/** The 0.x run tree rail: every session in tree order, with its state and selection. */
function RunTree({ tree, statuses, selectedSession, onSelect }: {
  tree: SessionTreeSnapshot | null;
  statuses: Record<string, SessionStatusSnapshot>;
  selectedSession: string;
  onSelect: (sessionId: string) => void;
}) {
  const ordered = useMemo(() => {
    if (!tree) return [];
    const children = new Map<string, SessionTreeNode[]>();
    for (const node of tree.nodes) {
      const parent = node.parentSessionId ?? '';
      children.set(parent, [...(children.get(parent) ?? []), node]);
    }
    const rows: { node: SessionTreeNode; depth: number }[] = [];
    const visit = (node: SessionTreeNode, depth: number) => {
      rows.push({ node, depth });
      for (const child of children.get(node.identity.sessionId) ?? []) visit(child, depth + 1);
    };
    const root = tree.nodes.find((node) => node.identity.sessionId === tree.rootSessionId);
    if (root) visit(root, 0);
    // Sessions whose parent is missing from the snapshot still appear, at the top level.
    for (const node of tree.nodes)
      if (!rows.some((row) => row.node === node)) rows.push({ node, depth: 0 });
    return rows;
  }, [tree]);
  const stateText = (node: SessionTreeNode) => {
    const status = statuses[node.identity.sessionId];
    if (status && status.blockers.length > 0) return 'Needs input';
    if (node.lifecycle !== 'active') return node.lifecycle === 'completed' ? 'Done' : node.lifecycle === 'cancelled' ? 'Cancelled' : 'Archived';
    return status?.activity === 'busy' ? 'Working' : status?.activity === 'idle' ? 'Idle' : 'Status unknown';
  };
  return (
    <aside className="v1-run-tree" aria-label="Run tree">
      <div className="v1-run-tree-header">
        <h2>Run tree</h2>
        <span className="v1-muted">{ordered.length === 1 ? '1 session' : `${ordered.length} sessions`}</span>
      </div>
      {ordered.length === 0 ? (
        <p className="v1-muted">The coordinator has not started any sessions yet.</p>
      ) : (
        <ol>
          {ordered.map(({ node, depth }) => (
            <li key={node.identity.sessionId}>
              <button
                type="button"
                className="v1-run-tree-item"
                style={{ paddingLeft: `calc(var(--spacingHorizontalS) + ${depth * 14}px)` }}
                aria-current={node.identity.sessionId === selectedSession ? 'true' : undefined}
                onClick={() => onSelect(node.identity.sessionId)}
              >
                <span>{sessionName(node)} · {stateText(node)}</span>
                <span>{node.identity.sessionId}</span>
              </button>
            </li>
          ))}
        </ol>
      )}
    </aside>
  );
}

/** Adapts the coordinator session tree into the shared interactive React Flow canvas. */
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
      nodeTypes={workflowNodeTypes}
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

/** Displays the in-window Broker sign-in exchange and redirects after a session is established. */
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

/** Sends a validated Broker callback from a popup or hidden frame to its same-origin parent. */
function identityCallbackTarget(): { target: Window; closeAfterSend: boolean } | null {
  const opener = window.opener;
  if (opener && opener !== window) return { target: opener, closeAfterSend: true };
  if (window.parent !== window) return { target: window.parent, closeAfterSend: false };
  return null;
}

function IdentityPopupCallbackPage() {
  const [callback] = useState(() => {
    const callbackSearch = window.__AGENTWEAVER_IDENTITY_CALLBACK__;
    if (!identityCallbackTarget() || typeof callbackSearch !== 'string')
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
    const callbackTarget = identityCallbackTarget();
    if (!callbackTarget || typeof callbackSearch !== 'string') return;

    if (!callback.message) {
      if (callbackTarget.closeAfterSend) window.close();
      return;
    }
    callbackTarget.target.postMessage(callback.message, window.location.origin);
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

/** Relays GitHub App callback status from the redirect URL to the initiating popup. */
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

/** Notifies the opener that GitHub returned; the opener must verify connection status with Identity. */
function RepoAppPopupCallbackPage() {
  const [message] = useState(() => {
    const opener = window.opener;
    return window.__AGENTWEAVER_REPO_APP_CALLBACK__ === true && opener && opener !== window
      ? 'The callback is notifying its opener. The original window confirms the connection.'
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

/** Accepts a same-origin Remote MCP callback and asks Identity to complete the authorization. */
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
        setNotice('The Remote MCP authorization was received by Identity.');
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

/** Applies consent/sign-in gates before mounting the authenticated project and run routes. */
function AuthenticatedRoutes() {
  const { session, consent, restoringSession } = useAuth();
  if (consent) return <ConsentScreen />;
  if (!session && restoringSession)
    return <main className="v1-auth-page"><Loading label="Checking your sign-in session…" /></main>;
  if (!session) return <SignInScreen />;
  return (
    <Routes>
      <Route path="/" element={<Navigate to="/overview" replace />} />
      <Route path="/overview" element={<Shell><OverviewPage /></Shell>} />
      <Route path="/sessions" element={<Shell><UnavailablePage title="Sessions" description="Starting and listing chat sessions needs a service update. Open a project, then choose a run in Orchestrations." /></Shell>} />
      <Route path="/assistant" element={<Shell><UnavailablePage title="New session" description="Starting a new chat needs a service update. Open a project, then choose a run in Orchestrations to read and send messages." /></Shell>} />
      <Route path="/console" element={<Navigate to="/assistant" replace />} />
      <Route path="/settings" element={<Shell><AccountSettingsPage /></Shell>} />
      <Route path="/platform-settings" element={<Shell><UnavailablePage title="Platform settings" description="Platform administration is not available in this web client." /></Shell>} />
      <Route path="/auth/callback" element={<CallbackPage />} />
      <Route path="/projects" element={<Shell><ProjectsPage /></Shell>} />
      <Route path="/projects/:projectId" element={<Shell><DashboardPage /></Shell>} />
      <Route path="/projects/:projectId/settings" element={<Shell><ProjectConfigurationPage /></Shell>} />
      <Route path="/projects/:projectId/skills" element={<Shell><ProjectConfigurationPage view="skills" /></Shell>} />
      <Route path="/projects/:projectId/team" element={<Shell><ProjectConfigurationPage view="agents" /></Shell>} />
      <Route path="/projects/:projectId/team/cast" element={<Shell><UnavailablePage title="Cast a team" description="Interactive team casting is not available in this version. Review the current team in Agents and edit its configuration in Project settings." /></Shell>} />
      <Route path="/projects/:projectId/team/:agentName/memory" element={<AgentMemoryRedirect />} />
      <Route path="/projects/:projectId/knowledge" element={<Shell><KnowledgePage /></Shell>} />
      <Route path="/projects/:projectId/memories" element={<Shell><KnowledgePage /></Shell>} />
      <Route path="/projects/:projectId/orchestrations" element={<Shell><OrchestrationsPage /></Shell>} />
      <Route path="/projects/:projectId/sessions" element={<Navigate to="/sessions" replace />} />
      <Route path="/projects/:projectId/runs/:runId" element={<Shell><RunPage /></Shell>} />
      <Route path="/projects/:projectId/orchestrations/:runId" element={<CoordinatorRunRedirect />} />
      {unavailableProjectPages.map(({ segment, title, description }) => (
        <Route key={segment} path={`/projects/:projectId/${segment}`} element={<Shell><UnavailablePage title={title} description={description} /></Shell>} />
      ))}
      <Route path="/observability/*" element={<Shell><UnavailablePage title="Observability" description="Project trace and agent summaries are not available in this version. Open an existing run for its Activity and Usage views." /></Shell>} />
      <Route path="*" element={<Shell><UnavailablePage title="Page not found" description="This address does not match an Agentweaver page. Use the navigation to choose a destination." /></Shell>} />
    </Routes>
  );
}

/** Preserves older orchestration links by redirecting them to the current run route. */
function CoordinatorRunRedirect() {
  const { projectId = '', runId = '' } = useParams();
  const location = useLocation();
  return <Navigate to={`/projects/${encodeURIComponent(projectId)}/runs/${encodeURIComponent(runId)}${location.search}${location.hash}`} replace />;
}

/** Legacy destinations stay addressable without falling back to monolith APIs. */
const unavailableProjectPages = [
  { segment: 'board', title: 'Board', description: 'The project board is not available in this version. Open Orchestrations to review runs and approvals.' },
  { segment: 'flow', title: 'Flow', description: 'Project-wide agent activity is not available in this version. Open Orchestrations, then a run, to view its session topology.' },
  { segment: 'workspace', title: 'Workspace', description: 'Project file browsing is not available in this version. A run shows its changes in the Changes tab.' },
  { segment: 'workflows', title: 'Workflows', description: 'The workflow editor is not available in this version. Workflow references remain in Project settings.' },
  { segment: 'observability/*', title: 'Observability', description: 'Project trace and agent summaries are not available in this version. Open a run for Activity and Usage.' },
  { segment: 'diagnostics', title: 'Diagnostics', description: 'Project diagnostics are not available in this version. Run Activity shows the status reported for each session.' },
  { segment: 'heartbeat', title: 'Heartbeat', description: 'Project heartbeat monitoring is not available in this version.' },
  { segment: 'cluster', title: 'Cluster', description: 'Cluster management is not available in this web client.' },
];

/** Uses the incumbent empty-state pattern for a capability with no admitted API. */
function UnavailablePage({ title, description }: { title: string; description: string }) {
  const { projectId } = useParams();
  return <>
    <PageHeading title={title} />
    <EmptyState title={title === 'Page not found' ? 'Choose a destination' : 'Not available in this version'} description={description}
      action={<Link className="v1-link-button" to={projectId ? `/projects/${encodeURIComponent(projectId)}` : '/projects'}>{projectId ? 'Open Dashboard' : 'Open Projects'}</Link>} />
  </>;
}

/** Account connections use their existing owner checks, not a new identity store. */
function AccountSettingsPage() {
  const { signOut } = useAuth();
  return <>
    <PageHeading title="Account settings" />
    <Panel title="Sign-in"><Button onClick={signOut}>Sign out</Button></Panel>
    <ProjectRepoAppConnectionPanel />
    <p>Choose a project in the navigation to manage its Copilot connection in Project settings.</p>
  </>;
}

/** A legacy display name is a search input, never an authorization claim. */
function AgentMemoryRedirect() {
  const { projectId = '', agentName = '' } = useParams();
  const location = useLocation();
  const search = new URLSearchParams(location.search);
  if (!search.has('agentId')) search.set('agentId', agentName);
  return <Navigate to={`/projects/${encodeURIComponent(projectId)}/memories?${search}`} replace />;
}

/** Selects popup callback handling or the authenticated v1 route application. */
export default function App() {
  const repoAppCallback = window.location.pathname === REPO_APP_CALLBACK_PATH &&
    window.__AGENTWEAVER_REPO_APP_CALLBACK__ === true;
  const identityPopupCallback = window.location.pathname === '/auth/callback' &&
    ((window.opener && window.opener !== window) || window.parent !== window);
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
