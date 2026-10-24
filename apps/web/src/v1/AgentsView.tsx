/**
 * Agents page: header with the 0.x Add member, Sync, and Cast team controls,
 * pending team proposals, a metric row, filter tabs, agent tiles, and a detail
 * drawer with Overview and Charter tabs. Agents come from the project
 * configuration's `agentCharters`; "In the team" means the agent has a casting
 * entry. Charter edits save a new configuration revision. Team changes (cast,
 * add, import) become casting proposals that apply only when confirmed.
 */
import {
  Badge, Button, DrawerBody, DrawerHeader, DrawerHeaderTitle, Field, Input, OverlayDrawer,
  Spinner, Tab, TabList, Text, Textarea, makeStyles, tokens,
} from '@fluentui/react-components';
import { Dismiss24Regular, People24Regular } from '@fluentui/react-icons';
import { useEffect, useState, type ReactNode } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { AgentAvatar } from '../components/AgentAvatar';
import { MetricRow } from '../components/ui/Metric';
import { PageSection } from '../components/ui/PageSection';
import { EmptyState } from '../components/ui/States';
import { Tile, TileGrid } from '../components/ui/TileGrid';
import { useAuth } from './AuthContext';
import { GatewayError, gatewayClient } from './api';
import { AddMemberDialog, PendingProposals, TeamMemberActions, TeamSyncPanel } from './AgentsTeamControls';
import { castPath } from './castingData';
import type { ProjectConfiguration } from './contracts';
import { PageHeading } from './pageParts';

type Filter = 'all' | 'team' | 'other';
type Charter = ProjectConfiguration['agentCharters'][number];

const useStyles = makeStyles({
  panel: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalL },
  label: { display: 'block', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  mono: { fontFamily: tokens.fontFamilyMonospace, overflowWrap: 'anywhere' },
  list: { margin: 0, paddingLeft: tokens.spacingHorizontalL },
  actions: { display: 'flex', gap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
  history: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  historyList: { display: 'grid', gap: tokens.spacingVerticalM, margin: 0, paddingLeft: tokens.spacingHorizontalL },
  historyEntry: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS },
  historyHeading: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalNone },
  historyContent: { whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' },
});

