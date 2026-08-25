import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import {
  Badge,
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DialogTrigger,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  Select,
  Spinner,
  Tab,
  TabList,
  Text,
  Textarea,
  Title3,
  tokens,
} from '@fluentui/react-components';
import {
  Dismiss24Regular,
  People24Regular,
  PersonAddRegular,
  PuzzlePiece20Regular,
} from '@fluentui/react-icons';
import { AgentAvatar } from '../components/AgentAvatar';
import { SyncPanel } from '../components/SyncPanel';
import {
  EmptyState,
  ErrorState,
  LoadingState,
  MetricRow,
  PageContainer,
  PageHeader,
  PageSection,
  Tile,
  TileGrid,
} from '../components/ui';
import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import type {
  CharterDto,
  HistoryDto,
  Project,
  ReroleRequest,
  RoleDto,
  SkillDto,
  SkillStatus,
  TeamDto,
  TeamMemberDto,
  TeamTemplateDto,
} from '../api/types';

type FilterTab = 'all' | 'active' | 'retired';
type PanelTab = 'overview' | 'charter' | 'capabilities';

const useStyles = makeStyles({
  breadcrumbLink: {
    color: tokens.colorNeutralForeground2,
    textDecoration: 'none',
    ':hover': { textDecorationLine: 'underline' },
  },
  breadcrumbSep: {
    color: tokens.colorNeutralForeground4,
  },
  dialogFields: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  panelTabBar: {
    paddingInline: tokens.spacingHorizontalM,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  panelContent: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  panelSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  panelSectionLabel: {
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground3,
  },
  monoText: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    backgroundColor: tokens.colorNeutralBackground2,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    borderRadius: tokens.borderRadiusSmall,
    wordBreak: 'break-all',
  },
  historyBox: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
    backgroundColor: tokens.colorNeutralBackground2,
    padding: tokens.spacingVerticalS,
    borderRadius: tokens.borderRadiusSmall,
    maxHeight: '250px',
    overflowY: 'auto',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  skillList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  skillItem: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    padding: tokens.spacingVerticalS,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusSmall,
  },
  skillItemHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
  },
  skillName: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground1,
    textDecoration: 'none',
    ':hover': {
      textDecoration: 'underline',
    },
  },
  skillDescription: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  drawerFooterRow: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
  },
  panelActions: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
});

