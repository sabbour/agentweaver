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
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  Textarea,
  Title2,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type {
  TeamDto,
  TeamMemberDto,
  CharterDto,
  TeamTemplateDto,
  RoleDto,
  AddMemberRequest,
  ReroleRequest,
  Project,
} from '../api/types';
import { SyncPanel } from '../components/SyncPanel';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    maxWidth: '960px',
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
  pageHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  actions: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
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
  charterContent: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
    backgroundColor: tokens.colorNeutralBackground2,
    padding: tokens.spacingVerticalS,
    borderRadius: tokens.borderRadiusSmall,
    maxHeight: '400px',
    overflowY: 'auto',
  },
  tableActions: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
  },
});

function CharterDialog({
  projectId,
  member,
}: {
  projectId: string;
  member: TeamMemberDto;
}) {
  const styles = useStyles();
  const [open, setOpen] = useState(false);
  const [charter, setCharter] = useState<CharterDto | null>(null);
  const [loading, setLoading] = useState(false);
  const [editing, setEditing] = useState(false);
  const [editContent, setEditContent] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = async () => {
    setLoading(true);
    setError(null);
    try {
      const c = await apiClient.getMemberCharter(projectId, member.name);
      setCharter(c);
      setEditContent(c.content);
    } catch (err) {
      setError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error ? err.message : String(err),
      );
    } finally {
      setLoading(false);
    }
  };

  const handleSave = async () => {
    setSaving(true);
    setError(null);
    try {
      await apiClient.updateMemberCharter(projectId, member.name, editContent);
      setCharter({ member_name: member.name, content: editContent });
      setEditing(false);
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
    <Dialog
      open={open}
      onOpenChange={(_, s) => {
        setOpen(s.open);
        if (s.open) void load();
        else { setEditing(false); setError(null); }
      }}
    >
      <DialogTrigger disableButtonEnhancement>
        <Button appearance="subtle" size="small">View charter</Button>
      </DialogTrigger>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Charter — {member.name}</DialogTitle>
          <DialogContent>
            <div className={styles.dialogFields}>
              {loading && <Spinner label="Loading charter" />}
              {error && (
                <MessageBar intent="error">
                  <MessageBarBody>{error}</MessageBarBody>
                </MessageBar>
              )}
              {charter && !loading && !editing && (
                <pre className={styles.charterContent}>{charter.content}</pre>
              )}
              {charter && !loading && editing && (
                <Field label="Charter content">
                  <Textarea
                    value={editContent}
                    onChange={(_, v) => setEditContent(v.value)}
                    rows={16}
                  />
                </Field>
              )}
            </div>
          </DialogContent>
          <DialogActions>
            <DialogTrigger disableButtonEnhancement>
              <Button appearance="secondary" disabled={saving}>Close</Button>
            </DialogTrigger>
            {charter && !editing && (
              <Button appearance="secondary" onClick={() => setEditing(true)}>Edit</Button>
            )}
            {editing && (
              <Button
                appearance="primary"
                disabled={saving}
                onClick={() => void handleSave()}
              >
                {saving ? 'Saving' : 'Save'}
              </Button>
            )}
            {saving && <Spinner size="extra-tiny" aria-hidden="true" />}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

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
  scenarios,
  onAdded,
}: {
  projectId: string;
  scenarios: TeamTemplateDto[];
  onAdded: (member: TeamMemberDto) => void;
}) {
  const [open, setOpen] = useState(false);
  const [roleId, setRoleId] = useState('');
  const [customTitle, setCustomTitle] = useState('');
  const [modelId, setModelId] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const allRoles: RoleDto[] = scenarios.flatMap((g) => g.roles);

  const reset = () => { setRoleId(''); setCustomTitle(''); setModelId(''); setError(null); setSaving(false); };

  const handleAdd = async () => {
    if (!roleId) return;
    setSaving(true);
    setError(null);
    try {
      const req: AddMemberRequest = {
        role_id: roleId,
        custom_role_title: customTitle.trim() || undefined,
        model_id: modelId.trim() || undefined,
      };
      const member = await apiClient.addMember(projectId, req);
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

  return (
    <Dialog open={open} onOpenChange={(_, s) => { setOpen(s.open); if (!s.open) reset(); }}>
      <DialogTrigger disableButtonEnhancement>
        <Button appearance="secondary">Add member</Button>
      </DialogTrigger>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Add team member</DialogTitle>
          <DialogContent>
            <div style={{ display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM }}>
              <Field label="Role" required>
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
              <Field label="Model ID (optional)">
                <Input
                  value={modelId}
                  onChange={(_, v) => setModelId(v.value)}
                  placeholder="e.g. gpt-4o"
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
              onClick={() => void handleAdd()}
            >
              {saving ? 'Adding' : 'Add'}
            </Button>
            {saving && <Spinner size="extra-tiny" aria-hidden="true" />}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
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
    void apiClient.getTeam(projectId).then(setTeam).catch(() => setTeam(null));
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
        <div className={styles.pageHeader}>
          <Title2>Team</Title2>
          <div className={styles.actions}>
            {team && (
              <AddMemberDialog
                projectId={projectId}
                scenarios={scenarios}
                onAdded={handleMemberAdded}
              />
            )}
            <Button
              appearance="secondary"
              onClick={() => setShowSync((v) => !v)}
            >
              {showSync ? 'Hide sync' : 'Sync'}
            </Button>
            <Button
              appearance="primary"
              onClick={() => navigate(`/projects/${projectId}/team/cast`)}
            >
              Cast team
            </Button>
          </div>
        </div>
      )}

      {showSync && <SyncPanel projectId={projectId} />}

      {!loading && !team && !error && (
        <div className={styles.emptyState}>
          <Title3>No team yet</Title3>
          <Text>Cast a team to get started. The casting wizard will help you pick roles and generate agent charters.</Text>
          <Button
            appearance="primary"
            onClick={() => navigate(`/projects/${projectId}/team/cast`)}
          >
            Cast team
          </Button>
        </div>
      )}

      {team && team.members.length > 0 && (
        <>
          <Table aria-label="Team roster">
            <TableHeader>
              <TableRow>
                <TableHeaderCell>Name</TableHeaderCell>
                <TableHeaderCell>Role</TableHeaderCell>
                <TableHeaderCell>Status</TableHeaderCell>
                <TableHeaderCell>Default model</TableHeaderCell>
                <TableHeaderCell>Actions</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {team.members.map((member) => (
                <TableRow key={member.name}>
                  <TableCell>{member.name}</TableCell>
                  <TableCell>{member.role_title}</TableCell>
                  <TableCell>
                    <Badge
                      appearance="tint"
                      color={member.status === 'active' ? 'success' : 'subtle'}
                    >
                      {member.status}
                    </Badge>
                  </TableCell>
                  <TableCell>
                    <Text style={{ fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200 }}>
                      {member.default_model}
                    </Text>
                  </TableCell>
                  <TableCell>
                    <div className={styles.tableActions}>
                      <CharterDialog projectId={projectId} member={member} />
                      <ReroleDialog
                        projectId={projectId}
                        member={member}
                        scenarios={scenarios}
                        onReroled={handleMemberReroled}
                      />
                      <RemoveMemberDialog
                        projectId={projectId}
                        member={member}
                        onRemoved={handleMemberRemoved}
                      />
                    </div>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </>
      )}
    </div>
  );
}
