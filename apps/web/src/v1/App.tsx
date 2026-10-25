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
  Branch24Regular,
  Chat24Regular,
  Code24Regular,
  Delete24Regular,
  Flowchart24Regular,
  History24Regular,
  People24Regular,
  PlugConnected24Regular,
  PuzzlePiece24Regular,
  Settings24Regular,
  Sparkle24Regular,
  Wrench24Regular,
} from '@fluentui/react-icons';
import { agentweaverLightTheme } from '../theme';
import { PageSection } from '../components/ui/PageSection';
import { Tile, TileGrid } from '../components/ui/TileGrid';
import { MetricRow } from '../components/ui/Metric';
import { AppDialog } from '../components/ui/AppDialog';
import { EmptyState } from '../components/ui/States';
import { Pager } from '../copilot-fluent-system';
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
import type { FormEvent, ReactElement } from 'react';
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
  ProjectKnowledgeRecord,
  ProjectConfiguration,
  ProjectConfigurationChoices,
  HostedModelReasoningCatalogV1,
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
import { ErrorNotice, Loading, PageHeading, Panel, ProjectRoleBadge } from './pageParts';
import { RunOutputCapturesPanel } from './RunOutputCaptures';
import { OutputCaptureBrowser } from './OutputCaptureBrowser';
import { TranscriptPanel } from './TranscriptPanel';
import { useTurnTranscript } from './turnTranscript';
import { pinnedDecisionFromSearch } from './runAttention';
import { rememberRun } from './recentRuns';
import { DecisionList } from './DecisionView';
import { NewChatComposer } from './NewChatComposer';
import { PlatformComponentsTable } from './PlatformComponentsTable';
import { isRunFinished, runState } from './runState';
import { DashboardPage } from './DashboardPage';
import { ClusterPage, DiagnosticsPage, FlowPage, HeartbeatPage, ProjectObservabilityPage, WorkflowsPage, WorkspacePage } from './OperationsPages';
import { OverviewPage } from './OverviewPage';
import { OrchestrationsPage } from './OrchestrationsPage';
import { BoardPage } from './BoardPage';
import { SessionsPage } from './SessionsPage';
import { NewSessionPage } from './NewSessionPage';
import { readPendingRun } from './pendingRun';
import { StopRunButton } from './StopRunButton';
import { AgentsView } from './AgentsView';
import { CastTeamPage } from './CastTeamPage';
import { PlatformSettingsPage } from './PlatformSettingsPage';
import { useProjectConfiguration, useProjectSummary } from './projectData';
import { runListErrorMessage, useProjectRunList } from './runListData';
import { CONFIGURATION_SECTIONS, SETTINGS_SECTIONS, settingsSection } from './settingsSections';
import type { SettingsSection } from './settingsSections';

/** 0.x settings rail icons; v1-only sections use icons from the same Fluent set. */
const SETTINGS_SECTION_ICONS: Record<SettingsSection, ReactElement> = {
  general: <Settings24Regular />,
  access: <People24Regular />,
  repository: <Branch24Regular />,
  models: <Sparkle24Regular />,
  components: <PuzzlePiece24Regular />,
  limits: <Wrench24Regular />,
  connections: <PlugConnected24Regular />,
  history: <History24Regular />,
  danger: <Delete24Regular />,
  advanced: <Code24Regular />,
};
import { AccessSection, DangerZoneSection, GeneralSection, RepositoryFields, RunLimitsFields } from './ProjectSettingsSections';

/** Checks that a callback or response still refers to the selected project/run pair. */
function isSameBinding(
  actual: RunBinding | null | undefined,
  expected: RunBinding | null,
): boolean {
  return actual?.projectId === expected?.projectId && actual?.runId === expected?.runId;
}

/**
 * Run page tabs in display order. Chat is first, matching the 0.x run page.
 * Chat is also the default tab when the URL has no valid `view` value.
 */
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
  // The button stays disabled while a sign-in is running, and also when the
  // app has a configuration error, because sign-in cannot succeed then.
  // `authorize(null)` asks for a session that is not bound to any run.
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
  // Strip an optional `https://github.com/` prefix and `.git` suffix, so a
  // pasted clone URL works. Then require exactly two path parts (owner and
  // name) with an optional trailing slash. Anything else is not a repository.
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

  // The repository is only used when "GitHub repository" is selected. When it
  // is selected, the form cannot submit until the input parses as owner/name.
  const repository = startFrom === 'github' ? parseRepository(repositoryInput) : null;
  const createDisabled = busy || !projectName.trim() || (startFrom === 'github' && !repository);

  /** Creates the project, then optionally saves the chosen repository into its configuration. */
  const create = async (event: FormEvent) => {
    event.preventDefault();
    // Enter in a field can submit the form even when the button is disabled,
    // so repeat the same check here.
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
        // Second step: save the repository. The project already exists at
        // this point, so a failure here must not be reported as a failed
        // create. It only changes the success message.
        try {
          await apiCall(async (token, tenantSelector) => {
            // Read the current configuration first, so the update keeps every
            // other field and is checked against the current revision.
            const current = await gatewayClient.getProjectConfiguration(token, created.projectId, tenantSelector);
            // Keep any existing source-control fields and switch the auth
            // mode to the GitHub App.
            const sourceControl = { ...(current.configuration.sourceControl ?? {}), repository, authMode: 'githubApp' as const };
            return gatewayClient.updateProjectConfiguration(
              token, created.projectId, current.revision, { ...current.configuration, sourceControl }, tenantSelector,
            );
          }, null);
          message += ` It uses ${repository.owner}/${repository.name}.`;
        } catch {
          // Most often the creator has no grant on the new project yet.
          // Keep the project and tell the user how to add the repository later.
          message += ` The repository was not saved. Add it in Settings > Repository after you have access.`;
        }
      }
      // Reset the form, close the dialog, and reload so the new tile appears.
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
                  meta={<><Badge appearance="outline" size="small">Revision {project.revision}</Badge><ProjectRoleBadge roles={project.effectiveRoles} /></>}
                  actions={<Button appearance="primary" size="small" onClick={() => navigate(`/projects/${encodeURIComponent(project.projectId)}`)}>Open</Button>}
                  actionsAlwaysVisible
                  onClick={() => navigate(`/projects/${encodeURIComponent(project.projectId)}`)}
                />
              ))}
            </TileGrid>
          )}
      <AppDialog open={createOpen} onOpenChange={setCreateOpen} title="Create a project">
          <form className="v1-form" onSubmit={(event) => void create(event)}>
            {/* The dialog covers the page, so the create error is repeated here where the user can see it. */}
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

/**
 * Edits project configuration and reports rejected or stale updates.
 * One component serves three routes: Settings (sectioned form), Skills, and Agents.
 * All three share one loaded configuration, one draft, and one save path.
 */