function RemoveMemberDialog({
  projectId,
  member,
  onRemoved,
}: {
  projectId: string;
  member: TeamMemberDto;
  onRemoved: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [removing, setRemoving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleRemove = async () => {
    setRemoving(true);
    setError(null);
    try {
      await apiClient.removeMember(projectId, member.name);
      setOpen(false);
      onRemoved();
    } catch (err) {
      setError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error ? err.message : String(err),
      );
    } finally {
      setRemoving(false);
    }
  };

  return (
    <Dialog open={open} onOpenChange={(_, s) => { setOpen(s.open); if (!s.open) setError(null); }}>
      <DialogTrigger disableButtonEnhancement>
        <Button appearance="subtle" size="small">Remove</Button>
      </DialogTrigger>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Remove {member.name}</DialogTitle>
          <DialogContent>
            <Text>Are you sure you want to remove {member.name} from the team? This cannot be undone.</Text>
            {error && (
              <MessageBar intent="error">
                <MessageBarBody>{error}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <DialogTrigger disableButtonEnhancement>
              <Button appearance="secondary" disabled={removing}>Cancel</Button>
            </DialogTrigger>
            <Button
              appearance="primary"
              disabled={removing}
              onClick={() => void handleRemove()}
            >
              {removing ? 'Removing' : 'Remove'}
            </Button>
            {removing && <Spinner size="extra-tiny" aria-hidden="true" />}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function ReroleDialog({
  projectId,
  member,
  scenarios,
  onReroled,
}: {
  projectId: string;
  member: TeamMemberDto;
  scenarios: TeamTemplateDto[];
  onReroled: (updated: TeamMemberDto) => void;
}) {
  const [open, setOpen] = useState(false);
  const [roleId, setRoleId] = useState('');
  const [customTitle, setCustomTitle] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const allRoles: RoleDto[] = scenarios.flatMap((g) => g.roles);

  const handleSave = async () => {
    if (!roleId) return;
    setSaving(true);
    setError(null);
    try {
      const req: ReroleRequest = {
        new_role_id: roleId,
        custom_role_title: customTitle.trim() || undefined,
      };
      const updated = await apiClient.reroleMember(projectId, member.name, req);
      setOpen(false);
      onReroled(updated);
    } catch (err) {
      setError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error ? err.message : String(err),
      );
    } finally {
      setSaving(false);
    }
  };

  return (
    <Dialog open={open} onOpenChange={(_, s) => { setOpen(s.open); if (!s.open) { setRoleId(''); setCustomTitle(''); setError(null); } }}>
      <DialogTrigger disableButtonEnhancement>
        <Button appearance="subtle" size="small">Re-role</Button>
      </DialogTrigger>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Re-role {member.name}</DialogTitle>
          <DialogContent>
            <div style={{ display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM }}>
              <Field label="New role" required>
                <Select value={roleId} onChange={(_, v) => setRoleId(v.value)}>
                  <option value="">Select a role</option>
                  {allRoles.map((r) => (
                    <option key={r.id} value={r.id}>{r.title}</option>
                  ))}
                </Select>
              </Field>
              <Field label="Custom role title (optional)">
                <Input
                  value={customTitle}
                  onChange={(_, v) => setCustomTitle(v.value)}
                  placeholder="Override the role title"
                />
              </Field>
              {error && (
                <MessageBar intent="error">
                  <MessageBarBody>{error}</MessageBarBody>
                </MessageBar>
              )}
            </div>
          </DialogContent>
          <DialogActions>
            <DialogTrigger disableButtonEnhancement>
              <Button appearance="secondary" disabled={saving}>Cancel</Button>
            </DialogTrigger>
            <Button
              appearance="primary"
              disabled={!roleId || saving}
              onClick={() => void handleSave()}
            >
              {saving ? 'Saving' : 'Re-role'}
            </Button>
            {saving && <Spinner size="extra-tiny" aria-hidden="true" />}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function AddMemberDialog({
  projectId,
  onAdded,
}: {
  projectId: string;
  onAdded: (member: TeamMemberDto) => void;
}) {
  const [open, setOpen] = useState(false);
  const [roleId, setRoleId] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [catalogRoles, setCatalogRoles] = useState<RoleDto[]>([]);

  useEffect(() => {
    if (open && catalogRoles.length === 0) {
      void apiClient.getRoles().then(setCatalogRoles).catch(() => {});
    }
  }, [open, catalogRoles.length]);

  const reset = () => { setRoleId(''); setError(null); setSaving(false); };

  const handleAdd = async () => {
    if (!roleId) return;
    setSaving(true);
    setError(null);
    try {
      const member = await apiClient.addMember(projectId, { role_id: roleId });
      setOpen(false);
      reset();
      onAdded(member);
    } catch (err) {
      setError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error ? err.message : String(err),
      );
    } finally {
      setSaving(false);
    }
  };

  const selectedRole = catalogRoles.find(r => r.id === roleId);

  return (
    <Dialog open={open} onOpenChange={(_, s) => { setOpen(s.open); if (!s.open) reset(); }}>
      <DialogTrigger disableButtonEnhancement>
        <Button appearance="primary" icon={<PersonAddRegular />}>Add member</Button>
      </DialogTrigger>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Add team member</DialogTitle>
          <DialogContent>
            <div style={{ display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM }}>
              <Field label="Role" required>
                <Select value={roleId} onChange={(_, v) => setRoleId(v.value)}>
                  <option value="">Select a role</option>
                  {catalogRoles.map((r) => (
                    <option key={r.id} value={r.id}>{r.title}</option>
                  ))}
                </Select>
              </Field>
              {selectedRole && (
                <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                  {selectedRole.summary}
                </Text>
              )}
              {error && (
                <MessageBar intent="error">
                  <MessageBarBody>{error}</MessageBarBody>
                </MessageBar>
              )}
            </div>
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={() => setOpen(false)}>Cancel</Button>
            <Button appearance="primary" disabled={!roleId || saving} onClick={() => void handleAdd()}>
              {saving ? 'Adding...' : 'Cast member'}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function skillStatusColor(status: SkillStatus): 'warning' | 'danger' | 'subtle' {
  if (status === 'missing') return 'warning';
  if (status === 'malformed') return 'danger';
  return 'subtle';
}

function AgentDetailPanel({
  projectId,
  member,
  scenarios,
  onClose,
  onRemoved,
  onReroled,
}: {
  projectId: string;
  member: TeamMemberDto;
  scenarios: TeamTemplateDto[];
  onClose: () => void;
  onRemoved: () => void;
  onReroled: (updated: TeamMemberDto) => void;
}) {
  const styles = useStyles();
  const navigate = useNavigate();
  const [panelTab, setPanelTab] = useState<PanelTab>('overview');

  const [history, setHistory] = useState<HistoryDto | null>(null);
  const [historyError, setHistoryError] = useState<string | null>(null);
  const [historyLoaded, setHistoryLoaded] = useState(false);

  const [charter, setCharter] = useState<CharterDto | null>(null);
  const [charterError, setCharterError] = useState<string | null>(null);
  const [editContent, setEditContent] = useState('');
  const [saving, setSaving] = useState(false);
  const [charterLoaded, setCharterLoaded] = useState(false);

  const [skills, setSkills] = useState<SkillDto[]>([]);
  const [skillsError, setSkillsError] = useState<string | null>(null);
  const [skillsLoaded, setSkillsLoaded] = useState(false);

  const historyLoading = panelTab === 'overview' && !historyLoaded && historyError === null;
  const charterLoading = panelTab === 'charter' && !charterLoaded && charterError === null;
  const skillsTabActive = panelTab === 'overview' || panelTab === 'capabilities';
  const skillsLoading = skillsTabActive && !skillsLoaded && skillsError === null;

  useEffect(() => {
    if (panelTab !== 'overview' || historyLoaded || historyError !== null) return;
    let cancelled = false;
    apiClient.getMemberHistory(projectId, member.name)
      .then((h) => {
        if (!cancelled) {
          setHistory(h);
          setHistoryLoaded(true);
        }
      })
      .catch((err) => {
        if (!cancelled) {
          if (err instanceof ApiError && err.status === 404) {
            setHistoryLoaded(true);
          } else {
            setHistoryError(
              err instanceof ApiError
                ? `API error ${err.status}: ${err.body}`
                : err instanceof Error ? err.message : String(err),
            );
          }
        }
      });
    return () => { cancelled = true; };
  }, [projectId, member.name, panelTab, historyLoaded, historyError]);

  useEffect(() => {
    if (panelTab !== 'charter' || charterLoaded || charterError !== null) return;
    let cancelled = false;
    apiClient.getMemberCharter(projectId, member.name)
      .then((c) => {
        if (!cancelled) {
          setCharter(c);
          setEditContent(c.content);
          setCharterLoaded(true);
        }
      })
      .catch((err) => {
        if (!cancelled) {
          setCharterError(
            err instanceof ApiError
              ? `API error ${err.status}: ${err.body}`
              : err instanceof Error ? err.message : String(err),
          );
        }
      });
    return () => { cancelled = true; };
  }, [projectId, member.name, panelTab, charterLoaded, charterError]);

  useEffect(() => {
    if (!skillsTabActive || skillsLoaded || skillsError !== null) return;
    let cancelled = false;
    apiClient.listSkills(projectId)
      .then((all) => {
        if (!cancelled) {
          setSkills(all.filter((s) => s.assigned_agents.includes(member.name)));
          setSkillsLoaded(true);
        }
      })
      .catch((err) => {
        if (!cancelled) {
          setSkillsError(
            err instanceof ApiError
              ? `API error ${err.status}: ${err.body}`
              : err instanceof Error ? err.message : String(err),
          );
        }
      });
    return () => { cancelled = true; };
  }, [projectId, member.name, skillsTabActive, skillsLoaded, skillsError]);

  const handleSaveCharter = async () => {
    setSaving(true);
    try {
      await apiClient.updateMemberCharter(projectId, member.name, editContent);
      setCharter({ member_name: member.name, content: editContent });
    } catch (err) {
      setCharterError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error ? err.message : String(err),
      );
    } finally {
      setSaving(false);
    }
  };

  void charter;

  const skillsSection = (
    <div className={styles.panelSection}>
      <Text className={styles.panelSectionLabel}>Assigned skills</Text>
      {skillsLoading && <Spinner label="Loading skills" size="small" />}
      {skillsError && (
        <MessageBar intent="error">
          <MessageBarBody>{skillsError}</MessageBarBody>
        </MessageBar>
      )}
      {!skillsLoading && !skillsError && skills.length === 0 && (
        <Text style={{ color: tokens.colorNeutralForeground3 }}>No skills assigned</Text>
      )}
      {!skillsLoading && !skillsError && skills.length > 0 && (
        <div className={styles.skillList}>
          {skills.map((skill) => (
            <div key={skill.id} className={styles.skillItem}>
              <div className={styles.skillItemHeader}>
                <PuzzlePiece20Regular aria-hidden="true" />
                <Link
                  className={styles.skillName}
                  to={`/projects/${projectId}/skills`}
                >
                  {skill.name}
                </Link>
                {skill.status !== 'active' && (
                  <Badge appearance="tint" color={skillStatusColor(skill.status)} size="small">
                    {skill.status}
                  </Badge>
                )}
              </div>
              {skill.description && (
                <Text className={styles.skillDescription}>{skill.description}</Text>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  );

  return (
    <>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={
            <Button
              appearance="subtle"
              aria-label="Close panel"
              icon={<Dismiss24Regular />}
              onClick={onClose}
            />
          }
        >
          {member.name}
          {member.is_built_in && (
            <Badge
              appearance="tint"
              color="subtle"
              size="small"
              style={{ marginLeft: '8px', verticalAlign: 'middle' }}
            >
              System
            </Badge>
          )}
        </DrawerHeaderTitle>
        <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{member.role_title}</Text>
      </DrawerHeader>

      <div className={styles.panelTabBar}>
        <TabList
          selectedValue={panelTab}
          onTabSelect={(_, data) => { setPanelTab(data.value as PanelTab); }}
          aria-label="Agent detail sections"
        >
          <Tab value="overview">Overview</Tab>
          <Tab value="charter">Charter</Tab>
          <Tab value="capabilities">Capabilities</Tab>
        </TabList>
      </div>

      <DrawerBody>
        <div className={styles.panelContent}>
          {panelTab === 'overview' && (
            <>
              <div className={styles.panelSection}>
                <Text className={styles.panelSectionLabel}>Model</Text>
                <Text className={styles.monoText}>{member.default_model}</Text>
              </div>
              <div className={styles.panelSection}>
                <Text className={styles.panelSectionLabel}>Charter path</Text>
                <Text className={styles.monoText}>{member.charter_path}</Text>
              </div>
              <div className={styles.panelActions}>
                <Button
                  appearance="secondary"
                  onClick={() => {
                    onClose();
                    navigate(`/projects/${projectId}/team/${encodeURIComponent(member.name)}/memory`);
                  }}
                >
                  View memory
                </Button>
              </div>
              <div className={styles.panelSection}>
                <Text className={styles.panelSectionLabel}>Recent history</Text>
                {historyLoading && <Spinner label="Loading history" size="small" />}
                {historyError && (
                  <MessageBar intent="error">
                    <MessageBarBody>{historyError}</MessageBarBody>
                  </MessageBar>
                )}
                {!historyLoading && !historyError && !history && (
                  <Text style={{ color: tokens.colorNeutralForeground3 }}>No history yet</Text>
                )}
                {!historyLoading && !historyError && history && (
                  <div className={styles.historyBox}>
                    {history.content.length > 1000
                      ? `${history.content.slice(0, 1000)}...`
                      : history.content}
                  </div>
                )}
              </div>
              {skillsSection}
            </>
          )}

          {panelTab === 'charter' && (
            <>
              {charterLoading && <Spinner label="Loading charter" size="small" />}
              {charterError && (
                <MessageBar intent="error">
                  <MessageBarBody>{charterError}</MessageBarBody>
                </MessageBar>
              )}
              {!charterLoading && member.is_built_in && (
                <MessageBar intent="warning">
                  <MessageBarBody>Built-in system agent charters are read-only.</MessageBarBody>
                </MessageBar>
              )}
              {!charterLoading && (
                <Field label="Charter content">
                  <Textarea
                    value={editContent}
                    onChange={(_, v) => { if (!member.is_built_in) setEditContent(v.value); }}
                    readOnly={member.is_built_in}
                    rows={20}
                    style={{ fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200 }}
                  />
                </Field>
              )}
              {!charterLoading && !member.is_built_in && (
                <div style={{ display: 'flex', gap: tokens.spacingHorizontalS, alignItems: 'center' }}>
                  <Button
                    appearance="primary"
                    disabled={saving || charterLoading}
                    onClick={() => { void handleSaveCharter(); }}
                  >
                    {saving ? 'Saving\u2026' : 'Save charter'}
                  </Button>
                  {saving && <Spinner size="extra-tiny" aria-hidden="true" />}
                </div>
              )}
            </>
          )}

          {panelTab === 'capabilities' && (
            <>
              <Title3>{member.role_title}</Title3>
              <div className={styles.panelSection}>
                <Text className={styles.panelSectionLabel}>Model</Text>
                <Text className={styles.monoText}>{member.default_model}</Text>
              </div>
              {skillsSection}
            </>
          )}
        </div>
      </DrawerBody>

      <DrawerFooter>
        <div className={styles.drawerFooterRow}>
          {!member.is_built_in && (
            <RemoveMemberDialog
              projectId={projectId}
              member={member}
              onRemoved={() => { onClose(); onRemoved(); }}
            />
          )}
          {!member.is_built_in && (
            <ReroleDialog
              projectId={projectId}
              member={member}
              scenarios={scenarios}
              onReroled={onReroled}
            />
          )}
          {member.is_built_in && (
            <Text size={200} style={{ color: tokens.colorNeutralForeground3, fontStyle: 'italic' }}>
              Built-in system agents cannot be removed or re-roled.
            </Text>
          )}
        </div>
      </DrawerFooter>
    </>
  );
}

export function TeamPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();
  const navigate = useNavigate();
  const [team, setTeam] = useState<TeamDto | null>(null);
  const [project, setProject] = useState<Project | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [scenarios, setScenarios] = useState<TeamTemplateDto[]>([]);
  const [showSync, setShowSync] = useState(false);
  const [filterTab, setFilterTab] = useState<FilterTab>('all');
  const [selectedMember, setSelectedMember] = useState<TeamMemberDto | null>(null);

  useEffect(() => {
    if (!projectId) return;
    let cancelled = false;
    setTeam(null);
    setProject(null);
    setScenarios([]);
    setLoading(true);
    setError(null);

    void apiClient.getTeam(projectId)
      .then((t) => {
        if (!cancelled) setTeam(t);
      })
      .catch((err) => {
        if (!cancelled && !(err instanceof ApiError && err.status === 404)) {
          setError(
            err instanceof ApiError
              ? `API error ${err.status}: ${err.body}`
              : err instanceof Error ? err.message : String(err),
          );
        }
      })
      .finally(() => { if (!cancelled) setLoading(false); });

    void apiClient.getTemplates()
      .then((s) => {
        if (!cancelled) setScenarios(s);
      })
      .catch(() => {
        if (!cancelled) setScenarios([]);
      });

    void apiClient.getProject(projectId)
      .then((p) => {
        if (!cancelled) setProject(p);
      })
      .catch(() => {
        if (!cancelled) setProject(null);
      });

    return () => { cancelled = true; };
  }, [projectId]);

  if (!projectId) return null;

  const handleMemberRemoved = () => {
    void apiClient.getTeam(projectId).then(setTeam).catch(() => { setTeam(null); });
  };

  const handleMemberReroled = (updated: TeamMemberDto) => {
    setTeam((prev) => {
      if (!prev) return prev;
      return {
        ...prev,
        members: prev.members.map((m) => m.name === updated.name ? updated : m),
      };
    });
  };

  const handleMemberAdded = (member: TeamMemberDto) => {
    setTeam((prev) => {
      if (!prev) return prev;
      return { ...prev, members: [...prev.members, member] };
    });
  };

  const members = team?.members ?? [];
  const activeCount = members.filter((m) => m.status === 'active').length;
  const retiredCount = members.filter((m) => m.status === 'retired').length;
  const filteredMembers = filterTab === 'all'
    ? members
    : members.filter((m) => m.status === filterTab);

  const projectMembers = filteredMembers.filter((m) => !m.is_built_in);
  const builtInMembers = filteredMembers.filter((m) => m.is_built_in);

  return (
    <PageContainer>
      <PageHeader
        title="Agents"
        description="The cast working on this project."
        breadcrumbs={
          <>
            <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
            <span className={styles.breadcrumbSep}>/</span>
            <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>
              {project?.name ?? team?.project_name ?? projectId}
            </Link>
            <span className={styles.breadcrumbSep}>/</span>
            <span>Team</span>
          </>
        }
        actions={
          !loading && !error ? (
            <>
              {team && (
                <AddMemberDialog
                  projectId={projectId}
                  onAdded={handleMemberAdded}
                />
              )}
              <Button
                appearance="secondary"
                onClick={() => { setShowSync((v) => !v); }}
              >
                {showSync ? 'Hide sync' : 'Sync'}
              </Button>
              <Button
                appearance="primary"
                icon={<People24Regular />}
                onClick={() => { navigate(`/projects/${projectId}/team/cast`); }}
              >
                Cast team
              </Button>
            </>
          ) : undefined
        }
      />

      {loading && <LoadingState rows={4} label="Loading team…" />}
      {error && <ErrorState message={error} />}

      {showSync && <SyncPanel projectId={projectId} />}

      {!loading && !team && !error && (
        <EmptyState
          title="No team yet"
          description="Cast a team to get started. The wizard will help you pick roles and generate agent charters."
          action={
            <Button appearance="primary" onClick={() => { navigate(`/projects/${projectId}/team/cast`); }}>
              Cast team
            </Button>
          }
        />
      )}

      {team && members.length > 0 && (
        <>
          <MetricRow items={[
            { label: 'Total', value: members.length },
            { label: 'Active', value: activeCount },
            { label: 'Retired', value: retiredCount },
          ]} />

          <TabList
            selectedValue={filterTab}
            onTabSelect={(_, data) => { setFilterTab(data.value as FilterTab); }}
            aria-label="Filter agents"
          >
            <Tab value="all">All ({members.length})</Tab>
            <Tab value="active">Active ({activeCount})</Tab>
            <Tab value="retired">Retired ({retiredCount})</Tab>
          </TabList>

          {filteredMembers.length === 0 && (
            <EmptyState title={`No ${filterTab} agents`} description="Adjust the filter to see more agents." />
          )}

          {projectMembers.length > 0 && (
            <TileGrid aria-label="Project agents">
              {projectMembers.map((member) => (
                <Tile
                  key={member.name}
                  media={<AgentAvatar name={member.name} size={32} isBuiltIn={member.is_built_in} isRetired={member.status === 'retired'} />}
                  bubble={false}
                  badges={
                    <>
                      {member.status === 'active' && <Badge appearance="tint" color="success" size="small">Active</Badge>}
                      {member.status === 'retired' && <Badge appearance="tint" color="subtle" size="small">Retired</Badge>}
                    </>
                  }
                  primary={member.name}
                  secondary={member.role_title}
                  onClick={() => { setSelectedMember(member); }}
                />
              ))}
            </TileGrid>
          )}

          {builtInMembers.length > 0 && (
            <PageSection title="System agents">
              <TileGrid aria-label="System agents">
                {builtInMembers.map((member) => (
                  <Tile
                    key={member.name}
                    media={<AgentAvatar name={member.name} size={32} isBuiltIn={member.is_built_in} isRetired={member.status === 'retired'} />}
                    bubble={false}
                    badges={
                      <>
                        {member.status === 'active' && <Badge appearance="tint" color="success" size="small">Active</Badge>}
                        {member.status === 'retired' && <Badge appearance="tint" color="subtle" size="small">Retired</Badge>}
                      </>
                    }
                    primary={member.name}
                    secondary={member.role_title}
                    onClick={() => { setSelectedMember(member); }}
                  />
                ))}
              </TileGrid>
            </PageSection>
          )}
        </>
      )}

      <OverlayDrawer
        open={selectedMember !== null}
        onOpenChange={(_, data) => { if (!data.open) setSelectedMember(null); }}
        position="end"
        size="medium"
      >
        {selectedMember && (
          <AgentDetailPanel
            key={selectedMember.name}
            projectId={projectId}
            member={selectedMember}
            scenarios={scenarios}
            onClose={() => { setSelectedMember(null); }}
            onRemoved={handleMemberRemoved}
            onReroled={(updated) => {
              handleMemberReroled(updated);
              setSelectedMember(updated);
            }}
          />
        )}
      </OverlayDrawer>
    </PageContainer>
  );
}