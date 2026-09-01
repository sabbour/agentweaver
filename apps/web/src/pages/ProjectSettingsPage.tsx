import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { authConfigModeToAuthMode, buildEntraAdminLink } from '../api/entraAdminLink';
import { formatApiErrorMessage } from '../api/errors';
import { GitHubCopilotConnectionPicker } from '../components/GitHubCopilotConnectionPicker';
import { CopilotAuthorizationResultNotice } from '../components/CopilotAuthorizationResultNotice';
import { ConnectGitHubRepositoryDialog } from '../components/ConnectGitHubRepositoryDialog';
import {
  Badge,
  Button,
  Checkbox,
  Divider,
  Field,
  Input,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Select,
  Spinner,
  Switch,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import { Branch24Regular, Delete24Regular, People24Regular, Settings24Regular, Shield24Regular, Wrench24Regular } from '@fluentui/react-icons';
import { useCallback, useEffect, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import type {
  AuthConfigResponse,
  Project,
  ProjectAccessOverview,
  SandboxPolicy,
  ServerInfo,
  UnattendedReadiness,
  UpdateProjectProviderSettingsRequest,
} from '../api/types';
import type { ReactElement } from 'react';
import {
  Body,
  Label,
  MetricRow,
  PageContainer,
  PageHeader,
  PageSection,
  TitleText,
} from '../components/ui';
// Spec settings-subnav — project Settings restructured into a left in-page rail +
// right content pane. Only sections with a real Agentweaver backend are shipped
// (Principle VII): General, Sandbox policy, Danger Zone. The rail is
// data-driven so more sections can be appended as their backends land.
type SectionId = 'general' | 'access' | 'repository' | 'unattended' | 'sandbox' | 'danger';

const GENERATION_DEFAULT_MODEL = 'gpt-5.4';

interface GenerationModelState {
  blueprint_generation_model: string;
  workflow_generation_model: string;
  outcome_spec_generation_model: string;
}

const emptyGenerationModels: GenerationModelState = {
  blueprint_generation_model: '',
  workflow_generation_model: '',
  outcome_spec_generation_model: '',
};

const AUTH_MODE_LABELS = {
  entra: 'Entra ID',
} as const;

interface SectionDef {
  id: SectionId;
  label: string;
  description: string;
  icon: ReactElement;
  danger?: boolean;
}

const SECTIONS: SectionDef[] = [
  {
    id: 'general',
    label: 'General',
    description: 'Project name and model overrides.',
    icon: <Settings24Regular />,
  },
  {
    id: 'access',
    label: 'Access',
    description: 'Manage project membership.',
    icon: <People24Regular />,
  },
  {
    id: 'repository',
    label: 'Repository',
    description: 'Connect or create the GitHub repository for this project.',
    icon: <Branch24Regular />,
  },
  {
    id: 'unattended',
    label: 'Background',
    description: 'Review safe background automation prerequisites for this project.',
    icon: <Shield24Regular />,
  },
  {
    id: 'sandbox',
    label: 'Sandbox policy',
    description: 'Control how agent commands execute and what they may reach.',
    icon: <Wrench24Regular />,
  },
  {
    id: 'danger',
    label: 'Danger Zone',
    description: 'Irreversible actions for this project.',
    icon: <Delete24Regular />,
    danger: true,
  },
];

function isSectionId(value: string | null): value is SectionId {
  return value === 'general'
    || value === 'access'
    || value === 'repository'
    || value === 'unattended'
    || value === 'sandbox'
    || value === 'danger';
}

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    maxWidth: '1180px',
  },
  breadcrumb: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
  },
  breadcrumbLink: {
    color: tokens.colorNeutralForeground2,
    textDecoration: 'none',
    ':hover': { textDecorationLine: 'underline' },
  },
  layout: {
    display: 'flex',
    gap: tokens.spacingHorizontalXXL,
    alignItems: 'flex-start',
  },
  rail: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    width: '240px',
    flexShrink: 0,
    position: 'sticky',
    top: '0',
    padding: tokens.spacingVerticalS,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusXLarge,
  },
  railItem: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusMedium,
    border: 'none',
    background: 'transparent',
    cursor: 'pointer',
    textAlign: 'left',
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
    fontFamily: tokens.fontFamilyBase,
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
      color: tokens.colorNeutralForeground1,
    },
  },
  railItemActive: {
    backgroundColor: tokens.colorNeutralBackground1Selected,
    color: tokens.colorNeutralForeground1,
    fontWeight: tokens.fontWeightSemibold,
  },
  railItemDanger: {
    color: tokens.colorPaletteRedForeground1,
    ':hover': {
      backgroundColor: tokens.colorPaletteRedBackground1,
      color: tokens.colorPaletteRedForeground1,
    },
  },
  railIcon: {
    display: 'flex',
    flexShrink: 0,
  },
  pane: {
    flex: 1,
    minWidth: 0,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    maxWidth: '640px',
  },
  subBlock: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  formActions: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
  },
  dangerSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    border: `1px solid ${tokens.colorPaletteRedBorder2}`,
    maxWidth: '640px',
    padding: tokens.spacingVerticalL,
    borderRadius: tokens.borderRadiusLarge,
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
  helperText: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
  },
  badgeRow: {
    display: 'flex',
    gap: tokens.spacingHorizontalXS,
    flexWrap: 'wrap',
  },
  roleList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  roleRow: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    padding: tokens.spacingHorizontalM,
    backgroundColor: tokens.colorNeutralBackground1,
    flexWrap: 'wrap',
  },
  roleIdentity: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  roleActions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
});