function ProjectConfigurationPage({ view = 'settings' }: { view?: 'settings' | 'skills' | 'agents' }) {
  const { projectId = '' } = useParams();
  const { session, apiCall } = useAuth();
  const [searchParams] = useSearchParams();
  // The selected settings section lives in the URL (`?section=`), so links and
  // reloads open the same section. Unknown values fall back to a default.
  const section = settingsSection(searchParams.get('section'));
  // Only the settings view needs the project summary (General and Danger zone
  // sections). An empty ID tells the hook to skip the request on other views.
  const project = useProjectSummary(view === 'settings' ? projectId : '');
  const [versioned, setVersioned] = useState<VersionedProjectConfiguration | null>(null);
  const [configurationChoices, setConfigurationChoices] = useState<ProjectConfigurationChoices | null>(null);
  const [configurationChoicesError, setConfigurationChoicesError] = useState(false);
  const [hostedReasoningCatalog, setHostedReasoningCatalog] = useState<HostedModelReasoningCatalogV1 | null>(null);
  const [hostedReasoningCatalogError, setHostedReasoningCatalogError] = useState(false);
  // Two views of the same unsaved edit:
  // - `editor` is the raw JSON text in the Advanced section.
  // - `configurationDraft` is the parsed object that the form sections edit.
  // Form edits update both. Raw JSON edits update only `editor`, so the two
  // can drift apart until the user saves or reloads.
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
    setConfigurationChoices(null);
    setConfigurationChoicesError(false);
    setHostedReasoningCatalog(null);
    setHostedReasoningCatalogError(false);
    try {
      const [result, choiceResult, hostedCatalogResult] = await Promise.all([
        apiCall(
          (token, tenantSelector) => gatewayClient.getProjectConfiguration(token, projectId, tenantSelector),
          null,
        ),
        view === 'settings'
          ? apiCall(
            (token, tenantSelector) =>
              gatewayClient.getProjectConfigurationChoices(token, projectId, tenantSelector),
            null,
          ).then(
            (value) => ({ value, failed: false }),
            () => ({ value: null, failed: true }),
          )
          : Promise.resolve({ value: null, failed: false }),
        view === 'settings'
          ? apiCall(
            (token, tenantSelector) =>
              gatewayClient.getHostedModelReasoningCatalog(token, projectId, tenantSelector),
            null,
          ).then(
            (value) => ({ value, failed: false }),
            () => ({ value: null, failed: true }),
          )
          : Promise.resolve({ value: null, failed: false }),
      ]);
      if (activeScope.current !== scope || loadGeneration.current !== generation) return;
      if (result.projectId !== projectId)
        throw new Error('The server returned configuration for a different project.');
      setVersioned(result);
      const scopedChoices = choiceResult.value?.projectId === projectId ? choiceResult.value : null;
      const scopedHostedCatalog = hostedCatalogResult.value?.projectId === projectId
        ? hostedCatalogResult.value
        : null;
      setConfigurationChoices(scopedChoices);
      setConfigurationChoicesError(view === 'settings' && (choiceResult.failed || !scopedChoices));
      setHostedReasoningCatalog(scopedHostedCatalog);
      setHostedReasoningCatalogError(view === 'settings' &&
        (hostedCatalogResult.failed || !scopedHostedCatalog));
      setEditor(JSON.stringify(result.configuration, null, 2));
      // Start the form draft from the loaded document, so it matches the JSON text.
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
  }, [apiCall, projectId, requestScope, view]);

  useEffect(() => {
    activeScope.current = requestScope;
    loadGeneration.current += 1;
    const timer = window.setTimeout(() => { void load(); }, 0);
    return () => {
      window.clearTimeout(timer);
      loadGeneration.current += 1;
    };
  }, [load, requestScope]);

  /**
   * Saves a full configuration document against the loaded revision. Resolves true on success.
   * The settings form, the raw JSON editor, and the Agents view all save through here.
   */
  const persist = async (configuration: ProjectConfiguration): Promise<boolean> => {
    // Remember which user and project this save belongs to. If either changes
    // while the request runs, the late response is ignored below.
    const scope = requestScope;
    // Refuse to save when:
    // - the configuration is not loaded for this exact scope (we need its revision), or
    // - a skill assignment is updating the same document right now.
    if (!versioned || loadedScope !== scope || versioned.projectId !== projectId || skillAssignmentPending) return false;
    // A new model choice may need a credential that is not set yet. The form
    // shows that problem itself; the save just stops here.
    if (needsModelCredential(configuration, versioned.configuration.modelSelection)) return false;
    setSavingScope(scope);
    setError(null);
    setErrorScope(null);
    setNotice(null);
    setNoticeScope(null);
    try {
      // The server rejects the update if `versioned.revision` is no longer the
      // latest revision. That stops one user from silently overwriting another.
      const updated = await apiCall(
        (token, tenantSelector) => gatewayClient.updateProjectConfiguration(
          token, projectId, versioned.revision, configuration, tenantSelector,
        ),
        null,
      );
      // The user switched project or account while saving. Do not show this result.
      if (activeScope.current !== scope) return false;
      if (updated.projectId !== projectId)
        throw new Error('The server returned an update for a different project.');
      // Replace the saved copy, the JSON text, and the form draft together so
      // all three show the new revision.
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

  /**
   * Form submit handler. It always saves the raw JSON text, because form
   * edits are copied into that text and raw edits exist only there.
   */
  const save = async (event: FormEvent) => {
    event.preventDefault();
    let configuration: ProjectConfiguration;
    // Parse locally first so invalid JSON shows a clear error and is never sent.
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
    listDefinitions: (scope: string) => apiCall(
      (token, tenantSelector) => gatewayClient.listMarketplaceDefinitions(token, scope, tenantSelector),
      null,
    ),
    packageSkillContent: (request: Parameters<typeof gatewayClient.packageSkillContent>[1]) =>
      apiCall((token, tenantSelector) =>
        gatewayClient.packageSkillContent(token, request, tenantSelector), null),
    revokeSkillRevision: (scope: string, skillId: string, revision: number, reason: string) =>
      apiCall((token, tenantSelector) =>
        gatewayClient.revokeProjectSkillRevision(token, scope, skillId, revision, reason, tenantSelector), null),
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

  // A save or a skill assignment is running. Both write the same document,
  // so the page blocks other edits until it finishes.
  const busy = savingScope === requestScope || skillAssignmentPending;
  // True when the JSON text still matches the form draft. False means the
  // user edited raw JSON; then the form sections become read-only so the
  // two edits cannot overwrite each other.
  const draftInSync = Boolean(configurationDraft && editor === JSON.stringify(configurationDraft, null, 2));
  // Shared props for every form section. A form change updates the draft
  // and rewrites the JSON text, so the two stay in step.
  const fieldProps = configurationDraft ? {
    configuration: configurationDraft,
    readOnly: busy || !draftInSync,
    onChange: (next: ProjectConfiguration) => { setConfigurationDraft(next); setEditor(JSON.stringify(next, null, 2)); },
  } : null;
  // Disable Save when:
  // - the page is busy or the JSON text is empty,
  // - nothing changed from the saved revision, or
  // - the form draft picks a model that still needs a credential.
  const saveDisabled = busy || !editor.trim()
    || Boolean(currentConfiguration && editor === JSON.stringify(currentConfiguration.configuration, null, 2))
    || Boolean(configurationDraft && draftInSync && currentConfiguration
      && needsModelCredential(configurationDraft, currentConfiguration.configuration.modelSelection));
  // Error and success banners, shown at the top of every view of this page.
  const statusMessages = (
    <>
      {currentError != null && <ErrorNotice code={errorCode(currentError)}>{errorMessage(currentError)}</ErrorNotice>}
      {currentNotice && <MessageBar intent="success"><MessageBarBody>{currentNotice}</MessageBarBody></MessageBar>}
    </>
  );

  // Agents route: the Agents view renders its own header with the team
  // controls once the configuration is loaded, and saves through persist().
  if (view === 'agents') return currentConfiguration && !currentLoading ? (
    <AgentsView
      projectId={projectId}
      configuration={currentConfiguration.configuration}
      configurationRevision={currentConfiguration.revision}
      readOnly={busy}
      onSave={persist}
      status={statusMessages}
      onReload={() => void load()}
    />
  ) : (
    <>
      <PageHeading title="Agents" description="The cast working on this project." page="Agents" />
      {statusMessages}
      {currentLoading && <Loading label="Loading agents" />}
    </>
  );

  // Skills route: assignments write their own configuration revision on the server.
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
            // Ignore updates that arrive after the user moved to another
            // project or account, and updates for a different project.
            if (activeScope.current !== requestScope || updated.projectId !== projectId) return;
            // Keep the saved copy, JSON text, and form draft on the same revision.
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

  // Settings view: a side rail of section links plus the selected section.
  // Some sections own their own data and saving (General, Access, Danger zone,
  // Connections, History). The sections in CONFIGURATION_SECTIONS edit the
  // shared configuration document and use the shared form and Save button.
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
              <span className="v1-settings-rail__icon" aria-hidden="true">{SETTINGS_SECTION_ICONS[item.key]}</span>
              <span>{item.label}</span>
              {item.key === 'danger' && <Badge appearance="tint" color="danger" size="small">Risk</Badge>}
            </Link>
          ))}
        </nav>
        <div className="v1-stack v1-settings-pane">
          {statusMessages}
          {/* General and Danger zone edit the project record, not the configuration document.
              Keying General by revision resets its fields after every saved change. */}
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
            // The key remounts the history list after each save, so it reloads.
            <ConfigurationHistory key={`${requestScope}/${currentConfiguration.revision}`}
              projectId={projectId} currentRevision={currentConfiguration.revision} />
          ))}
          {CONFIGURATION_SECTIONS.has(section) && (currentLoading ? <Loading /> : currentConfiguration && fieldProps && (
            <form className="v1-form" onSubmit={(event) => void save(event)}>
              {section === 'repository' && <RepositoryFields {...fieldProps} />}
              {section === 'models' && <ConfigurationFields {...fieldProps} part="models" savedSelection={currentConfiguration.configuration.modelSelection}
                choices={configurationChoices} choicesError={configurationChoicesError}
                hostedReasoningCatalog={hostedReasoningCatalog}
                hostedReasoningCatalogError={hostedReasoningCatalogError} />}
              {section === 'components' && <ConfigurationFields {...fieldProps} part="components" savedSelection={currentConfiguration.configuration.modelSelection}
                choices={configurationChoices} choicesError={configurationChoicesError} />}
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

/** Discovers project-wide records and manages records within a selected run and agent. */
function ProjectKnowledgeDiscovery({
  projectId,
  onRefreshCounts,
}: {
  projectId: string;
  onRefreshCounts: () => void;
}) {
  const { apiCall, authorize, ensureSession } = useAuth();
  const [draftQuery, setDraftQuery] = useState('');
  const [query, setQuery] = useState('');
  const [kind, setKind] = useState('');
  const [includeInactive, setIncludeInactive] = useState(true);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(50);
  const historyPageSize = 25;
  const [records, setRecords] = useState<ProjectKnowledgeRecord[]>([]);
  const [totalCount, setTotalCount] = useState(0);
  const [loading, setLoading] = useState(false);
  const [needsSignIn, setNeedsSignIn] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [recordsScope, setRecordsScope] = useState<string | null>(null);
  const [expandedRecordKey, setExpandedRecordKey] = useState<string | null>(null);
  const [historyItems, setHistoryItems] = useState<KnowledgeRecordRevision[]>([]);
  const [historyTotalCount, setHistoryTotalCount] = useState(0);
  const [historyPage, setHistoryPage] = useState(1);
  const [historyScope, setHistoryScope] = useState<string | null>(null);
  const [historyLoading, setHistoryLoading] = useState(false);
  const [historyError, setHistoryError] = useState<unknown>(null);
  const requestScope = JSON.stringify([projectId, query, kind, includeInactive, page, pageSize]);
  const activeScope = useRef(requestScope);
  const activeHistoryScope = useRef<string | null>(null);
  // The service supports at most 100 pages, so keep the shared pager inside that window.
  const maxVisibleRecords = pageSize * 100;
  const pageCount = Math.max(1, Math.ceil(Math.min(totalCount, maxVisibleRecords) / pageSize));
  const currentRecords = recordsScope === requestScope ? records : [];
  const currentError = recordsScope === requestScope ? error : null;

  const load = useCallback(async () => {
    const scope = requestScope;
    activeScope.current = scope;
    activeHistoryScope.current = null;
    setExpandedRecordKey(null);
    setHistoryScope(null);
    setLoading(true);
    setError(null);
    setRecords([]);
    setTotalCount(0);
    setRecordsScope(null);
    try {
      // Keep a project-wide token separate from the current run token; the owner rejects run-bound authority here.
      if (!await ensureSession(null, false)) {
        if (activeScope.current === scope) {
          setNeedsSignIn(true);
          setRecordsScope(scope);
        }
        return;
      }
      setNeedsSignIn(false);
      const resultPage = await apiCall(
        (token, tenantSelector) => gatewayClient.searchProjectKnowledge(token, projectId, {
          query,
          kind: kind || undefined,
          includeInactive,
          page,
          pageSize,
        }, tenantSelector),
        null,
      );
      if (activeScope.current !== scope) return;
      if (resultPage.items.some((item) =>
        item.record.projectId !== projectId ||
        !item.accessRunId ||
        !item.record.agentId ||
        !item.record.recordId))
        throw new Error('Knowledge returned records outside the requested project scope.');
      const lastPage = Math.max(1, Math.ceil(Math.min(resultPage.totalCount, maxVisibleRecords) / pageSize));
      if (page > lastPage) {
        // A refresh can shrink the result set while the reader is on a later page.
        setPage(lastPage);
        return;
      }
      setRecords(resultPage.items);
      setTotalCount(resultPage.totalCount);
      setRecordsScope(scope);
    } catch (reason) {
      if (activeScope.current !== scope) return;
      setError(reason);
      setNeedsSignIn(errorCode(reason) === 'unauthorized');
      setRecords([]);
      setTotalCount(0);
      setRecordsScope(scope);
    } finally {
      if (activeScope.current === scope) setLoading(false);
    }
  }, [apiCall, ensureSession, includeInactive, kind, maxVisibleRecords, page, pageSize, projectId, query, requestScope]);

  useEffect(() => {
    activeScope.current = requestScope;
    const timer = window.setTimeout(() => { void load(); }, 0);
    return () => window.clearTimeout(timer);
  }, [load, requestScope]);

  const loadHistory = async (item: ProjectKnowledgeRecord, requestedPage = 1) => {
    const key = JSON.stringify([item.accessRunId, item.record.recordId]);
    const scope = JSON.stringify([requestScope, key, requestedPage]);
    activeHistoryScope.current = scope;
    setExpandedRecordKey(key);
    setHistoryItems([]);
    setHistoryTotalCount(0);
    setHistoryPage(requestedPage);
    setHistoryScope(null);
    setHistoryError(null);
    setHistoryLoading(true);
    try {
      // The access run comes from the provider binding returned with the record, never from user-entered IDs.
      if (!await ensureSession(null, false)) {
        setNeedsSignIn(true);
        throw new Error('Sign in to read this record history.');
      }
      const resultPage = await apiCall((token, tenantSelector) => gatewayClient.readKnowledgeRevisions(
        token,
        projectId,
        item.accessRunId,
        item.record.agentId,
        item.record.recordId,
        { page: requestedPage, pageSize: historyPageSize },
        tenantSelector,
      ), null);
      if (activeScope.current !== requestScope || activeHistoryScope.current !== scope) return;
      if (resultPage.items.some((revision) => revision.recordId !== item.record.recordId))
        throw new Error('Knowledge returned revisions outside the requested record scope.');
      setHistoryItems(resultPage.items);
      setHistoryTotalCount(resultPage.totalCount);
      setHistoryScope(scope);
    } catch (reason) {
      if (activeScope.current !== requestScope || activeHistoryScope.current !== scope) return;
      setNeedsSignIn(errorCode(reason) === 'unauthorized');
      setHistoryError(reason);
      setHistoryScope(scope);
    } finally {
      if (activeScope.current === requestScope && activeHistoryScope.current === scope)
        setHistoryLoading(false);
    }
  };

  const search = (event: FormEvent) => {
    event.preventDefault();
    setPage(1);
    setQuery(draftQuery);
  };

  return (
    <Panel
      title="Project-wide search"
      description="Find records across accessible runs and agents. Choose a run and agent below to manage records."
      actions={<Button appearance="subtle" icon={<ArrowClockwiseRegular />} onClick={() => {
        void load();
        onRefreshCounts();
      }}>Refresh</Button>}
    >
      <form className="v1-form v1-inline-form" onSubmit={search}>
        <Field label="Search text">
          <Input value={draftQuery} onChange={(_, data) => setDraftQuery(data.value)} />
        </Field>
        <Field label="Kind">
          <select className="v1-select" aria-label="Project Knowledge kind" value={kind}
            onChange={(event) => { setPage(1); setKind(event.target.value); }}>
            <option value="">All kinds</option>
            <option value="memory">Memories</option>
            <option value="decision">Decisions</option>
            <option value="proposal">Proposals</option>
            <option value="sessionContext">Session context</option>
          </select>
        </Field>
        <label className="v1-checkbox">
          <input type="checkbox" checked={includeInactive}
            onChange={(event) => { setPage(1); setIncludeInactive(event.target.checked); }} />
          Include inactive records
        </label>
        <Button appearance="primary" type="submit">Search project memories</Button>
      </form>
      {needsSignIn && (
        <div className="v1-scope-action">
          <p className="v1-muted">Sign in without a run binding to search project-wide Knowledge.</p>
          <Button appearance="primary" onClick={() => void authorize(null).then(() => {
            onRefreshCounts();
            return load();
          })}>
            Sign in for project-wide search
          </Button>
        </div>
      )}
      {currentError != null && <ErrorNotice code={errorCode(currentError)}>{errorMessage(currentError)}</ErrorNotice>}
      {currentError == null && recordsScope === requestScope && (
        <>
          <p className="v1-muted">
            {totalCount} record(s){totalCount > pageSize ? ` · Page ${page} of ${Math.min(100, pageCount)}` : ''}.
            {totalCount > maxVisibleRecords
              ? ` Showing the first ${maxVisibleRecords.toLocaleString()} matches. Increase page size or refine the search.`
              : ''}
          </p>
          {totalCount > pageSize && (
            <Pager
              page={page}
              pageSize={pageSize}
              totalItems={Math.min(totalCount, maxVisibleRecords)}
              pageSizeOptions={[10, 25, 50]}
              onPageChange={(nextPage) => setPage(Math.min(100, nextPage))}
              onPageSizeChange={(size) => { setPageSize(size); setPage(1); }}
            />
          )}
        </>
      )}
      {loading || recordsScope !== requestScope ? <Loading /> : currentError != null ? null : currentRecords.length === 0 ? (
        <p className="v1-muted">No project-wide records match this search.</p>
      ) : (
        <div className="v1-record-list">
          {currentRecords.map((item) => {
            const record = item.record;
            const key = JSON.stringify([item.accessRunId, record.recordId]);
            const isExpanded = expandedRecordKey === key;
            const expectedHistoryScope = JSON.stringify([requestScope, key, historyPage]);
            return (
              <article className="v1-record" key={key}>
                <div className="v1-record-title">
                  <div>
                    <h3>{record.title || record.type}</h3>
                    <p className="v1-muted">
                      {record.kind} · {record.type} · agent {record.agentId} · {record.state} · trust: {record.trustState}
                      {record.sourceRunId && <> · Source run {record.sourceRunId}</>}
                    </p>
                  </div>
                  <Badge appearance="tint" color={record.state === 'active' ? 'success' : record.state === 'pending' ? 'warning' : 'subtle'}>
                    {record.state}
                  </Badge>
                </div>
                <p className="v1-record-content">{record.content}</p>
                {record.rationale && <p className="v1-muted">Rationale: {record.rationale}</p>}
                {record.tags.length > 0 && <p className="v1-muted">Tags: {record.tags.join(', ')}</p>}
                <p className="v1-muted">
                  Updated <time dateTime={record.updatedAt}>{new Date(record.updatedAt).toLocaleString()}</time>
                  {' · '}Access run <Link to={`/projects/${encodeURIComponent(projectId)}/runs/${encodeURIComponent(item.accessRunId)}`}>
                    {item.accessRunId}
                  </Link>
                </p>
                <Button appearance="secondary" onClick={() => {
                  if (isExpanded) {
                    activeHistoryScope.current = null;
                    setExpandedRecordKey(null);
                  } else {
                    void loadHistory(item);
                  }
                }}>
                  {isExpanded ? 'Hide revision history' : 'View revision history'}
                </Button>
                {isExpanded && (
                  <section aria-label={`Revision history for ${record.title || record.type}`}>
                    <h4>Revision history</h4>
                    {historyError != null && <ErrorNotice code={errorCode(historyError)}>{errorMessage(historyError)}</ErrorNotice>}
                    {historyLoading || historyScope !== expectedHistoryScope ? <Loading /> : historyError != null ? null : (
                      <>
                        <p className="v1-muted">
                          {historyTotalCount} revision(s) · Page {historyPage} of {Math.max(1, Math.ceil(historyTotalCount / historyPageSize))}
                        </p>
                        <div className="v1-actions">
                          <Button appearance="secondary" disabled={historyLoading || historyPage <= 1}
                            onClick={() => void loadHistory(item, Math.max(1, historyPage - 1))}>
                            Previous revisions
                          </Button>
                          <Button appearance="secondary"
                            disabled={historyLoading || historyPage >= Math.max(1, Math.ceil(historyTotalCount / historyPageSize))}
                            onClick={() => void loadHistory(item, historyPage + 1)}>
                            Next revisions
                          </Button>
                        </div>
                        {historyItems.length === 0 ? <p className="v1-muted">No revisions were returned.</p> : (
                          <div className="v1-record-list">
                            {historyItems.map((revision) => (
                              <article className="v1-record" key={revision.revisionId}>
                                <div className="v1-record-title">
                                  <div>
                                    <h5>Revision {revision.revision} · {revision.changeKind || revision.state}</h5>
                                    <p className="v1-muted">{revision.createdAt}</p>
                                  </div>
                                  <Badge appearance="tint" color={revision.state === 'active' ? 'success' : 'subtle'}>
                                    {revision.state}
                                  </Badge>
                                </div>
                                <p className="v1-record-content">{revision.content}</p>
                                {revision.rationale && <p className="v1-muted">Rationale: {revision.rationale}</p>}
                                {revision.reason && <p className="v1-muted">Change reason: {revision.reason}</p>}
                              </article>
                            ))}
                          </div>
                        )}
                      </>
                    )}
                  </section>
                )}
              </article>
            );
          })}
        </div>
      )}
    </Panel>
  );
}

function ProjectKnowledgeMetrics({ projectId, refreshKey }: { projectId: string; refreshKey: number }) {
  const { apiCall, ensureSession } = useAuth();
  const [result, setResult] = useState<
    | { key: string; status: 'loaded'; counts: { pending: number; decisions: number; memories: number } }
    | { key: string; status: 'needs-sign-in' }
    | { key: string; status: 'error'; error: unknown }
    | null
  >(null);
  const requestKey = JSON.stringify([projectId, refreshKey]);
  const currentResult = result?.key === requestKey ? result : null;

  useEffect(() => {
    let active = true;

    // Counts come from owner-filtered totals, not a page of records; sessions still lack a project-wide total.
    void (async () => {
      try {
        if (!await ensureSession(null, false)) {
          if (active) setResult({ key: requestKey, status: 'needs-sign-in' });
          return;
        }
        const [pending, decisions, memories] = await Promise.all([
          apiCall((token, tenantSelector) => gatewayClient.searchProjectKnowledge(token, projectId, {
            kind: 'proposal',
            state: 'pending',
            includeInactive: true,
            page: 1,
            pageSize: 1,
          }, tenantSelector), null),
          apiCall((token, tenantSelector) => gatewayClient.searchProjectKnowledge(token, projectId, {
            kind: 'decision',
            includeInactive: true,
            page: 1,
            pageSize: 1,
          }, tenantSelector), null),
          apiCall((token, tenantSelector) => gatewayClient.searchProjectKnowledge(token, projectId, {
            kind: 'memory',
            includeInactive: false,
            page: 1,
            pageSize: 1,
          }, tenantSelector), null),
        ]);
        if (active) setResult({
          key: requestKey,
          status: 'loaded',
          counts: { pending: pending.totalCount, decisions: decisions.totalCount, memories: memories.totalCount },
        });
      } catch (reason) {
        if (active) setResult({ key: requestKey, status: 'error', error: reason });
      }
    })();

    return () => {
      active = false;
    };
  }, [apiCall, ensureSession, projectId, refreshKey, requestKey]);

  const countValue = (value: number | undefined) => {
    if (currentResult === null) return 'Loading';
    if (currentResult.status === 'needs-sign-in') return 'Sign in required';
    if (currentResult.status === 'error' || value === undefined) return 'Unavailable';
    return value;
  };

  return (
    <section aria-label="Team memory summary">
      <MetricRow items={[
        { label: 'Pending proposals', value: countValue(currentResult?.status === 'loaded' ? currentResult.counts.pending : undefined) },
        { label: 'Decisions', value: countValue(currentResult?.status === 'loaded' ? currentResult.counts.decisions : undefined) },
        { label: 'Memories', value: countValue(currentResult?.status === 'loaded' ? currentResult.counts.memories : undefined) },
        { label: 'Sessions', value: 'Unavailable' },
      ]} />
      {currentResult?.status === 'error' && (
        <ErrorNotice code={errorCode(currentResult.error)}>{errorMessage(currentResult.error)}</ErrorNotice>
      )}
      {currentResult?.status === 'needs-sign-in' && (
        <p className="v1-muted">Sign in without a run binding to load project-wide counts.</p>
      )}
      <p className="v1-muted">
        Project-wide session counts aren't available yet.
      </p>
    </section>
  );
}

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
  const [summaryRefreshKey, setSummaryRefreshKey] = useState(0);
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
  // The newest runs in this project, for the run select. The service caps a
  // page at 100 runs.
  const projectRuns = useProjectRunList(projectId, { pageSize: 100 });
  const knowledgeConfiguration = useProjectConfiguration(projectId);
  /** The project's newest runs, plus the run from the URL so a deep link keeps its choice. */
  const runOptions = useMemo(() => {
    const options = projectRuns.items.map((item) => ({ value: item.runId, label: `${item.label} · ${runState(item.executionState).label}` }));
    // A run from a link can be older than the loaded page. Add it at the top so
    // the select can show it.
    const current = searchParams.get('runId');
    if (current && !options.some((option) => option.value === current)) options.unshift({ value: current, label: current });
    return options;
  }, [projectRuns.items, searchParams]);
  /** Agents from the project configuration, plus the agent from the URL (for example, a session opened from Topology). */
  const agentOptions = useMemo(() => {
    const options = (knowledgeConfiguration.data?.configuration.agentCharters ?? [])
      .map((agent) => ({ value: agent.agentId, label: `${agent.name} · ${agent.role}` }));
    const current = searchParams.get('agentId');
    // Same idea as runs: keep an agent ID from the URL even if the project
    // configuration does not list it (for example, a session ID).
    if (current && !options.some((option) => option.value === current)) options.unshift({ value: current, label: current });
    return options;
  }, [knowledgeConfiguration.data, searchParams]);
  // Knowledge requests need a session bound to this exact project and run.
  // If the current session is bound elsewhere, try to switch silently first;
  // `silentBind` becomes 'failed' only when the user must sign in again.
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
  const activeProjectId = useRef(projectId);
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
    activeProjectId.current = projectId;
    if (!binding || !agentId.trim() || !correctlyScoped) return undefined;
    const timer = window.setTimeout(() => { void load(); }, 0);
    return () => window.clearTimeout(timer);
  }, [binding, agentId, correctlyScoped, load, projectId, requestScope]);

  const refreshSummaryCounts = (mutationProjectId: string) => {
    // List filters can change while a project-wide mutation is in flight.
    if (activeProjectId.current === mutationProjectId)
      setSummaryRefreshKey((current) => current + 1);
  };

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
      assertKnowledgeMutationScope(result, projectId, agentId.trim());
      refreshSummaryCounts(projectId);
      if (activeScope.current !== scope) return;
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
      assertKnowledgeMutationScope(result, projectId, agentId.trim());
      refreshSummaryCounts(projectId);
      if (activeScope.current !== scope) return;
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
      assertKnowledgeMutationScope(result, projectId, agentId.trim());
      refreshSummaryCounts(projectId);
      if (activeScope.current !== scope) return;
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
      assertKnowledgeMutationScope(result, projectId, agentId.trim());
      refreshSummaryCounts(projectId);
      if (activeScope.current !== scope) return;
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
      assertKnowledgeMutationScope(result, projectId, agentId.trim());
      refreshSummaryCounts(projectId);
      if (activeScope.current !== scope) return;
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
      assertKnowledgeMutationScope(result, projectId, agentId.trim());
      refreshSummaryCounts(projectId);
      if (activeScope.current !== scope) return;
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
      assertKnowledgeMutationScope(result, projectId, agentId.trim());
      refreshSummaryCounts(projectId);
      if (activeScope.current !== scope) return;
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
      assertKnowledgeRecordsInScope(result.records, projectId, agentId.trim());
      refreshSummaryCounts(projectId);
      if (activeScope.current !== scope) return;
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
      <PageHeading title="Memories" description="Search project-wide or choose a run and agent to manage records." page="Memories" />
      <ProjectKnowledgeMetrics projectId={projectId} refreshKey={summaryRefreshKey} />
      {currentError != null && <ErrorNotice code={errorCode(currentError)}>{errorMessage(currentError)}</ErrorNotice>}
      {currentNotice && <MessageBar intent="info"><MessageBarBody>{currentNotice}</MessageBarBody></MessageBar>}
      <ProjectKnowledgeDiscovery
        projectId={projectId}
        onRefreshCounts={() => setSummaryRefreshKey((current) => current + 1)}
      />
      <Panel title="Show memories for">
        {projectRuns.error != null && <ErrorNotice code={errorCode(projectRuns.error)}>{runListErrorMessage(projectRuns.error)}</ErrorNotice>}
        {/* Wait for the run list before saying there are no runs. */}
        {runOptions.length === 0 && projectRuns.loading ? <Loading label="Loading runs" /> : runOptions.length === 0 ? (
          <EmptyState title="No runs yet" description="Memories belong to a run. Start a run in this project, then come back here."
            action={<Link className="v1-link-button" to={`/projects/${encodeURIComponent(projectId)}/orchestrations`}>Open Orchestrations</Link>} />
        ) : (
          <form className="v1-form v1-inline-form" onSubmit={setScope}>
            {/* Run and agent are picked from lists, not typed, to avoid mistyped IDs. */}
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
        {/* The session is bound to another run. Show a spinner while the silent switch
            runs, and a sign-in button only if it failed. */}
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
      {projectRuns.items.length > 0 && (
        <Panel title="Recent project runs" description="Browse project work history without entering run or session IDs.">
          <div className="v1-record-list">
            {projectRuns.items.map((item) => (
              <article className="v1-record" key={item.runId}>
                <div className="v1-record-title">
                  <div>
                    <h3>{item.label}</h3>
                    <p className="v1-muted">{runState(item.executionState).label} · {item.runId}</p>
                  </div>
                  <Link className="v1-link-button"
                    to={`/projects/${encodeURIComponent(projectId)}/runs/${encodeURIComponent(item.runId)}`}>
                    Open run
                  </Link>
                </div>
                <div className="v1-actions">
                  <Link className="v1-link-button"
                    to={`/projects/${encodeURIComponent(projectId)}/runs/${encodeURIComponent(item.runId)}/history?view=sessions`}>
                    Session tree
                  </Link>
                  <Link className="v1-link-button"
                    to={`/projects/${encodeURIComponent(projectId)}/runs/${encodeURIComponent(item.runId)}/history?view=activity`}>
                    Activity
                  </Link>
                </div>
              </article>
            ))}
          </div>
          <p className="v1-muted">
            Session tree shows a run's sessions. Activity shows addressed-message receipts and their routing details, not message text.
            Private conversations on Sessions are separate from project work history.
          </p>
        </Panel>
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
      // Use the shared workflow node card, so the run topology looks like the
      // project Flow page.
      type: 'workflow',
      position: { x: depth * 300, y: index * 150 },
      data: {
        def: { key: node.kind, label: node.kind, roleDescription: node.kind, Icon: iconForRole(node.kind) },
        state: {
          // Map session state to the card's three states: a busy session is
          // 'started', a completed one is 'completed', and anything else
          // (idle, unknown, cancelled, archived) shows as 'pending'.
          status: status?.activity === 'busy' ? 'started' : node.lifecycle === 'completed' ? 'completed' : 'pending',
          message: description,
        },
        // Show the real activity and lifecycle words instead of the card's generic label.
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
function eventSessionLabel(value: unknown): string {
  if (typeof value !== 'object' || value === null ||
    !('sessionId' in value) || typeof value.sessionId !== 'string')
    return 'unknown session';
  return value.sessionId;
}

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
    const sender = eventSessionLabel(payload.sender);
    const recipient = eventSessionLabel(payload.recipient);
    const sequence = typeof payload.threadSequence === 'number' ? ` · thread ${payload.threadSequence}` : '';
    const purpose = typeof payload.purpose === 'string' ? ` · ${payload.purpose}` : '';
    const status = typeof payload.status === 'string' ? ` · owner journal status ${payload.status}` : '';
    return `Addressed-message receipt ${id} · ${sender} → ${recipient}${sequence}${purpose}${status}; receipt is not proof of delivery, task completion, or approval.`;
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
  to: string;
  text: string;
  status: string;
}

/**
 * Renders run chat. Messages go to `targetSessionId`: the coordinator unless the user
 * selected another active session in the run tree. The authenticated user is the sender;
 * sent messages show on this page until it reloads.
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
  const targetName = target ? sessionName(target) : 'Coordinator';
  // Historical text comes from run-bound event references, not the local send receipt.
  const transcript = useTurnTranscript(journalEvents, projectId, runId, apiCall);

  const send = async (text: string, idempotencyKey: string) => {
    if (!target) return;
    setNotice(null);
    const result = await apiCall((token, tenantSelector) => gatewayClient.sendProjectRunMessage(
      token,
      projectId,
      runId,
      target.identity.sessionId === target.rootSessionId
        ? { text }
        : { text, targetSessionId: target.identity.sessionId },
      idempotencyKey,
      tenantSelector,
    ), { projectId, runId });
    setPending((items) => [...items, { id: result.ownerMessageId, to: targetName, text, status: result.status }]);
    setNotice('Message accepted by the addressed-message journal.');
  };

  return (
    <div className="v1-stack">
      {notice && <MessageBar intent="info"><MessageBarBody>{notice}</MessageBarBody></MessageBar>}
      <TranscriptPanel transcript={transcript} />
      <Panel title="Activity" description="Events from this run, oldest first. Message text is not stored in the event list.">
        <JournalList events={journalEvents} />
      </Panel>
      <Panel title={`Message the ${targetName.toLowerCase()}`} description="Your signed-in identity sends the message. By default it goes to the coordinator; choose an active session in the run tree to target it instead.">
        {!target ? (
          <p className="v1-muted">You can send a message when the selected run session is active.</p>
        ) : (
          <NewChatComposer
            onSubmit={send}
            blocked={snapshotBusy}
            draftScope={`${projectId}/${runId}/${target.identity.sessionId}`}
          />
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
  // Chat is the default tab when ?view= is missing or unknown.
  const tab: RunTab = isRunTab(requestedTab) ? requestedTab : 'chat';
  // Run data needs a sign-in bound to this exact project and run. If the current
  // session is bound elsewhere, try to switch without a prompt first.
  const authorized = isSameBinding(session?.binding, binding);
  const silentBind = useSilentRunSession(binding, authorized, ensureSession);
  // The clock only ticks once the run page is usable, for the elapsed-time chip.
  const now = useTickingNow(authorized);
  const snapshotScope = useMemo(() => ({
    projectId,
    runId,
    accessToken: session?.accessToken ?? '',
  }), [projectId, runId, session?.accessToken]);
  const activeSnapshotScope = useRef(snapshotScope);
  // The tenant is used for two things: as the selector for run requests (only
  // when the session is bound to this run) and as the key for the browser's
  // recent-run list.
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
    // Replace an unknown `view` value with the default tab. `replace` keeps the
    // bad URL out of the browser history.
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
    // The list is stored per tenant, so skip it until the tenant is known.
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
        {/* Show a loader while the silent sign-in runs. Ask the user only if it failed. */}
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
  // Every blocker (question, approval, plan, outcome) in every session counts
  // as one decision that waits for the user.
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

  // The run start time is the root session's creation time. It is NaN when
  // the tree has not loaded, and the header and elapsed chip then hide it.
  const rootNode = currentTree?.nodes.find((node) => node.identity.sessionId === currentTree.rootSessionId);
  const startedAt = rootNode ? Date.parse(rootNode.createdAt) : Number.NaN;
  // A finished run has no running "elapsed" counter.
  const finished = isRunFinished(currentRunStatus?.executionState);
  // Live-update state of the journal stream, shown as a chip.
  const streamLabel = journal.connected ? 'Live' : journal.loading ? 'Connecting' : 'Reconnecting';
  // Chat goes to the session selected in the run tree when it is active.
  // Otherwise it falls back to the root (coordinator) session.
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
        actions={<>
          {/* Stop shows only while the run is open; a stop reloads the run state. */}
          <StopRunButton
            projectId={projectId}
            runId={runId}
            state={currentRunStatus?.executionState}
            onStopped={() => void refreshRef.current()}
          />
          <Button appearance="secondary" aria-label="Refresh run" icon={<ArrowClockwiseRegular />} disabled={snapshotBusy} onClick={() => void refreshRef.current()}>Refresh</Button>
        </>}
      />
      {currentSnapshotMessage && <MessageBar intent="info"><MessageBarBody>{currentSnapshotMessage}</MessageBarBody></MessageBar>}
      {Object.entries(pageErrors).map(([key, message]) => (
        <ErrorNotice key={key} code={key}>{message}</ErrorNotice>
      ))}
      {(snapshotBusy || !snapshotIsCurrent) && !currentRunStatus && <Loading />}
      {currentRunStatus && (
        <>
          <div className="v1-run-strip" aria-label="Run progress">
            {/* Summary strip: state, session count, elapsed time (only while running), open decisions, and live-stream state. */}
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
            {/* Left: the run tree. Selecting a session there also picks the chat recipient. */}
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
            <DecisionList projectId={projectId} runId={runId} pinned={pinnedDecisionFromSearch(searchParams)} statuses={currentStatuses} decisions={currentDecisions} apiCall={apiCall} refresh={() => refreshRef.current()} snapshotBusy={snapshotBusy} />
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
              {/* Only runs that use a GitHub App connection have a branch or pull request to show. */}
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
              {/* Linked captures still require a session selected from the authorized run tree. */}
              <OutputCaptureBrowser projectId={projectId} runId={runId}
                initialCaptureId={searchParams.get('captureId') ?? undefined} />
              {/* Produced files are read from the root session, which every started run has. */}
              {currentRunStatus.rootSessionId && (
                <RunOutputCapturesPanel projectId={projectId} runId={runId} sessionId={currentRunStatus.rootSessionId} />
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
  // Fall back to the raw kind if the server sends a kind this client does not know.
  return SESSION_KIND_LABELS[node.kind] ?? node.kind;
}

/** Formats a duration in the 0.x elapsed style, for example "1h 4m" or "42s". */
function formatElapsed(milliseconds: number): string {
  // Clamp at zero: a client clock that runs behind the server can make the
  // difference negative for a moment.
  const seconds = Math.max(0, Math.floor(milliseconds / 1000));
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  // Show only the two largest units, so the text stays short.
  if (hours > 0) return `${hours}h ${minutes}m`;
  if (minutes > 0) return `${minutes}m ${seconds % 60}s`;
  return `${seconds}s`;
}

/** Current time that updates every second while `enabled`, for elapsed counters. */
function useTickingNow(enabled: boolean): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    // No timer while disabled, so a hidden or unauthorized page does not re-render every second.
    if (!enabled) return undefined;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    // Stop the timer when disabled or unmounted.
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
  // Remembers which run's silent attempt failed. Storing the key (not a boolean)
  // means a different run gets its own fresh attempt.
  const [failedKey, setFailedKey] = useState<string | null>(null);
  useEffect(() => {
    // Skip when there is no run, the session is already bound to it, or this run
    // already failed. The last check stops an endless retry loop.
    // A held session activates at once; a new one is requested once, and concurrent calls share it.
    if (!binding || authorized || failedKey === key) return;
    void ensureSession(binding).then((ok) => {
      // On success the auth context changes and `authorized` becomes true, so
      // only the failure needs local state.
      if (!ok) setFailedKey(key);
    });
  }, [authorized, binding, ensureSession, failedKey, key]);
  // Leaving the run makes the unbound session active again for project pages.
  useEffect(() => () => { void ensureSession(null); }, [ensureSession]);
  // 'pending' also covers "already authorized"; callers check `authorized` first.
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
    // Group sessions by parent ID. Sessions with no parent use the '' key.
    const children = new Map<string, SessionTreeNode[]>();
    for (const node of tree.nodes) {
      const parent = node.parentSessionId ?? '';
      children.set(parent, [...(children.get(parent) ?? []), node]);
    }
    // Depth-first walk from the root: each parent comes right before its
    // children, and the depth sets the indent.
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
  /** One short state word per session, in priority order. */
  const stateText = (node: SessionTreeNode) => {
    const status = statuses[node.identity.sessionId];
    // A session waiting for the user matters most, so it wins over every other state.
    if (status && status.blockers.length > 0) return 'Needs input';
    // A session that is no longer active shows its final lifecycle.
    if (node.lifecycle !== 'active') return node.lifecycle === 'completed' ? 'Done' : node.lifecycle === 'cancelled' ? 'Cancelled' : 'Archived';
    // An active session shows its activity. Its status may be missing if that read failed.
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

/** Reads one run's tree or event journal with project-read authority, not the accepting run actor. */
function ProjectRunHistoryPage() {
  const { projectId = '', runId = '' } = useParams();
  const { apiCall, authorize, ensureSession } = useAuth();
  const [searchParams, setSearchParams] = useSearchParams();
  const requestedView = searchParams.get('view');
  const view = requestedView === 'activity' ? 'activity' : 'sessions';
  const [tree, setTree] = useState<SessionTreeSnapshot | null>(null);
  const [events, setEvents] = useState<SessionEventEnvelope[]>([]);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [selectedSession, setSelectedSession] = useState('');
  const [loading, setLoading] = useState(true);
  const [loadedKey, setLoadedKey] = useState('');
  const [needsSignIn, setNeedsSignIn] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const requestGeneration = useRef(0);
  const historyKey = JSON.stringify([projectId, runId, view]);

  useEffect(() => {
    if (requestedView === 'sessions' || requestedView === 'activity') return;
    const normalized = new URLSearchParams(searchParams);
    normalized.set('view', 'sessions');
    setSearchParams(normalized, { replace: true });
  }, [requestedView, searchParams, setSearchParams]);

  const fetchHistory = useCallback(async (cursor: string | null) => {
    if (!await ensureSession(null, false)) return null;
    if (view === 'sessions') {
      const result = await apiCall((token, tenantSelector) =>
        gatewayClient.getProjectRunHistorySessions(
          token, projectId, runId, tenantSelector), null);
      // Reject cross-run data even if a project-scoped endpoint returns a broader result.
      if (!Array.isArray(result.nodes) ||
          result.nodes.some((node) =>
            node.identity.projectId !== projectId || node.identity.runId !== runId) ||
          !result.nodes.some((node) => node.identity.sessionId === result.rootSessionId))
        throw new Error('The service returned a session tree for a different run.');
      return { kind: 'sessions' as const, result };
    }
    const page = await apiCall((token, tenantSelector) =>
      gatewayClient.replayProjectRunHistoryEvents(
        token, projectId, runId, cursor, 100, tenantSelector), null);
    // Reject cross-run data even if a project-scoped endpoint returns a broader result.
    if (!Array.isArray(page.events) ||
        page.events.some((event) =>
          event.identity.projectId !== projectId || event.identity.runId !== runId))
      throw new Error('The service returned activity for a different run.');
    return { kind: 'activity' as const, page };
  }, [apiCall, ensureSession, projectId, runId, view]);

  const applyHistory = useCallback((
    history: Awaited<ReturnType<typeof fetchHistory>>,
    cursor: string | null,
  ) => {
    if (history === null) {
      setNeedsSignIn(true);
      return;
    }
    setNeedsSignIn(false);
    if (history.kind === 'sessions') {
      setTree(history.result);
      setSelectedSession(history.result.rootSessionId);
    } else {
      setEvents((current) => {
        if (cursor === null) return history.page.events;
        // Keep replay pages disjoint when the same event arrives on adjacent cursors.
        const unique = new Map(current.concat(history.page.events).map((event) => [event.eventId, event]));
        return [...unique.values()];
      });
      setNextCursor(history.page.nextCursor ?? null);
      setHasMore(history.page.hasMore);
    }
  }, []);

  const load = useCallback(async (cursor: string | null = null) => {
    const generation = ++requestGeneration.current;
    setLoading(true);
    setError(null);
    if (cursor === null) {
      setTree(null);
      setEvents([]);
      setNextCursor(null);
      setHasMore(false);
    }
    try {
      const history = await fetchHistory(cursor);
      // A response from an earlier run or tab must not replace the current history.
      if (generation !== requestGeneration.current) return;
      applyHistory(history, cursor);
      if (cursor === null) setLoadedKey(historyKey);
    } catch (caught) {
      if (generation === requestGeneration.current) {
        setError(caught);
        if (cursor === null) setLoadedKey(historyKey);
      }
    } finally {
      if (generation === requestGeneration.current) setLoading(false);
    }
  }, [applyHistory, fetchHistory, historyKey]);

  useEffect(() => {
    const generation = ++requestGeneration.current;
    let active = true;
    void fetchHistory(null).then((history) => {
      if (!active || generation !== requestGeneration.current) return;
      applyHistory(history, null);
      setError(null);
      setLoadedKey(historyKey);
    }).catch((caught: unknown) => {
      if (!active || generation !== requestGeneration.current) return;
      setError(caught);
      setLoadedKey(historyKey);
    }).finally(() => {
      if (active && generation === requestGeneration.current) setLoading(false);
    });
    return () => { active = false; };
  }, [applyHistory, fetchHistory, historyKey]);

  const isLoading = loading || loadedKey !== historyKey;
  const selectedNode = tree?.nodes.find((node) => node.identity.sessionId === selectedSession) ?? null;
  const setView = (nextView: string) => {
    const normalized = new URLSearchParams(searchParams);
    normalized.set('view', nextView);
    setSearchParams(normalized);
  };

  return (
    <div className="v1-stack">
      <PageHeading title="Run history" description="Project-authorized sessions and activity for this run." page="Memories" />
      <Link className="v1-link-button" to={`/projects/${encodeURIComponent(projectId)}/memories`}>Back to Memories</Link>
      <TabList
        selectedValue={view}
        onTabSelect={(_, data) => setView(String(data.value))}
        aria-label="Run history"
      >
        <Tab value="sessions">Session tree</Tab>
        <Tab value="activity">Activity</Tab>
      </TabList>
      {needsSignIn && (
        <Panel title="Sign in for project history">
          <Button appearance="primary" onClick={() => void authorize(null).then(() => load(null))}>
            Sign in
          </Button>
        </Panel>
      )}
      {error != null && <ErrorNotice code={errorCode(error)}>{errorMessage(error)}</ErrorNotice>}
      {needsSignIn ? null : isLoading ? <Loading label="Loading run history" /> : view === 'sessions' ? (
        <div className="v1-run-body">
          <RunTree tree={tree} statuses={{}} selectedSession={selectedSession} onSelect={setSelectedSession} />
          <section className="v1-run-pane" aria-label="Selected session">
            <Panel title={selectedNode ? sessionName(selectedNode) : 'Session'}>
              {selectedNode ? (
                <dl className="v1-detail-list">
                  <div><dt>Lifecycle</dt><dd>{selectedNode.lifecycle}</dd></div>
                  <div><dt>Created</dt><dd>{new Date(selectedNode.createdAt).toLocaleString()}</dd></div>
                  {selectedNode.parentSessionId && (
                    <div><dt>Parent</dt><dd>{selectedNode.parentSessionId}</dd></div>
                  )}
                </dl>
              ) : <p className="v1-muted">No session history is available.</p>}
            </Panel>
          </section>
        </div>
      ) : (
        <Panel title="Activity" description="Committed events, oldest first. Message text is not included.">
          <JournalList events={events} />
          {hasMore && (
            <Button
              appearance="secondary"
              disabled={isLoading}
              onClick={() => void load(nextCursor)}
            >
              Load more
            </Button>
          )}
        </Panel>
      )}
    </div>
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
  // A full-page sign-in for a new chat returns here. Go back to New session,
  // which starts the saved chat; otherwise open Projects.
  if (session) return <Navigate to={readPendingRun() ? '/assistant' : '/projects'} replace />;
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
      {/* App-wide pages. Pages without a v1 service show an explanation instead of failing. */}
      <Route path="/" element={<Navigate to="/overview" replace />} />
      <Route path="/overview" element={<Shell><OverviewPage /></Shell>} />
      <Route path="/sessions" element={<Shell><SessionsPage /></Shell>} />
      <Route path="/assistant" element={<Shell><NewSessionPage /></Shell>} />
      <Route path="/console" element={<Navigate to="/assistant" replace />} />
      <Route path="/settings" element={<Shell><AccountSettingsPage /></Shell>} />
      <Route path="/platform-settings" element={<Shell><PlatformSettingsPage /></Shell>} />
      <Route path="/auth/callback" element={<CallbackPage />} />
      <Route path="/projects" element={<Shell><ProjectsPage /></Shell>} />
      <Route path="/projects/:projectId" element={<Shell><DashboardPage /></Shell>} />
      {/* Settings, Skills, and Agents are three views of the same configuration page. */}
      <Route path="/projects/:projectId/settings" element={<Shell><ProjectConfigurationPage /></Shell>} />
      <Route path="/projects/:projectId/skills" element={<Shell><ProjectConfigurationPage view="skills" /></Shell>} />
      <Route path="/projects/:projectId/team" element={<Shell><ProjectConfigurationPage view="agents" /></Shell>} />
      <Route path="/projects/:projectId/team/cast" element={<Shell><CastTeamPage /></Shell>} />
      <Route path="/projects/:projectId/team/:agentName/memory" element={<AgentMemoryRedirect />} />
      {/* "knowledge" is the older address for Memories; both show the same page. */}
      <Route path="/projects/:projectId/knowledge" element={<Shell><KnowledgePage /></Shell>} />
      <Route path="/projects/:projectId/memories" element={<Shell><KnowledgePage /></Shell>} />
      <Route path="/projects/:projectId/board" element={<Shell><BoardPage /></Shell>} />
      <Route path="/projects/:projectId/orchestrations" element={<Shell><OrchestrationsPage /></Shell>} />
      <Route path="/projects/:projectId/sessions" element={<Navigate to="/sessions" replace />} />
      <Route path="/projects/:projectId/runs/:runId/history" element={<Shell><ProjectRunHistoryPage /></Shell>} />
      <Route path="/projects/:projectId/runs/:runId" element={<Shell><RunPage /></Shell>} />
      <Route path="/projects/:projectId/orchestrations/:runId" element={<CoordinatorRunRedirect />} />
      {/* 0.x operations pages. Each shows the parts current reads support and names what is missing. */}
      <Route path="/projects/:projectId/flow" element={<Shell><FlowPage /></Shell>} />
      <Route path="/projects/:projectId/workflows" element={<Shell><WorkflowsPage /></Shell>} />
      <Route path="/projects/:projectId/workspace" element={<Shell><WorkspacePage /></Shell>} />
      <Route path="/projects/:projectId/observability/*" element={<Shell><ProjectObservabilityPage /></Shell>} />
      <Route path="/projects/:projectId/diagnostics" element={<Shell><DiagnosticsPage /></Shell>} />
      <Route path="/projects/:projectId/heartbeat" element={<Shell><HeartbeatPage /></Shell>} />
      <Route path="/projects/:projectId/cluster" element={<Shell><ClusterPage /></Shell>} />
      {/* The cross-project summary has no read yet; each project has its own Observability page. */}
      <Route path="/observability/*" element={<Shell><UnavailablePage title="Observability" description="A summary across all projects is not available in this version. Open a project and choose Observability for its runs and usage." /></Shell>} />
      {/* Unknown addresses show "Page not found" inside the app shell, so navigation stays available. */}
      <Route path="*" element={<Shell><UnavailablePage title="Page not found" description="This address does not match an Agentweaver page. Use the navigation to choose a destination." /></Shell>} />
    </Routes>
  );
}

/** Preserves older orchestration links by redirecting them to the current run route. */
function CoordinatorRunRedirect() {
  const { projectId = '', runId = '' } = useParams();
  const location = useLocation();
  // Keep the query and hash (for example ?view=approvals) so old links open the same tab.
  return <Navigate to={`/projects/${encodeURIComponent(projectId)}/runs/${encodeURIComponent(runId)}${location.search}${location.hash}`} replace />;
}

/** Explains that a page is not available in this version, using the standard empty state. */
function UnavailablePage({ title, description }: { title: string; description: string }) {
  // Inside a project, link back to its dashboard; otherwise link to the project list.
  const { projectId } = useParams();
  return <>
    <PageHeading title={title} />
    <EmptyState title={title === 'Page not found' ? 'Choose a destination' : 'Not available in this version'} description={description}
      action={<Link className="v1-link-button" to={projectId ? `/projects/${encodeURIComponent(projectId)}` : '/projects'}>{projectId ? 'Open Dashboard' : 'Open Projects'}</Link>} />
  </>;
}

/**
 * Account-level settings: sign out and the GitHub App connection. Each connection
 * panel runs its own server checks; this page stores no identity data itself.
 */
function AccountSettingsPage() {
  const { signOut } = useAuth();
  return <>
    <PageHeading title="Account settings" />
    <Panel title="Sign-in"><Button onClick={signOut}>Sign out</Button></Panel>
    <ProjectRepoAppConnectionPanel />
    <p>Choose a project in the navigation to manage its Copilot connection in Project settings.</p>
  </>;
}

/**
 * Redirects the older per-agent memory address to the Memories page. The agent
 * name from the old URL only pre-fills the agent choice; it grants no access,
 * and the server still checks every request.
 */
function AgentMemoryRedirect() {
  const { projectId = '', agentName = '' } = useParams();
  const location = useLocation();
  const search = new URLSearchParams(location.search);
  // An explicit `agentId` in the query wins over the name from the path.
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
