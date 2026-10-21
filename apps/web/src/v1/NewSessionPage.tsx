/**
 * New session page in the 0.x layout: pick a project, write the first message,
 * and land on the new run's Chat tab. Creating the run saves the message. The
 * run then needs a sign-in for that exact run, which may leave the page, so
 * the created run is kept in session storage and resumed when the page loads.
 */
import { Button, Field, Input, MessageBar, MessageBarActions, MessageBarBody, Select, makeStyles, tokens } from '@fluentui/react-components';
import { useCallback, useEffect, useRef, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { EmptyState } from '../components/ui/States';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import type { ProjectSummary } from './contracts';
import { errorMessage } from './errors';
import { NewChatComposer } from './NewChatComposer';
import { ErrorNotice, Loading, PageHeading } from './pageParts';
import type { PendingRun } from './pendingRun';
import {
  clearPendingRun,
  createRunError,
  needsNewCreateKey,
  readPendingRun,
  resumePendingRun,
  runChatPath,
  savePendingRun,
} from './pendingRun';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXL },
});

/** Where the pending run is in the bind-and-start flow. */
type Phase = 'idle' | 'starting' | 'sign-in' | 'error';

/** Renders the project picker and composer that start a new chat. */
export function NewSessionPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const { session, apiCall, authorize, ensureSession } = useAuth();
  // 0.x links use `?project=`; v1 links may use `?projectId=`.
  const requestedProject = searchParams.get('projectId') ?? searchParams.get('project') ?? '';
  const [projects, setProjects] = useState<ProjectSummary[] | null>(null);
  const [projectsError, setProjectsError] = useState<unknown>(null);
  const [projectId, setProjectId] = useState(requestedProject);
  // Follow a new ?project= link while this page is open (for example New
  // session from another project's rail). Adjusting state during render avoids
  // an extra effect pass.
  const [lastRequestedProject, setLastRequestedProject] = useState(requestedProject);
  if (requestedProject !== lastRequestedProject) {
    setLastRequestedProject(requestedProject);
    if (requestedProject) setProjectId(requestedProject);
  }
  const [title, setTitle] = useState('');
  const [pending, setPending] = useState<PendingRun | null>(() => readPendingRun());
  const [phase, setPhase] = useState<Phase>('idle');
  const [resumeError, setResumeError] = useState<unknown>(null);
  // Maps a composer key to the key actually sent. After a 409 the same key
  // would fail again, so a retry of that draft is sent with a new key.
  const createKeys = useRef(new Map<string, string>());
  const resuming = useRef(false);
  const initialized = useRef(false);

  /** Binds and starts a pending run, then opens its Chat tab. Returns true when it navigated. */
  const continuePending = useCallback(async (run: PendingRun): Promise<boolean> => {
    // Ignore a second call while one is running (for example a double click).
    if (resuming.current) return false;
    resuming.current = true;
    setPhase('starting');
    setResumeError(null);
    const binding = { projectId: run.projectId, runId: run.runId };
    try {
      // Save the caller's private conversation before switching to the run token.
      // Repeating after a redirect or failed start returns the same entry.
      await apiCall((token, tenant) => gatewayClient.bindChiefOfStaffConversation(
        token, { projectId: run.projectId, runId: run.runId }, tenant), null);
      const outcome = await resumePendingRun({
        ensureRunSession: () => ensureSession(binding),
        // Start needs the session bound to this run.
        start: () => apiCall((token, tenant) => gatewayClient.startProjectRun(token, run.projectId, run.runId, tenant), binding),
      });
      if (outcome === 'needs-sign-in') {
        setPhase('sign-in');
        return false;
      }
      // Started, or already ended: the run page shows the current state either way.
      setPending(null);
      setPhase('idle');
      navigate(runChatPath(run.projectId, run.runId));
      return true;
    } catch (reason) {
      // The resume helper clears records that cannot be resumed; mirror that.
      if (!readPendingRun()) setPending(null);
      setResumeError(reason);
      setPhase('error');
      return false;
    } finally {
      resuming.current = false;
    }
  }, [apiCall, ensureSession, navigate]);

  /** Loads the projects the user can see with the session not bound to a run. */
  const loadProjects = useCallback(async () => {
    try {
      // After a full-page sign-in for a run, only that run's session is held.
      // Get the other one without a prompt, but keep the active session.
      if (!await ensureSession(null, false))
        throw new Error('Your sign-in expired. Sign in again to see your projects.');
      const list = await apiCall((token, tenant) => gatewayClient.listProjects(token, tenant), null);
      setProjects(list);
      setProjectsError(null);
      // Keep the requested project when it is in the list; otherwise preselect
      // the only active project so a single-project user can just type.
      setProjectId((current) => {
        if (current && list.some((item) => item.projectId === current)) return current;
        const active = list.filter((item) => item.state === 'active');
        return active.length === 1 ? active[0].projectId : '';
      });
    } catch (reason) {
      setProjectsError(reason);
    }
  }, [apiCall, ensureSession]);

  // First load: resume a pending run first, then read projects. They run one
  // after the other because two sign-in attempts at once cancel each other.
  // The zero-delay timer keeps state updates out of the effect body.
  useEffect(() => {
    if (initialized.current) return undefined;
    const timer = window.setTimeout(() => {
      initialized.current = true;
      void (async () => {
        const run = readPendingRun();
        // Skip the project list when the page already moved to the run.
        if (run && await continuePending(run)) return;
        await loadProjects();
      })();
    }, 0);
    return () => window.clearTimeout(timer);
  }, [continuePending, loadProjects]);

  // After a manual sign-in in a popup or consent dialog, the run's session
  // becomes active. Continue then, without another sign-in attempt.
  useEffect(() => {
    if (phase !== 'sign-in' || !pending) return undefined;
    if (session?.binding?.projectId !== pending.projectId || session.binding.runId !== pending.runId) return undefined;
    const timer = window.setTimeout(() => { void continuePending(pending); }, 0);
    return () => window.clearTimeout(timer);
  }, [continuePending, pending, phase, session]);

  /** Creates the run with the saved first message, then binds and starts it. */
  const submit = async (text: string, composerKey: string) => {
    if (!projectId) throw new Error('Choose a project first.');
    const idempotencyKey = createKeys.current.get(composerKey) ?? composerKey;
    let created;
    try {
      // Read the current settings revision right before create, so the run
      // starts with the settings the user sees now.
      const configuration = await apiCall(
        (token, tenant) => gatewayClient.getProjectConfiguration(token, projectId, tenant),
        null,
      );
      const trimmedTitle = title.trim();
      created = await apiCall(
        (token, tenant) => gatewayClient.createProjectRun(token, projectId, {
          firstMessage: text,
          expectedProjectConfigRevision: configuration.revision,
          ...(trimmedTitle ? { title: trimmedTitle } : {}),
        }, idempotencyKey, tenant),
        null,
      );
    } catch (reason) {
      // A conflict cannot succeed with the same key; the next send of this
      // draft gets a new one and re-reads the settings.
      if (needsNewCreateKey(reason)) createKeys.current.set(composerKey, crypto.randomUUID());
      throw createRunError(reason);
    }
    createKeys.current.delete(composerKey);
    // Save before signing in for the run, because that sign-in can leave the page.
    const run: PendingRun = { projectId, runId: created.runId, createdAt: new Date().toISOString() };
    savePendingRun(run);
    setPending(run);
    setTitle('');
    // The message is saved in the run now, so the composer can clear. Any
    // later problem shows in the pending panel, which can retry.
    void continuePending(run);
  };

  /** Drops the pending run from this page. The run stays listed in Sessions. */
  const dismissPending = () => {
    clearPendingRun();
    setPending(null);
    setPhase('idle');
    setResumeError(null);
  };

  const pendingProjectName = pending
    ? projects?.find((item) => item.projectId === pending.projectId)?.name ?? pending.projectId
    : '';
  const activeProjects = projects?.filter((item) => item.state === 'active') ?? [];

  return (
    <div className={styles.root}>
      <PageHeading
        title="New session"
        description="Choose a project and write your first message. The chat opens when it starts."
        breadcrumb={false}
      />
      {pending && phase === 'starting' && <Loading label="Starting your chat…" />}
      {pending && phase === 'sign-in' && (
        <MessageBar intent="warning">
          <MessageBarBody>Your chat in {pendingProjectName} is saved. Sign in to start it.</MessageBarBody>
          <MessageBarActions>
            {/* A click opens the sign-in window; browsers block it outside a click. */}
            <Button appearance="primary" size="small" onClick={() => void authorize({ projectId: pending.projectId, runId: pending.runId })}>Sign in</Button>
            <Button size="small" onClick={dismissPending}>Dismiss</Button>
          </MessageBarActions>
        </MessageBar>
      )}
      {phase === 'error' && resumeError != null && (
        <MessageBar intent="error" role="alert">
          <MessageBarBody>{errorMessage(resumeError)}</MessageBarBody>
          {pending && (
            <MessageBarActions>
              <Button appearance="primary" size="small" onClick={() => void continuePending(pending)}>Try again</Button>
              <Button size="small" onClick={dismissPending}>Dismiss</Button>
            </MessageBarActions>
          )}
        </MessageBar>
      )}
      {projectsError != null && <ErrorNotice>{errorMessage(projectsError)}</ErrorNotice>}
      {projects === null && projectsError == null && <Loading label="Loading projects…" />}
      {projects !== null && activeProjects.length === 0 && (
        <EmptyState title="No projects yet" description="Create a project in Projects, then start a chat." />
      )}
      {projects !== null && activeProjects.length > 0 && (
        // A pending chat must start or be dismissed before another one.
        <NewChatComposer
          onSubmit={submit}
          blocked={!projectId || pending !== null}
          placeholder="Message the project's agents"
          submitLabel="Start chat"
        >
          <Field label="Project" required>
            <Select value={projectId} onChange={(_, data) => setProjectId(data.value)}>
              <option value="" disabled>Choose a project</option>
              {activeProjects.map((item) => (
                <option key={item.projectId} value={item.projectId}>{item.name}</option>
              ))}
            </Select>
          </Field>
          <Field label="Title" hint="Optional. When empty, the start of your message is used.">
            <Input value={title} maxLength={256} onChange={(_, data) => setTitle(data.value)} />
          </Field>
        </NewChatComposer>
      )}
    </div>
  );
}