export function ProjectSettingsPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();

  // Selected settings section is deep-linked via ?section=… so it is shareable and
  // survives refresh; fall back to General for missing/unknown values.
  const sectionParam = searchParams.get('section');
  const activeSection: SectionId = isSectionId(sectionParam) ? sectionParam : 'general';

  const selectSection = (id: SectionId) => {
    const next = new URLSearchParams(searchParams);
    next.set('section', id);
    setSearchParams(next, { replace: true });
  };
  const copilotAuthorizationResult = searchParams.get('copilot_app_auth');
  const dismissCopilotAuthorizationResult = () => {
    const next = new URLSearchParams(searchParams);
    next.delete('copilot_app_auth');
    setSearchParams(next, { replace: true });
  };

  const [connectRepoOpen, setConnectRepoOpen] = useState(false);

  const [project, setProject] = useState<Project | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);

  // Model settings
  const [copilotModel, setCopilotModel] = useState('');
  const [savingModel, setSavingModel] = useState(false);
  const [modelError, setModelError] = useState<string | null>(null);
  const [modelSuccess, setModelSuccess] = useState(false);
  const [generationModels, setGenerationModels] = useState<GenerationModelState>(emptyGenerationModels);
  const [savingGeneration, setSavingGeneration] = useState(false);
  const [generationError, setGenerationError] = useState<string | null>(null);
  const [generationSuccess, setGenerationSuccess] = useState(false);

  // Rename
  const [newName, setNewName] = useState('');
  const [savingRename, setSavingRename] = useState(false);
  const [renameError, setRenameError] = useState<string | null>(null);
  const [renameSuccess, setRenameSuccess] = useState(false);

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
  const [previewApprovalTimeout, setPreviewApprovalTimeout] = useState(30);
  const [savingPreviewApproval, setSavingPreviewApproval] = useState(false);
  const [previewApprovalError, setPreviewApprovalError] = useState<string | null>(null);
  const [previewApprovalSuccess, setPreviewApprovalSuccess] = useState(false);

  // Entra access management.
  const [accessOverview, setAccessOverview] = useState<ProjectAccessOverview | null>(null);
  const [accessLoading, setAccessLoading] = useState(true);
  const [accessError, setAccessError] = useState<string | null>(null);
  const [accessOverviewUnavailable, setAccessOverviewUnavailable] = useState(false);
  const [authConfig, setAuthConfig] = useState<AuthConfigResponse | null>(null);
  const [principalId, setPrincipalId] = useState('');
  const [principalDisplayName, setPrincipalDisplayName] = useState('');
  const [projectRole, setProjectRole] = useState('Viewer');
  const [savingRoleAssignment, setSavingRoleAssignment] = useState(false);
  const [roleAssignmentError, setRoleAssignmentError] = useState<string | null>(null);
  const [roleAssignmentSuccess, setRoleAssignmentSuccess] = useState<string | null>(null);
  const [roleActionKey, setRoleActionKey] = useState<string | null>(null);
  const [unattendedReadiness, setUnattendedReadiness] = useState<UnattendedReadiness | null>(null);
  const [unattendedLoading, setUnattendedLoading] = useState(true);
  const [unattendedError, setUnattendedError] = useState<string | null>(null);
  const [serverInfo, setServerInfo] = useState<ServerInfo | null>(null);

  const formatError = (err: unknown): string => formatApiErrorMessage(err);

  useEffect(() => {
    if (!projectId) return;
    let cancelled = false;
    apiClient.getProject(projectId)
      .then((p) => {
        if (!cancelled) {
          setProject(p);
          setCopilotModel(p.default_model_github_copilot ?? '');
          setGenerationModels({
            blueprint_generation_model: p.blueprint_generation_model ?? '',
            workflow_generation_model: p.workflow_generation_model ?? '',
            outcome_spec_generation_model: p.outcome_spec_generation_model ?? '',
          });
          setNewName(p.name);
          setPreviewApprovalTimeout(p.preview_approval_timeout_minutes ?? 30);
        }
      })
      .catch((err) => {
        if (!cancelled) setLoadError(formatError(err));
      })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [projectId]);

  useEffect(() => {
    let cancelled = false;
    void apiClient.getAuthConfig()
      .then((config) => {
        if (!cancelled) setAuthConfig(config);
      })
      .catch(() => {
        if (!cancelled) setAuthConfig(null);
      });
    return () => { cancelled = true; };
  }, []);

  useEffect(() => {
    let cancelled = false;
    void apiClient.getServerInfo()
      .then((info) => {
        if (!cancelled) setServerInfo(info);
      })
      .catch(() => {
        if (!cancelled) setServerInfo(null);
      });
    return () => { cancelled = true; };
  }, []);

  const refreshAccessOverview = useCallback(async () => {
    if (!projectId) return;
    setAccessLoading(true);
    setAccessError(null);
    setAccessOverviewUnavailable(false);
    try {
      // Assumption for Tank's authz rollout: a single access snapshot endpoint returns
      // the current auth mode, platform-role view, and project role assignments.
      const overview = await apiClient.getProjectAccessOverview(projectId);
      setAccessOverview(overview);
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) {
        setAccessOverviewUnavailable(true);
      } else {
        setAccessError(formatError(err));
      }
      setAccessOverview(null);
    } finally {
      setAccessLoading(false);
    }
  }, [projectId]);

  useEffect(() => {
    if (!projectId) return;
    queueMicrotask(() => { void refreshAccessOverview(); });
  }, [projectId, refreshAccessOverview]);

  const refreshUnattendedReadiness = useCallback(async () => {
    if (!projectId) return;
    setUnattendedLoading(true);
    setUnattendedError(null);
    try {
      setUnattendedReadiness(await apiClient.getUnattendedReadiness(projectId));
    } catch {
      setUnattendedReadiness(null);
      setUnattendedError('Automation readiness is unavailable. Refresh the page and try again.');
    } finally {
      setUnattendedLoading(false);
    }
  }, [projectId]);

  useEffect(() => {
    if (!projectId) return;
    queueMicrotask(() => { void refreshUnattendedReadiness(); });
  }, [projectId, refreshUnattendedReadiness]);

  const handleSaveModel = async () => {
    if (!projectId) return;
    setSavingModel(true);
    setModelError(null);
    setModelSuccess(false);
    try {
      const req: UpdateProjectProviderSettingsRequest = {};
      req.default_provider = project?.default_provider ?? 'github-copilot';
      req.default_model_github_copilot = copilotModel.trim() || null;
      req.default_model_microsoft_foundry = project?.default_model_microsoft_foundry ?? null;
      req.blueprint_generation_model = generationModels.blueprint_generation_model.trim() || null;
      req.workflow_generation_model = generationModels.workflow_generation_model.trim() || null;
      req.outcome_spec_generation_model = generationModels.outcome_spec_generation_model.trim() || null;
      await apiClient.updateProjectProviderSettings(projectId, req);
      setProject((prev) => prev ? {
        ...prev,
        default_model_github_copilot: req.default_model_github_copilot ?? null,
        blueprint_generation_model: req.blueprint_generation_model ?? null,
        workflow_generation_model: req.workflow_generation_model ?? null,
        outcome_spec_generation_model: req.outcome_spec_generation_model ?? null,
      } : prev);
      setModelSuccess(true);
    } catch (err) {
      setModelError(formatError(err));
    } finally {
      setSavingModel(false);
    }
  };

  const saveGenerationModels = async (models: GenerationModelState) => {
    if (!projectId) return;
    setSavingGeneration(true);
    setGenerationError(null);
    setGenerationSuccess(false);
    try {
      const req: UpdateProjectProviderSettingsRequest = {
        default_provider: project?.default_provider ?? 'github-copilot',
        default_model_github_copilot: copilotModel.trim() || null,
        default_model_microsoft_foundry: project?.default_model_microsoft_foundry ?? null,
        blueprint_generation_model: models.blueprint_generation_model.trim() || null,
        workflow_generation_model: models.workflow_generation_model.trim() || null,
        outcome_spec_generation_model: models.outcome_spec_generation_model.trim() || null,
      };
      await apiClient.updateProjectProviderSettings(projectId, req);
      setProject((prev) => prev ? {
        ...prev,
        blueprint_generation_model: req.blueprint_generation_model ?? null,
        workflow_generation_model: req.workflow_generation_model ?? null,
        outcome_spec_generation_model: req.outcome_spec_generation_model ?? null,
      } : prev);
      setGenerationSuccess(true);
    } catch (err) {
      setGenerationError(formatError(err));
    } finally {
      setSavingGeneration(false);
    }
  };

  const handleSaveGeneration = async () => {
    await saveGenerationModels(generationModels);
  };

  const handleResetGeneration = async () => {
    const inherited = { ...emptyGenerationModels };
    setGenerationModels(inherited);
    await saveGenerationModels(inherited);
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
          setSandboxError(formatError(err));
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
      // Round-trip the FULL policy (including allowed_repository_roots and
      // destructive_command_patterns) so omitted fields are never dropped.
      const updated = await apiClient.updateSandboxPolicy(sandboxPolicy);
      setSandboxPolicy(updated);
      setSandboxSaveSuccess(true);
    } catch (err) {
      setSandboxSaveError(formatError(err));
    } finally {
      setSavingSandbox(false);
    }
  };

  const handleSavePreviewApproval = async () => {
    if (!projectId) return;
    if (!Number.isInteger(previewApprovalTimeout)
      || previewApprovalTimeout < 1
      || previewApprovalTimeout > 1440) {
      setPreviewApprovalError('Approval timeout must be a whole number between 1 and 1440 minutes.');
      setPreviewApprovalSuccess(false);
      return;
    }

    setSavingPreviewApproval(true);
    setPreviewApprovalError(null);
    setPreviewApprovalSuccess(false);
    try {
      const saved = await apiClient.updateProjectPreviewSettings(projectId, {
        approval_timeout_minutes: previewApprovalTimeout,
      });
      setPreviewApprovalTimeout(saved.approval_timeout_minutes);
      setProject((prev) => prev
        ? { ...prev, preview_approval_timeout_minutes: saved.approval_timeout_minutes }
        : prev);
      setPreviewApprovalSuccess(true);
    } catch (err) {
      setPreviewApprovalError(formatError(err));
    } finally {
      setSavingPreviewApproval(false);
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
      setRenameError(formatError(err));
    } finally {
      setSavingRename(false);
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
      setDeleteError(formatError(err));
    } finally {
      setDeleting(false);
    }
  };

  const handleAddRoleAssignment = async () => {
    if (!projectId || !principalId.trim()) return;
    setSavingRoleAssignment(true);
    setRoleAssignmentError(null);
    setRoleAssignmentSuccess(null);
    try {
      await apiClient.createProjectRoleAssignment(projectId, {
        principal_id: principalId.trim(),
        display_name: principalDisplayName.trim() || null,
        email: principalId.includes('@') ? principalId.trim() : null,
        role: projectRole,
      });
      setPrincipalId('');
      setPrincipalDisplayName('');
      setProjectRole('Viewer');
      setRoleAssignmentSuccess('Project member saved.');
      await refreshAccessOverview();
    } catch (err) {
      setRoleAssignmentError(formatError(err));
    } finally {
      setSavingRoleAssignment(false);
    }
  };

  const handleDeleteRoleAssignment = async (assignmentId: string) => {
    if (!projectId) return;
    setRoleActionKey(assignmentId);
    setRoleAssignmentError(null);
    setRoleAssignmentSuccess(null);
    try {
      await apiClient.deleteProjectRoleAssignment(projectId, assignmentId);
      setRoleAssignmentSuccess('Project member removed.');
      await refreshAccessOverview();
    } catch (err) {
      setRoleAssignmentError(formatError(err));
    } finally {
      setRoleActionKey(null);
    }
  };

  if (!projectId) return null;

  const visibleSections = SECTIONS.filter((s) => s.id !== 'repository' || project?.origin === 'blank');
  const displayedSection = visibleSections.some((section) => section.id === activeSection)
    ? activeSection
    : 'unattended';
  const activeDef = visibleSections.find((s) => s.id === displayedSection) ?? visibleSections[0];
  const fallbackAuthMode = authConfigModeToAuthMode(authConfig?.mode);
  const resolvedAuthMode = accessOverview?.auth_mode ?? fallbackAuthMode;
  const authModeLabel = resolvedAuthMode ? AUTH_MODE_LABELS[resolvedAuthMode] : 'GitHub';
  const entraAdminLink = buildEntraAdminLink(authConfig?.entra);
  const accessStatusMessage = accessOverviewUnavailable
    ? resolvedAuthMode === 'entra'
      ? 'Access management is handled in Microsoft Entra ID for this deployment.'
      : 'Access management is not available on this deployment yet.'
    : accessError;
  const projectRoleSummary = accessOverview?.current_user_project_role ?? (project?.owner ? `Owner (${project.owner})` : 'Unspecified');

  return (
    <PageContainer>
      <PageHeader
        title="Project settings"
        description="Project configuration and pickup behavior."
        breadcrumbs={
          <div className={styles.breadcrumb}>
            <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
            <span>/</span>
            <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>{project?.name ?? projectId}</Link>
            <span>/</span>
            <span>Settings</span>
          </div>
        }
      />

      {loading && <Spinner label="Loading project" />}

      {loadError && (
        <MessageBar intent="error">
          <MessageBarBody>{loadError}</MessageBarBody>
        </MessageBar>
      )}
      <CopilotAuthorizationResultNotice
        code={copilotAuthorizationResult}
        onDismiss={dismissCopilotAuthorizationResult}
      />

      {project && (
        <div className={styles.layout}>
          <nav className={styles.rail} aria-label="Settings sections">
            {visibleSections.map((section) => (
              <button
                key={section.id}
                className={mergeClasses(
                  styles.railItem,
                  displayedSection === section.id && styles.railItemActive,
                  section.danger ? styles.railItemDanger : undefined,
                )}
                onClick={() => selectSection(section.id)}
              >
                <span className={styles.railIcon}>{section.icon}</span>
                <span>{section.label}</span>
                {section.danger && (
                  <Badge appearance="tint" color="danger" size="small">Risk</Badge>
                )}
              </button>
            ))}
          </nav>

          <div className={styles.pane}>
            <PageSection title={activeDef.label} description={activeDef.description}>
              <MetricRow items={[
                { label: 'Project', value: project.name },
                { label: 'Working directory', value: project.working_directory ?? 'Not configured' },
                { label: 'AI source', value: 'Deployment setting' },
                { label: 'Authentication mode', value: authModeLabel },
                { label: 'Your project role', value: projectRoleSummary },
              ]} />
            </PageSection>

            {displayedSection === 'general' && (
              <div className={styles.section}>
                <div className={styles.subBlock}>
                  <TitleText>AI source</TitleText>
                  <Body as="p" tone="muted">
                    This project uses the AI source configured for the deployment. Change it in Platform settings.
                  </Body>
                </div>

                <div className={styles.subBlock}>
                  <TitleText>Rename project</TitleText>
                  <Field
                    label="Name"
                    hint="Shown in project navigation, run context, and team views."
                  >
                    <Input id="project-settings-name" value={newName} onChange={(_, v) => setNewName(v.value)} />
                  </Field>
                  <div className={styles.formActions}>
                    <Button
                      appearance="primary"
                      disabled={savingRename || !newName.trim() || newName.trim() === project.name}
                      onClick={() => void handleRename()}
                    >
                      {savingRename ? 'Saving' : 'Save'}
                    </Button>
                    <Button
                      appearance="secondary"
                      disabled={savingRename || newName === project.name}
                      onClick={() => setNewName(project.name)}
                    >
                      Cancel
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

                <div className={styles.subBlock}>
                  <TitleText>Default run model</TitleText>
                  <Field
                    label="GitHub Copilot model"
                    hint="Leave blank to use the service default for Copilot-backed runs."
                  >
                    <Input
                      id="project-settings-copilot-model"
                      value={copilotModel}
                      onChange={(_, v) => setCopilotModel(v.value)}
                      placeholder="Auto (coordinator picks) — e.g. claude-sonnet-4.6"
                    />
                  </Field>
                  <div className={styles.formActions}>
                    <Button
                      appearance="primary"
                      disabled={savingModel}
                      onClick={() => void handleSaveModel()}
                    >
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

                <div className={styles.subBlock}>
                  <TitleText>Generation models</TitleText>
                  <Body as="p" tone="muted">
                    Leave a field blank to inherit the global generation default ({GENERATION_DEFAULT_MODEL}).
                  </Body>
                  <Field
                    label="Blueprint generation model"
                    hint={`Blank inherits ${GENERATION_DEFAULT_MODEL}.`}
                  >
                    <Input
                      id="project-settings-blueprint-model"
                      value={generationModels.blueprint_generation_model}
                      onChange={(_, v) => setGenerationModels((prev) => ({ ...prev, blueprint_generation_model: v.value }))}
                      placeholder={`Inherit ${GENERATION_DEFAULT_MODEL}`}
                    />
                  </Field>
                  <Field
                    label="Workflow generation model"
                    hint={`Blank inherits ${GENERATION_DEFAULT_MODEL}.`}
                  >
                    <Input
                      id="project-settings-workflow-model"
                      value={generationModels.workflow_generation_model}
                      onChange={(_, v) => setGenerationModels((prev) => ({ ...prev, workflow_generation_model: v.value }))}
                      placeholder={`Inherit ${GENERATION_DEFAULT_MODEL}`}
                    />
                  </Field>
                  <Field
                    label="Outcome spec generation model"
                    hint={`Blank inherits ${GENERATION_DEFAULT_MODEL}.`}
                  >
                    <Input
                      id="project-settings-outcome-model"
                      value={generationModels.outcome_spec_generation_model}
                      onChange={(_, v) => setGenerationModels((prev) => ({ ...prev, outcome_spec_generation_model: v.value }))}
                      placeholder={`Inherit ${GENERATION_DEFAULT_MODEL}`}
                    />
                  </Field>
                  <div className={styles.formActions}>
                    <Button
                      appearance="primary"
                      disabled={savingGeneration}
                      onClick={() => void handleSaveGeneration()}
                    >
                      {savingGeneration ? 'Saving generation models' : 'Save generation models'}
                    </Button>
                    <Button
                      appearance="secondary"
                      disabled={savingGeneration}
                      onClick={() => void handleResetGeneration()}
                    >
                      Reset to inherit defaults
                    </Button>
                    {savingGeneration && <Spinner size="extra-tiny" aria-hidden="true" />}
                  </div>
                  {generationError && (
                    <MessageBar intent="error"><MessageBarBody>{generationError}</MessageBarBody></MessageBar>
                  )}
                  {generationSuccess && (
                    <MessageBar intent="success"><MessageBarBody>Generation model settings saved.</MessageBarBody></MessageBar>
                  )}
                </div>
              </div>
            )}

            {displayedSection === 'access' && (
              <div className={styles.section}>
                {accessLoading && <Spinner label="Loading access settings" size="extra-tiny" />}
                {accessStatusMessage && (
                  <MessageBar intent="warning">
                    <MessageBarBody>{accessStatusMessage}</MessageBarBody>
                    {accessOverviewUnavailable && resolvedAuthMode === 'entra' && entraAdminLink && (
                      <MessageBarActions>
                        <Button
                          as="a"
                          href={entraAdminLink.href}
                          target="_blank"
                          rel="noreferrer"
                          size="small"
                        >
                          {entraAdminLink.label}
                        </Button>
                      </MessageBarActions>
                    )}
                  </MessageBar>
                )}
                {accessOverview && (
                  <>
                    <div className={styles.subBlock}>
                      <TitleText>Platform access</TitleText>
                      {accessOverview.auth_mode === 'entra' ? (
                        <>
                          <Body as="p" tone="muted">
                            Platform roles are assigned in Microsoft Entra ID. Agentweaver shows them here for
                            context, but changes must be made in Entra rather than in this project.
                          </Body>
                          <div className={styles.badgeRow}>
                            {accessOverview.platform_roles.length > 0 ? (
                              accessOverview.platform_roles.map((role) => (
                                <Badge key={role} appearance="filled">{role}</Badge>
                              ))
                            ) : (
                              <Label as="span" className={styles.emptyNote}>No Entra platform roles are assigned.</Label>
                            )}
                          </div>
                        </>
                      ) : (
                        <MessageBar intent="info">
                          <MessageBarBody>
                            This deployment uses GitHub authentication. Project access continues to follow the
                            current GitHub-based ownership model, so Entra platform-role mapping is inactive here.
                          </MessageBarBody>
                        </MessageBar>
                      )}
                    </div>

                    <div className={styles.subBlock}>
                      <TitleText>Project members</TitleText>
                      {accessOverview.auth_mode === 'entra' ? (
                        <>
                          <Body as="p" tone="muted">
                            Owners, contributors, and viewers are stored in Agentweaver for this project. These roles control Agentweaver actions only; GitHub repository access still depends on the authorization granted through the Repo App.
                          </Body>
                          <div className={styles.roleList}>
                            {accessOverview.project_role_assignments.length === 0 ? (
                              <Label as="span" className={styles.emptyNote}>No project role assignments yet.</Label>
                            ) : (
                              accessOverview.project_role_assignments.map((assignment) => (
                                <div key={assignment.assignment_id} className={styles.roleRow}>
                                  <div className={styles.roleIdentity}>
                                    <TitleText>{assignment.display_name ?? assignment.email ?? assignment.principal_id}</TitleText>
                                    <Body tone="muted">
                                      {assignment.email ?? assignment.principal_id}
                                    </Body>
                                    <div className={styles.badgeRow}>
                                      <Badge appearance="filled">{assignment.role}</Badge>
                                      <Badge appearance="outline">{assignment.scope}</Badge>
                                    </div>
                                  </div>
                                  {accessOverview.can_manage_role_assignments && (
                                    <div className={styles.roleActions}>
                                      <Button
                                        appearance="subtle"
                                        disabled={roleActionKey !== null}
                                        onClick={() => void handleDeleteRoleAssignment(assignment.assignment_id)}
                                      >
                                        {roleActionKey === assignment.assignment_id ? 'Removing' : 'Remove'}
                                      </Button>
                                    </div>
                                  )}
                                </div>
                              ))
                            )}
                          </div>

                          <Field
                            label="Add member"
                            hint="Enter the Entra object ID or email of the person who should receive access."
                          >
                            <Input
                              value={principalId}
                              placeholder="person@contoso.com"
                              onChange={(_, data) => setPrincipalId(data.value)}
                            />
                          </Field>
                          <Field
                            label="Display name (optional)"
                            hint="Stored for readability until Tank's directory lookup lands."
                          >
                            <Input
                              value={principalDisplayName}
                              placeholder="Ada Lovelace"
                              onChange={(_, data) => setPrincipalDisplayName(data.value)}
                            />
                          </Field>
                          <Field label="Role">
                            <Select value={projectRole} onChange={(_, data) => setProjectRole(data.value)}>
                              <option value="Owner">Owner</option>
                              <option value="Contributor">Contributor</option>
                              <option value="Viewer">Viewer</option>
                            </Select>
                          </Field>
                          <div className={styles.formActions}>
                            <Button
                              appearance="primary"
                              disabled={!accessOverview.can_manage_role_assignments || savingRoleAssignment || !principalId.trim()}
                              onClick={() => void handleAddRoleAssignment()}
                            >
                              {savingRoleAssignment ? 'Saving' : 'Add member'}
                            </Button>
                            {savingRoleAssignment && <Spinner size="extra-tiny" aria-hidden="true" />}
                          </div>
                        </>
                      ) : (
                        <Body as="p" tone="muted">
                          In GitHub mode, the project continues to rely on the existing single-owner model.
                          Project role assignments are only used in Entra ID mode.
                        </Body>
                      )}
                      {roleAssignmentError && (
                        <MessageBar intent="error"><MessageBarBody>{roleAssignmentError}</MessageBarBody></MessageBar>
                      )}
                      {roleAssignmentSuccess && (
                        <MessageBar intent="success"><MessageBarBody>{roleAssignmentSuccess}</MessageBarBody></MessageBar>
                      )}
                    </div>

                  </>
                )}
              </div>
            )}

            {displayedSection === 'unattended' && (
              <div className={styles.section}>
                <div className={styles.subBlock}>
                  <TitleText>Background automation readiness</TitleText>
                  <Body as="p" tone="muted">
                    This read-only status reports the server-verified prerequisites for background work.
                    This page does not enable or activate automation.
                  </Body>
                  <TitleText>GitHub Copilot account</TitleText>
                  <Body as="p" tone="muted">
                    This controls the GitHub Copilot account used for this project’s background AI and other Copilot-powered generation. It does not control repository access.
                  </Body>
                  <GitHubCopilotConnectionPicker
                    projectId={projectId}
                    showConnectionStatus
                    suppressProjectOverrideWhenPlatformDefault
                  />
                  <Divider />
                  <TitleText>Background requirements</TitleText>
                  <Body as="p" tone="muted">
                    {project.source_repository
                      ? 'These server-verified prerequisites cover repository access for background branch, push, and pull-request work on this project’s connected GitHub repository. They are separate from the GitHub Copilot AI access shown above.'
                      : 'These server-verified prerequisites apply after you connect a GitHub repository. They cover repository access for background branch, push, and pull-request work and are separate from the GitHub Copilot AI access shown above.'}
                  </Body>
                  {unattendedLoading && <Spinner label="Checking automation readiness" size="extra-tiny" />}
                  {unattendedReadiness && (
                    <>
                      <MetricRow items={[
                        { label: 'Status', value: unattendedReadiness.status === 'ready' ? 'Ready' : 'Not ready' },
                        { label: 'Reason code', value: unattendedReadiness.reason_code },
                      ]} />
                      <MessageBar intent={unattendedReadiness.status === 'ready' ? 'success' : 'warning'}>
                        <MessageBarBody>{unattendedReadiness.message}</MessageBarBody>
                      </MessageBar>
                    </>
                  )}
                  <div className={styles.formActions}>
                    {unattendedReadiness?.reason_code === 'repo_app_installation_required' && serverInfo?.repo_app_install_url && (
                      <Button
                        as="a"
                        href={serverInfo.repo_app_install_url}
                        target="_blank"
                        rel="noreferrer"
                        appearance="primary"
                      >
                        Install GitHub Repo App
                      </Button>
                    )}
                    <Button appearance="secondary" disabled={unattendedLoading} onClick={() => void refreshUnattendedReadiness()}>
                      Refresh status
                    </Button>
                  </div>
                  {unattendedError && (
                    <MessageBar intent="error"><MessageBarBody>{unattendedError}</MessageBarBody></MessageBar>
                  )}
                </div>
              </div>
            )}

            {displayedSection === 'repository' && (
              <div className={styles.section}>
                <div className={styles.subBlock}>
                  <TitleText>Connect a GitHub repository</TitleText>
                  <Body as="p" tone="muted">
                    This project was started without a connected GitHub repository, so runs can't
                    open pull requests. Create a new repository or connect one you already have to
                    enable publishing. GitHub actions use the project capability authorization, so
                    make sure you've linked an account first.
                  </Body>
                  <div className={styles.formActions}>
                    <Button appearance="primary" onClick={() => setConnectRepoOpen(true)}>
                      Connect or create repository
                    </Button>
                  </div>
                </div>
                <ConnectGitHubRepositoryDialog
                  projectId={projectId}
                  projectName={project.name}
                  open={connectRepoOpen}
                  onOpenChange={setConnectRepoOpen}
                  onConnected={(sourceRepository) => {
                    setProject((prev) => prev ? { ...prev, origin: 'github', source_repository: sourceRepository } : prev);
                    selectSection('general');
                  }}
                />
              </div>
            )}

            {displayedSection === 'sandbox' && (
              <div className={styles.section}>
                <div className={styles.subBlock}>
                  <TitleText>Preview approval</TitleText>
                  <Body as="p" tone="muted">
                    Agent-requested previews remain private until approved. An expired request can be
                    retried from the run timeline without restarting the run.
                  </Body>
                  <Field
                    label="Approval timeout (minutes)"
                    hint="Whole number from 1 to 1440. Existing and new projects default to 30 minutes."
                    validationState={previewApprovalError ? 'error' : 'none'}
                    validationMessage={previewApprovalError ?? undefined}
                  >
                    <Input
                      type="number"
                      min={1}
                      max={1440}
                      step={1}
                      value={String(previewApprovalTimeout)}
                      onChange={(_, data) => setPreviewApprovalTimeout(Number(data.value))}
                      aria-label="Preview approval timeout in minutes"
                    />
                  </Field>
                  <div className={styles.formActions}>
                    <Button
                      appearance="primary"
                      disabled={savingPreviewApproval}
                      onClick={() => void handleSavePreviewApproval()}
                    >
                      {savingPreviewApproval ? 'Saving timeout' : 'Save preview approval'}
                    </Button>
                    {savingPreviewApproval && <Spinner size="extra-tiny" aria-hidden="true" />}
                  </div>
                  {previewApprovalSuccess && (
                    <MessageBar intent="success">
                      <MessageBarBody>Preview approval timeout saved.</MessageBarBody>
                    </MessageBar>
                  )}
                </div>
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
                    <Field
                      label="Sandbox enabled"
                      hint="When off, commands run directly on the host with no isolation layer."
                    >
                      <Switch
                        label={sandboxPolicy.direct ? 'Off — no isolation layer' : 'On — commands run in the sandbox'}
                        checked={!sandboxPolicy.direct}
                        onChange={(_, data) =>
                          setSandboxPolicy((prev) => prev ? { ...prev, direct: !data.checked } : prev)
                        }
                      />
                    </Field>
                    <Field
                      label="Outbound network"
                      hint={sandboxPolicy.direct ? 'Only applies when the sandbox is enabled.' : undefined}
                    >
                      <Switch
                        label={sandboxPolicy.network_enabled ? 'Enabled' : 'Blocked'}
                        checked={sandboxPolicy.network_enabled}
                        disabled={sandboxPolicy.direct}
                        onChange={(_, data) =>
                          setSandboxPolicy((prev) => prev ? { ...prev, network_enabled: data.checked } : prev)
                        }
                      />
                    </Field>
                    <Field label="Allowed repository roots">
                      <div className={styles.listBox}>
                        {sandboxPolicy.allowed_repository_roots.length === 0 ? (
                          <Label as="span" className={styles.emptyNote}>None configured</Label>
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
                          <Label as="span" className={styles.emptyNote}>None configured</Label>
                        ) : (
                          sandboxPolicy.destructive_command_patterns.map((pat, i) => (
                            <div key={i} className={styles.listItem}>{pat}</div>
                          ))
                        )}
                      </div>
                    </Field>
                    <div className={styles.formActions}>
                      <Button
                        appearance="primary"
                        disabled={savingSandbox}
                        onClick={() => void handleSaveSandbox()}
                      >
                        {savingSandbox ? 'Saving' : 'Save'}
                      </Button>
                      <Button
                        appearance="secondary"
                        disabled={savingSandbox || sandboxLoading}
                        onClick={() => {
                          if (!project?.working_directory) return;
                          setSandboxFetched(false);
                          setSandboxSaveError(null);
                          setSandboxSaveSuccess(false);
                          void apiClient.getSandboxPolicy(project.working_directory)
                            .then((p) => {
                              setSandboxPolicy(p);
                              setSandboxFetched(true);
                            })
                            .catch((err) => {
                              setSandboxFetched(true);
                              setSandboxError(formatError(err));
                            });
                        }}
                      >
                        Discard changes
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
            )}

            {displayedSection === 'danger' && (
              <div className={styles.dangerSection}>
                <TitleText>Delete project</TitleText>
                <Body as="p">This action cannot be undone. The project and all its run history will be permanently removed.</Body>
                <Checkbox
                  label="I understand this is permanent"
                  checked={deleteConfirmed}
                  onChange={(_, data) => setDeleteConfirmed(!!data.checked)}
                />
                <div className={styles.formActions}>
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
            )}
          </div>
        </div>
      )}
    </PageContainer>
  );
}