/** Drawer content for one agent. Saving replaces only this agent's charter entry. */
function AgentDetail({ projectId, agent, configuration, configurationRevision, readOnly, onClose, onSave, onConflict }: {
  projectId: string;
  agent: Charter;
  configuration: ProjectConfiguration;
  configurationRevision: number;
  readOnly: boolean;
  onClose: () => void;
  onSave: (next: ProjectConfiguration) => Promise<boolean>;
  onConflict?: () => void;
}) {
  const styles = useStyles();
  const { apiCall } = useAuth();
  // Form fields start from the agent's saved values. The parent remounts this
  // component (by key) when another agent is opened, so values never carry over.
  const [tab, setTab] = useState<'overview' | 'charter'>('overview');
  const [role, setRole] = useState(agent.role);
  const [charter, setCharter] = useState(agent.charter);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [historyResult, setHistoryResult] = useState<{
    requestKey: string;
    status: 'empty' | 'loaded' | 'permission' | 'error';
    items: Awaited<ReturnType<typeof gatewayClient.getProjectAgentHistory>>['items'];
  }>({ requestKey: '', status: 'empty', items: [] });
  const [historyAttempt, setHistoryAttempt] = useState(0);
  const historyRequestKey = JSON.stringify([projectId, agent.agentId, historyAttempt]);
  const currentHistory = historyResult.requestKey === historyRequestKey ? historyResult : null;
  const historyStatus = currentHistory?.status ?? 'loading';
  const historyItems = currentHistory?.items ?? [];
  // An agent is "in the team" only when it has a casting entry.
  const casting = configuration.casting.find((item) => item.agentId === agent.agentId);
  // A skill applies if it is enabled and either lists no agents (all agents) or lists this one.
  const skills = configuration.skills.filter((skill) => skill.enabled && (!skill.agentIds?.length || skill.agentIds.includes(agent.agentId)));
  // Save is enabled only after a real change.
  const dirty = role !== agent.role || charter !== agent.charter;
  /** Saves a full configuration where only this agent's role and charter differ. */
  const save = async () => {
    setSaving(true);
    setSaved(false);
    // The configuration is saved as a whole revision, so copy it and replace just
    // this one charter entry. The role is trimmed; the charter text is kept as typed.
    const ok = await onSave({
      ...configuration,
      agentCharters: configuration.agentCharters.map((item) => item.agentId === agent.agentId ? { ...item, role: role.trim(), charter } : item),
    });
    setSaving(false);
    // Show "Charter saved." only when onSave reports success.
    setSaved(ok);
  };

  useEffect(() => {
    if (tab !== 'overview')
      return;

    let current = true;
    // Ignore late responses after the drawer closes, changes tabs, or opens another agent.
    void apiCall((token, tenantSelector) => gatewayClient.getProjectAgentHistory(
      token, projectId, agent.agentId, tenantSelector))
      .then((history) => {
        if (!current)
          return;
        setHistoryResult({
          requestKey: historyRequestKey,
          status: history.items.length === 0 ? 'empty' : 'loaded',
          items: history.items,
        });
      })
      .catch((error: unknown) => {
        if (!current)
          return;
        setHistoryResult({
          requestKey: historyRequestKey,
          status: error instanceof GatewayError && error.status === 403 ? 'permission' : 'error',
          items: [],
        });
      });

    return () => {
      current = false;
    };
  }, [apiCall, agent.agentId, historyAttempt, historyRequestKey, projectId, tab]);

  return (
    <>
      <DrawerHeader>
        <DrawerHeaderTitle action={<Button appearance="subtle" aria-label="Close panel" icon={<Dismiss24Regular />} onClick={onClose} />}>
          {agent.name}
        </DrawerHeaderTitle>
        <Text size={200}>{agent.role}</Text>
      </DrawerHeader>
      <TabList selectedValue={tab} onTabSelect={(_, data) => {
        const nextTab = data.value as 'overview' | 'charter';
        setTab(nextTab);
        if (nextTab === 'overview')
          setHistoryAttempt((attempt) => attempt + 1);
      }} aria-label="Agent detail sections">
        <Tab value="overview">Overview</Tab>
        <Tab value="charter">Charter</Tab>
      </TabList>
      <DrawerBody>
        <div className={styles.panel}>
          {tab === 'overview' && (
            <>
              <div>
                <Text className={styles.label}>Agent ID</Text>
                <Text className={styles.mono}>{agent.agentId}</Text>
              </div>
              <div>
                <Text className={styles.label}>Team</Text>
                <Text>{casting ? `In the team as ${casting.role}, position ${casting.order}` : 'Not in the team'}</Text>
              </div>
              <div>
                <Text className={styles.label}>Skills</Text>
                {skills.length === 0 ? <Text>No skills assigned.</Text> : (
                  <ul className={styles.list}>{skills.map((skill) => <li key={skill.skillId}>{skill.skillId}</li>)}</ul>
                )}
              </div>
              <section className={styles.history} aria-label="Agent history" aria-live="polite">
                <Text weight="semibold">Recent history</Text>
                {historyStatus === 'loading' && <Spinner size="tiny" label="Loading history…" />}
                {historyStatus === 'empty' && <Text>No history yet.</Text>}
                {historyStatus === 'permission' && <Text role="status">You do not have permission to view this history.</Text>}
                {historyStatus === 'error' && (
                  <>
                    <Text role="alert">History could not be loaded.</Text>
                    <Button appearance="secondary" onClick={() => setHistoryAttempt((attempt) => attempt + 1)}>Retry</Button>
                  </>
                )}
                {historyStatus === 'loaded' && (
                  <ol className={styles.historyList}>
                    {historyItems.map((item) => (
                      <li className={styles.historyEntry} key={`${item.recordId}:${item.updatedAt}`}>
                        <div className={styles.historyHeading}>
                          <Text weight="semibold">{item.title?.trim() || item.type}</Text>
                          <Text size={200}>
                            <time dateTime={item.updatedAt}>{new Date(item.updatedAt).toLocaleString()}</time>
                          </Text>
                        </div>
                        <Text className={styles.historyContent}>
                          {item.content.length > 1000 ? `${item.content.slice(0, 1000)}…` : item.content}
                        </Text>
                      </li>
                    ))}
                  </ol>
                )}
              </section>
              {casting && !readOnly && (
                <TeamMemberActions
                  projectId={projectId}
                  configuration={configuration}
                  configurationRevision={configurationRevision}
                  agentId={agent.agentId}
                  currentRoleId={casting.role}
                  onConflict={onConflict}
                />
              )}
              <div className={styles.actions}>
                <Link className="v1-link-button" to={`/projects/${encodeURIComponent(projectId)}/memories?agentId=${encodeURIComponent(agent.agentId)}`}>View memories</Link>
                <Link className="v1-link-button" to={`/projects/${encodeURIComponent(projectId)}/skills`}>Manage skills</Link>
              </div>
            </>
          )}
          {tab === 'charter' && (
            <>
              <Field label="Role" hint={casting ? 'Change team roles through a reviewed proposal.' : undefined}>
                <Input value={role} readOnly={readOnly || Boolean(casting)} onChange={(_, data) => { setRole(data.value); setSaved(false); }} />
              </Field>
              <Field label="Charter content" hint="Instructions this agent follows in every run. Saved changes apply to future runs.">
                <Textarea value={charter} readOnly={readOnly} resize="vertical" rows={16} onChange={(_, data) => { setCharter(data.value); setSaved(false); }} />
              </Field>
              {!readOnly && (
                <div className={styles.actions}>
                  <Button appearance="primary" disabled={saving || !dirty || !role.trim()} onClick={() => void save()}>
                    {saving ? 'Saving…' : 'Save charter'}
                  </Button>
                  {saved && <Text>Charter saved.</Text>}
                </div>
              )}
            </>
          )}
        </div>
      </DrawerBody>
    </>
  );
}

