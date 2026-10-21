/**
 * Agents in the 0.x Team layout: metric row, filter tabs, agent tiles, and a
 * detail drawer with Overview and Charter tabs. Agents come from the project
 * configuration's `agentCharters`; "In the team" means the agent has a casting
 * entry. Charter edits save a new configuration revision. Casting, adding, and
 * removing agents need casting support in the Gateway, which does not exist yet,
 * so they are not shown.
 */
import {
  Badge, Button, DrawerBody, DrawerHeader, DrawerHeaderTitle, Field, Input, OverlayDrawer,
  Tab, TabList, Text, Textarea, makeStyles, tokens,
} from '@fluentui/react-components';
import { Dismiss24Regular } from '@fluentui/react-icons';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { AgentAvatar } from '../components/AgentAvatar';
import { MetricRow } from '../components/ui/Metric';
import { PageSection } from '../components/ui/PageSection';
import { EmptyState } from '../components/ui/States';
import { Tile, TileGrid } from '../components/ui/TileGrid';
import type { ProjectConfiguration } from './contracts';

type Filter = 'all' | 'team' | 'other';
type Charter = ProjectConfiguration['agentCharters'][number];

const useStyles = makeStyles({
  panel: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalL },
  label: { display: 'block', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  mono: { fontFamily: tokens.fontFamilyMonospace, overflowWrap: 'anywhere' },
  list: { margin: 0, paddingLeft: tokens.spacingHorizontalL },
  actions: { display: 'flex', gap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
});

/** Drawer content for one agent. Saving replaces only this agent's charter entry. */
function AgentDetail({ projectId, agent, configuration, readOnly, onClose, onSave }: {
  projectId: string;
  agent: Charter;
  configuration: ProjectConfiguration;
  readOnly: boolean;
  onClose: () => void;
  onSave: (next: ProjectConfiguration) => Promise<boolean>;
}) {
  const styles = useStyles();
  // Form fields start from the agent's saved values. The parent remounts this
  // component (by key) when another agent is opened, so values never carry over.
  const [tab, setTab] = useState<'overview' | 'charter'>('overview');
  const [role, setRole] = useState(agent.role);
  const [charter, setCharter] = useState(agent.charter);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
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
  return (
    <>
      <DrawerHeader>
        <DrawerHeaderTitle action={<Button appearance="subtle" aria-label="Close panel" icon={<Dismiss24Regular />} onClick={onClose} />}>
          {agent.name}
        </DrawerHeaderTitle>
        <Text size={200}>{agent.role}</Text>
      </DrawerHeader>
      <TabList selectedValue={tab} onTabSelect={(_, data) => setTab(data.value as 'overview' | 'charter')} aria-label="Agent detail sections">
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
              <div className={styles.actions}>
                <Link className="v1-link-button" to={`/projects/${encodeURIComponent(projectId)}/memories?agentId=${encodeURIComponent(agent.agentId)}`}>View memories</Link>
                <Link className="v1-link-button" to={`/projects/${encodeURIComponent(projectId)}/skills`}>Manage skills</Link>
              </div>
            </>
          )}
          {tab === 'charter' && (
            <>
              <Field label="Role">
                <Input value={role} readOnly={readOnly} onChange={(_, data) => { setRole(data.value); setSaved(false); }} />
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

/** Renders the project's agents. `onSave` resolves true when the new revision is saved. */
export function AgentsView({ projectId, configuration, readOnly, onSave }: {
  projectId: string;
  configuration: ProjectConfiguration;
  readOnly: boolean;
  onSave: (next: ProjectConfiguration) => Promise<boolean>;
}) {
  const [filter, setFilter] = useState<Filter>('all');
  const [selectedId, setSelectedId] = useState<string | null>(null);
  // Agent ids that have a casting entry, i.e. are in the team.
  const cast = new Set(configuration.casting.map((item) => item.agentId));
  const agents = configuration.agentCharters;
  const teamCount = agents.filter((agent) => cast.has(agent.agentId)).length;
  // 'all' shows everyone. Otherwise keep agents whose team membership matches the
  // tab: 'team' wants members, 'other' wants non-members.
  const visible = agents.filter((agent) => filter === 'all' || (filter === 'team') === cast.has(agent.agentId));
  // Look the agent up by id on each render, so the drawer closes on its own if
  // the agent disappears from a newer configuration.
  const selected = agents.find((agent) => agent.agentId === selectedId) ?? null;

  if (agents.length === 0) {
    return <EmptyState title="No agents yet" description="This project has no agent charters. Add them in Project settings, Advanced." />;
  }
  return (
    <>
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
            readOnly={readOnly}
            onClose={() => setSelectedId(null)}
            onSave={onSave}
          />
        )}
      </OverlayDrawer>
    </>
  );
}
