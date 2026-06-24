import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
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
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { Dismiss24Regular, PersonAddRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { AgentAvatar } from '../components/AgentAvatar';
import type {
  TeamDto,
  TeamMemberDto,
  CharterDto,
  HistoryDto,
  TeamTemplateDto,
  RoleDto,
  ReroleRequest,
  Project,
} from '../api/types';
import { SyncPanel } from '../components/SyncPanel';
import { PageHeader } from '../components/PageHeader';

type FilterTab = 'all' | 'active' | 'retired';
type PanelTab = 'overview' | 'charter' | 'capabilities';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
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
  emptyState: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: tokens.spacingVerticalM,
    padding: `${tokens.spacingVerticalXXL} ${tokens.spacingHorizontalXXL}`,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    textAlign: 'center',
  },
  dialogFields: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  cardGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))',
    gap: tokens.spacingVerticalM,
  },
  systemSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  systemSectionHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    paddingTop: tokens.spacingVerticalXS,
  },
  systemSectionRule: {
    flex: 1,
    height: '1px',
    backgroundColor: tokens.colorNeutralStroke2,
  },
  systemSectionLabel: {
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground3,
    textTransform: 'uppercase' as const,
    letterSpacing: '0.05em',
    whiteSpace: 'nowrap' as const,
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    cursor: 'pointer',
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
      border: `1px solid ${tokens.colorNeutralStroke1Hover}`,
    },
  },
  cardHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
  },
  cardInfo: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: '0',
    overflow: 'hidden',
  },
  cardName: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  cardRole: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  cardFooter: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    marginTop: tokens.spacingVerticalXS,
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
    textTransform: 'uppercase',
    letterSpacing: '0.05em',
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
  drawerFooterRow: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
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

  // Load catalog roles when dialog opens
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
  const [panelTab, setPanelTab] = useState<PanelTab>('overview');

  const [history, setHistory] = useState<HistoryDto | null>(null);
  const [historyError, setHistoryError] = useState<string | null>(null);
  const [historyLoaded, setHistoryLoaded] = useState(false);

  const [charter, setCharter] = useState<CharterDto | null>(null);
  const [charterError, setCharterError] = useState<string | null>(null);
  const [editContent, setEditContent] = useState('');
  const [saving, setSaving] = useState(false);
  const [charterLoaded, setCharterLoaded] = useState(false);

  // Derived loading states avoid synchronous setState calls inside effects
  const historyLoading = panelTab === 'overview' && !historyLoaded && historyError === null;
  const charterLoading = panelTab === 'charter' && !charterLoaded && charterError === null;

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

  // suppress unused variable warning — charter state is managed for side-effects
  void charter;

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
              color="brand"
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
          onTabSelect={(_, d) => { setPanelTab(d.value as PanelTab); }}
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
              <Text style={{ color: tokens.colorNeutralForeground3, fontStyle: 'italic' }}>
                Capabilities are defined in the agent&apos;s charter.
              </Text>
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

type TeamPageStyles = ReturnType<typeof useStyles>;

function MemberCard({
  member,
  styles,
  onClick,
}: {
  member: TeamMemberDto;
  styles: TeamPageStyles;
  onClick: () => void;
}) {
  return (
    <div
      className={styles.card}
      role="button"
      tabIndex={0}
      aria-label={`Open details for ${member.name}`}
      onClick={onClick}
      onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') onClick(); }}
    >
      <div className={styles.cardHeader}>
        <AgentAvatar
          name={member.name}
          size={40}
          isBuiltIn={member.is_built_in}
          isRetired={member.status === 'retired'}
        />
        <div className={styles.cardInfo}>
          <Text className={styles.cardName}>{member.name}</Text>
          <Text className={styles.cardRole}>{member.role_title}</Text>
        </div>
      </div>
      <div className={styles.cardFooter}>
        <span
          aria-hidden="true"
          style={{
            width: '8px',
            height: '8px',
            borderRadius: '50%',
            display: 'inline-block',
            flexShrink: 0,
            backgroundColor: member.status === 'active' ? '#107c10' : '#8a8886',
          }}
        />
        {member.is_built_in ? (
          <Badge appearance="tint" color="brand" size="small">
            System agent
          </Badge>
        ) : (
          <Badge appearance="tint" color="informative" size="small">
            Project agent
          </Badge>
        )}
      </div>
    </div>
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
    Promise.all([
      apiClient.getTeam(projectId).catch((err) => {
        if (err instanceof ApiError && err.status === 404) return null;
        throw err;
      }),
      apiClient.getTemplates().catch(() => [] as TeamTemplateDto[]),
      apiClient.getProject(projectId).catch(() => null as Project | null),
    ])
      .then(([t, s, p]) => {
        if (!cancelled) {
          setTeam(t);
          setScenarios(s);
          setProject(p);
        }
      })
      .catch((err) => {
        if (!cancelled) setError(
          err instanceof ApiError
            ? `API error ${err.status}: ${err.body}`
            : err instanceof Error ? err.message : String(err),
        );
      })
      .finally(() => { if (!cancelled) setLoading(false); });
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
    <div className={styles.root}>
      <div className={styles.breadcrumb}>
        <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
        <span>/</span>
        <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>
          {project?.name ?? team?.project_name ?? projectId}
        </Link>
        <span>/</span>
        <span>Team</span>
      </div>

      {loading && <Spinner label="Loading team" />}

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {!loading && (
        <PageHeader
          title="Agents"
          subtitle="The cast working on this project."
          actions={
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
                onClick={() => { navigate(`/projects/${projectId}/team/cast`); }}
              >
                Cast team
              </Button>
            </>
          }
        />
      )}

      {showSync && <SyncPanel projectId={projectId} />}

      {!loading && !team && !error && (
        <div className={styles.emptyState}>
          <Title3>No team yet</Title3>
          <Text>Cast a team to get started. The casting wizard will help you pick roles and generate agent charters.</Text>
          <Button
            appearance="primary"
            onClick={() => { navigate(`/projects/${projectId}/team/cast`); }}
          >
            Cast team
          </Button>
        </div>
      )}

      {team && members.length > 0 && (
        <>
          <TabList
            selectedValue={filterTab}
            onTabSelect={(_, d) => { setFilterTab(d.value as FilterTab); }}
          >
            <Tab value="all">All ({members.length})</Tab>
            <Tab value="active">Active ({activeCount})</Tab>
            <Tab value="retired">Retired ({retiredCount})</Tab>
          </TabList>

          {projectMembers.length > 0 && (
            <div className={styles.cardGrid}>
              {projectMembers.map((member) => (
                <MemberCard
                  key={member.name}
                  member={member}
                  styles={styles}
                  onClick={() => { setSelectedMember(member); }}
                />
              ))}
            </div>
          )}

          {builtInMembers.length > 0 && (
            <div className={styles.systemSection}>
              <div className={styles.systemSectionHeader}>
                <span className={styles.systemSectionLabel}>System agents</span>
                <div className={styles.systemSectionRule} />
              </div>
              <div className={styles.cardGrid}>
                {builtInMembers.map((member) => (
                  <MemberCard
                    key={member.name}
                    member={member}
                    styles={styles}
                    onClick={() => { setSelectedMember(member); }}
                  />
                ))}
              </div>
            </div>
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
    </div>
  );
}