/**
 * Renders the Agents page for a loaded configuration. `onSave` resolves true
 * when the new revision is saved. `status` holds page messages shown under the
 * header, and `onReload` re-reads the configuration after a revision conflict.
 */
export function AgentsView({ projectId, configuration, configurationRevision, readOnly, onSave, status, onReload }: {
  projectId: string;
  configuration: ProjectConfiguration;
  configurationRevision: number;
  readOnly: boolean;
  onSave: (next: ProjectConfiguration) => Promise<boolean>;
  status?: ReactNode;
  onReload?: () => void;
}) {
  const navigate = useNavigate();
  const [filter, setFilter] = useState<Filter>('all');
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [showSync, setShowSync] = useState(false);
  // IDs of agents with a casting entry, meaning they are in the team.
  const cast = new Set(configuration.casting.map((item) => item.agentId));
  const agents = configuration.agentCharters;
  const teamCount = agents.filter((agent) => cast.has(agent.agentId)).length;
  // "all" shows everyone. "team" keeps cast agents and "other" keeps the rest.
  const visible = agents.filter((agent) => filter === 'all' || (filter === 'team') === cast.has(agent.agentId));
  // Look the agent up by ID on every render, so the drawer shows the latest saved
  // configuration and closes if the agent no longer exists.
  const selected = agents.find((agent) => agent.agentId === selectedId) ?? null;
  const openCast = () => navigate(castPath(projectId));

  // 0.x header controls. They stay off while a configuration save is running,
  // because proposals are fenced to the revision shown here.
  const header = (
    <PageHeading
      title="Agents"
      description="The cast working on this project."
      page="Agents"
      actions={<>
        <AddMemberDialog
          projectId={projectId}
          configuration={configuration}
          configurationRevision={configurationRevision}
          disabled={readOnly}
          onConflict={onReload}
        />
        <Button appearance="secondary" onClick={() => setShowSync((value) => !value)}>{showSync ? 'Hide sync' : 'Sync'}</Button>
        <Button appearance="primary" icon={<People24Regular />} disabled={readOnly} onClick={openCast}>Cast team</Button>
      </>}
    />
  );
  const top = (
    <>
      {header}
      {status}
      {showSync && <TeamSyncPanel projectId={projectId} configurationRevision={configurationRevision} onConflict={onReload} />}
      <PendingProposals projectId={projectId} />
    </>
  );

  if (agents.length === 0) {
    return (
      <>
        {top}
        <EmptyState
          title="No team yet"
          description="Cast a team to get started. Pick a template or roles, then review the proposed agents before they apply."
          action={<Button appearance="primary" disabled={readOnly} onClick={openCast}>Cast team</Button>}
        />
      </>
    );
  }
  return (
    <>
      {top}
      <MetricRow items={[
        { label: 'Total', value: agents.length },
        { label: 'In the team', value: teamCount },
        { label: 'Not in the team', value: agents.length - teamCount },
      ]} />
      <TabList selectedValue={filter} onTabSelect={(_, data) => setFilter(data.value as Filter)} aria-label="Filter agents">
        <Tab value="all">All ({agents.length})</Tab>
        <Tab value="team">In the team ({teamCount})</Tab>
        <Tab value="other">Not in the team ({agents.length - teamCount})</Tab>
      </TabList>
      {visible.length === 0 ? <EmptyState title="No agents match" description="Choose another filter to see more agents." /> : (
        <PageSection title={filter === 'other' ? 'Not in the team' : filter === 'team' ? 'In the team' : 'Project agents'}>
          <TileGrid aria-label="Project agents">
            {visible.map((agent) => (
              <Tile
                key={agent.agentId}
                media={<AgentAvatar name={agent.name} size={32} />}
                bubble={false}
                badges={cast.has(agent.agentId)
                  ? <Badge appearance="tint" color="success" size="small">In the team</Badge>
                  : <Badge appearance="tint" color="subtle" size="small">Not in the team</Badge>}
                primary={agent.name}
                secondary={agent.role}
                onClick={() => setSelectedId(agent.agentId)}
              />
            ))}
          </TileGrid>
        </PageSection>
      )}
      <OverlayDrawer open={selected !== null} onOpenChange={(_, data) => { if (!data.open) setSelectedId(null); }} position="end" size="medium">
        {selected && (
          <AgentDetail
            key={selected.agentId}
            projectId={projectId}
            agent={selected}
            configuration={configuration}
            configurationRevision={configurationRevision}
            readOnly={readOnly}
            onClose={() => setSelectedId(null)}
            onSave={onSave}
            onConflict={onReload}
          />
        )}
      </OverlayDrawer>
    </>
  );
}
