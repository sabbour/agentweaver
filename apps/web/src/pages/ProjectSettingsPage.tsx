import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import {
  Button,
  Checkbox,
  Divider,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Spinner,
  Switch,
  Text,
  Title2,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { Project, SandboxPolicy, UpdateProjectProviderSettingsRequest } from '../api/types';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    maxWidth: '640px',
  },
  breadcrumb: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
  },
  breadcrumbLink: {
    color: tokens.colorBrandForeground1,
    textDecoration: 'none',
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  actions: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
  },
  dangerSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalM,
    border: `1px solid ${tokens.colorPaletteRedBorder2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  listBox: {
    backgroundColor: tokens.colorNeutralBackground3,
    borderRadius: tokens.borderRadiusMedium,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
  },
  listItem: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
    padding: `${tokens.spacingVerticalXS} 0`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
    ':last-child': {
      borderBottom: 'none',
    },
  },
  emptyNote: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    fontStyle: 'italic',
  },
});

export function ProjectSettingsPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();
  const navigate = useNavigate();

  const [project, setProject] = useState<Project | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);

  // Model settings
  const [copilotModel, setCopilotModel] = useState('');
  const [savingModel, setSavingModel] = useState(false);
  const [modelError, setModelError] = useState<string | null>(null);
  const [modelSuccess, setModelSuccess] = useState(false);

  // Rename
  const [newName, setNewName] = useState('');
  const [savingRename, setSavingRename] = useState(false);
  const [renameError, setRenameError] = useState<string | null>(null);
  const [renameSuccess, setRenameSuccess] = useState(false);

  // Relink
  const [newDir, setNewDir] = useState('');
  const [savingRelink, setSavingRelink] = useState(false);
  const [relinkError, setRelinkError] = useState<string | null>(null);
  const [relinkSuccess, setRelinkSuccess] = useState(false);

  // Delete
  const [deleteConfirmed, setDeleteConfirmed] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);

  // Sandbox policy
  const [sandboxPolicy, setSandboxPolicy] = useState<SandboxPolicy | null>(null);
  const [sandboxFetched, setSandboxFetched] = useState(false);
  const [sandboxError, setSandboxError] = useState<string | null>(null);
  const [savingSandbox, setSavingSandbox] = useState(false);
  const [sandboxSaveError, setSandboxSaveError] = useState<string | null>(null);
  const [sandboxSaveSuccess, setSandboxSaveSuccess] = useState(false);
  const sandboxLoading = project !== null && !sandboxFetched;

  useEffect(() => {
    if (!projectId) return;
    let cancelled = false;
    apiClient.getProject(projectId)
      .then((p) => {
        if (!cancelled) {
          setProject(p);
          setCopilotModel(p.default_model_github_copilot ?? '');
          setNewName(p.name);
          setNewDir(p.working_directory);
        }
      })
      .catch((err) => {
        if (!cancelled) setLoadError(
          err instanceof ApiError
            ? `API error ${err.status}: ${err.body}`
            : err instanceof Error ? err.message : String(err),
        );
      })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [projectId]);

  const handleSaveModel = async () => {
    if (!projectId) return;
    setSavingModel(true);
    setModelError(null);
    setModelSuccess(false);
    try {
      const req: UpdateProjectProviderSettingsRequest = {};
      req.default_provider = 'github-copilot';
      if (copilotModel.trim()) req.default_model_github_copilot = copilotModel.trim();
      await apiClient.updateProjectProviderSettings(projectId, req);
      setModelSuccess(true);
    } catch (err) {
      setModelError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error ? err.message : String(err),
      );
    } finally {
      setSavingModel(false);
    }
  };

  useEffect(() => {
    if (!project?.working_directory) return;
    let cancelled = false;
    apiClient.getSandboxPolicy(project.working_directory)
      .then((p) => {
        if (!cancelled) {
          setSandboxPolicy(p);
          setSandboxFetched(true);
        }
      })
      .catch((err) => {
        if (!cancelled) {
          setSandboxFetched(true);
          setSandboxError(
            err instanceof ApiError
              ? `API error ${err.status}: ${err.body}`
              : err instanceof Error ? err.message : String(err),
          );
        }
      });
    return () => { cancelled = true; };
  }, [project?.working_directory]);

  const handleSaveSandbox = async () => {
    if (!sandboxPolicy) return;
    setSavingSandbox(true);
    setSandboxSaveError(null);
    setSandboxSaveSuccess(false);
    try {
      const updated = await apiClient.updateSandboxPolicy({
        repository_path: sandboxPolicy.repository_path,
        shell_enabled: sandboxPolicy.shell_enabled,
        direct: sandboxPolicy.direct,
        network_enabled: sandboxPolicy.network_enabled,
      });
      setSandboxPolicy(updated);
      setSandboxSaveSuccess(true);
    } catch (err) {
      setSandboxSaveError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error ? err.message : String(err),
      );
    } finally {
      setSavingSandbox(false);
    }
  };

  const handleRename = async () => {
    if (!projectId || !newName.trim()) return;
    setSavingRename(true);
    setRenameError(null);
    setRenameSuccess(false);
    try {
      await apiClient.renameProject(projectId, newName.trim());
      setProject((prev) => prev ? { ...prev, name: newName.trim() } : prev);
      setRenameSuccess(true);
    } catch (err) {
      setRenameError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error ? err.message : String(err),
      );
    } finally {
      setSavingRename(false);
    }
  };

  const handleRelink = async () => {
    if (!projectId || !newDir.trim()) return;
    setSavingRelink(true);
    setRelinkError(null);
    setRelinkSuccess(false);
    try {
      await apiClient.relinkProject(projectId, newDir.trim());
      setProject((prev) => prev ? { ...prev, working_directory: newDir.trim(), available: true } : prev);
      setRelinkSuccess(true);
    } catch (err) {
      setRelinkError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error ? err.message : String(err),
      );
    } finally {
      setSavingRelink(false);
    }
  };

  const handleDelete = async () => {
    if (!projectId || !deleteConfirmed) return;
    setDeleting(true);
    setDeleteError(null);
    try {
      await apiClient.deleteProject(projectId);
      navigate('/');
    } catch (err) {
      setDeleteError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error ? err.message : String(err),
      );
    } finally {
      setDeleting(false);
    }
  };

  if (!projectId) return null;

  return (
    <div className={styles.root}>
      <div className={styles.breadcrumb}>
        <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
        <span>/</span>
        <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>{project?.name ?? projectId}</Link>
        <span>/</span>
        <span>Settings</span>
      </div>

      <Title2>Project settings</Title2>

      {loading && <Spinner label="Loading project" />}

      {loadError && (
        <MessageBar intent="error">
          <MessageBarBody>{loadError}</MessageBarBody>
        </MessageBar>
      )}

      {project && (
        <>
          <Divider />

          <div className={styles.section}>
            <Title3>Model settings</Title3>
            <Field label="GitHub Copilot model">
              <Input value={copilotModel} onChange={(_, v) => setCopilotModel(v.value)} placeholder="e.g. gpt-4o" />
            </Field>
            <div className={styles.actions}>
              <Button appearance="primary" disabled={savingModel} onClick={() => void handleSaveModel()}>
                {savingModel ? 'Saving' : 'Save'}
              </Button>
              {savingModel && <Spinner size="extra-tiny" aria-hidden="true" />}
            </div>
            {modelError && (
              <MessageBar intent="error"><MessageBarBody>{modelError}</MessageBarBody></MessageBar>
            )}
            {modelSuccess && (
              <MessageBar intent="success"><MessageBarBody>Model settings saved.</MessageBarBody></MessageBar>
            )}
          </div>

          <Divider />

          <div className={styles.section}>
            <Title3>Sandbox policy</Title3>
            {sandboxLoading && <Spinner size="extra-tiny" label="Loading policy" />}
            {sandboxError && (
              <MessageBar intent="error"><MessageBarBody>{sandboxError}</MessageBarBody></MessageBar>
            )}
            {sandboxPolicy && (
              <>
                <Field label="Shell execution">
                  <Switch
                    label={sandboxPolicy.shell_enabled ? 'Enabled' : 'Disabled'}
                    checked={sandboxPolicy.shell_enabled}
                    onChange={(_, data) =>
                      setSandboxPolicy((prev) => prev ? { ...prev, shell_enabled: data.checked } : prev)
                    }
                  />
                </Field>
                <Field label="Direct execution (no sandbox isolation)">
                  <Switch
                    label={sandboxPolicy.direct ? 'On — commands run on host shell directly' : 'Off — uses bwrap/mxc isolation'}
                    checked={sandboxPolicy.direct}
                    onChange={(_, data) =>
                      setSandboxPolicy((prev) => prev ? { ...prev, direct: data.checked } : prev)
                    }
                  />
                </Field>
                <Field label="Outbound network">
                  <Switch
                    label={sandboxPolicy.network_enabled ? 'Enabled' : 'Blocked'}
                    checked={sandboxPolicy.network_enabled}
                    onChange={(_, data) =>
                      setSandboxPolicy((prev) => prev ? { ...prev, network_enabled: data.checked } : prev)
                    }
                  />
                </Field>
                <Field label="Allowed repository roots">
                  <div className={styles.listBox}>
                    {sandboxPolicy.allowed_repository_roots.length === 0 ? (
                      <Text className={styles.emptyNote}>None configured</Text>
                    ) : (
                      sandboxPolicy.allowed_repository_roots.map((root, i) => (
                        <div key={i} className={styles.listItem}>{root}</div>
                      ))
                    )}
                  </div>
                </Field>
                <Field label="Blocked command patterns">
                  <div className={styles.listBox}>
                    {sandboxPolicy.destructive_command_patterns.length === 0 ? (
                      <Text className={styles.emptyNote}>None configured</Text>
                    ) : (
                      sandboxPolicy.destructive_command_patterns.map((pat, i) => (
                        <div key={i} className={styles.listItem}>{pat}</div>
                      ))
                    )}
                  </div>
                </Field>
                <div className={styles.actions}>
                  <Button appearance="primary" disabled={savingSandbox} onClick={() => void handleSaveSandbox()}>
                    {savingSandbox ? 'Saving' : 'Save'}
                  </Button>
                  {savingSandbox && <Spinner size="extra-tiny" aria-hidden="true" />}
                </div>
                {sandboxSaveError && (
                  <MessageBar intent="error"><MessageBarBody>{sandboxSaveError}</MessageBarBody></MessageBar>
                )}
                {sandboxSaveSuccess && (
                  <MessageBar intent="success"><MessageBarBody>Sandbox policy saved.</MessageBarBody></MessageBar>
                )}
              </>
            )}
          </div>

          <Divider />

          <div className={styles.section}>
            <Title3>Rename project</Title3>
            <Field label="Name">
              <Input value={newName} onChange={(_, v) => setNewName(v.value)} />
            </Field>
            <div className={styles.actions}>
              <Button
                appearance="primary"
                disabled={savingRename || !newName.trim() || newName.trim() === project.name}
                onClick={() => void handleRename()}
              >
                {savingRename ? 'Saving' : 'Save'}
              </Button>
              {savingRename && <Spinner size="extra-tiny" aria-hidden="true" />}
            </div>
            {renameError && (
              <MessageBar intent="error"><MessageBarBody>{renameError}</MessageBarBody></MessageBar>
            )}
            {renameSuccess && (
              <MessageBar intent="success"><MessageBarBody>Project renamed.</MessageBarBody></MessageBar>
            )}
          </div>

          <Divider />

          <div className={styles.section}>
            <Title3>Relink working directory</Title3>
            <Text>Update the local path if the project folder has moved.</Text>
            <Field label="Working directory">
              <Input value={newDir} onChange={(_, v) => setNewDir(v.value)} />
            </Field>
            <div className={styles.actions}>
              <Button
                appearance="primary"
                disabled={savingRelink || !newDir.trim() || newDir.trim() === project.working_directory}
                onClick={() => void handleRelink()}
              >
                {savingRelink ? 'Saving' : 'Save'}
              </Button>
              {savingRelink && <Spinner size="extra-tiny" aria-hidden="true" />}
            </div>
            {relinkError && (
              <MessageBar intent="error"><MessageBarBody>{relinkError}</MessageBarBody></MessageBar>
            )}
            {relinkSuccess && (
              <MessageBar intent="success"><MessageBarBody>Project relinked.</MessageBarBody></MessageBar>
            )}
          </div>

          <Divider />

          <div className={styles.dangerSection}>
            <Title3>Delete project</Title3>
            <Text>This action cannot be undone. The project and all its run history will be permanently removed.</Text>
            <Checkbox
              label="I understand this is permanent"
              checked={deleteConfirmed}
              onChange={(_, data) => setDeleteConfirmed(!!data.checked)}
            />
            <div className={styles.actions}>
              <Button
                appearance="primary"
                style={{ backgroundColor: tokens.colorPaletteRedBackground3, borderColor: tokens.colorPaletteRedBorder2 }}
                disabled={!deleteConfirmed || deleting}
                onClick={() => void handleDelete()}
              >
                {deleting ? 'Deleting' : 'Delete project'}
              </Button>
              {deleting && <Spinner size="extra-tiny" aria-hidden="true" />}
            </div>
            {deleteError && (
              <MessageBar intent="error"><MessageBarBody>{deleteError}</MessageBarBody></MessageBar>
            )}
          </div>
        </>
      )}
    </div>
  );
}
