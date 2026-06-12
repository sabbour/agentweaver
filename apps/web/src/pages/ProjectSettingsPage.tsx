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
  Text,
  Title2,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { Project, UpdateProjectProviderSettingsRequest } from '../api/types';

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
