import { apiClient } from '../api/apiClient';
import { normalizeBlueprintList } from '../api/client';
import {
  Badge,
  Button,
  Card,
  Field,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  Textarea,
  Tooltip,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import {
  BotRegular,
  CheckmarkRegular,
  ChevronDownRegular,
  ChevronRightRegular,
  DocumentRegular,
  FlowchartRegular,
  InfoRegular,
  PeopleTeamRegular,
  SparkleRegular,
} from '@fluentui/react-icons';
import { useEffect, useState } from 'react';
import type { Blueprint, SuggestBlueprintResponse } from '../api/types';
import type { ReactElement } from 'react';
export type BlueprintSelection =
  | { kind: 'none' }
  | { kind: 'predefined'; blueprint: Blueprint }
  | { kind: 'generated'; blueprint: Blueprint; generatedWorkflowYaml?: string | null };

export const NO_BLUEPRINT: BlueprintSelection = { kind: 'none' };

export type BlueprintPanelTab = 'suggested' | 'templates' | 'generate';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM, minHeight: 0 },
  panel: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM, minHeight: 0, height: '100%' },
  panelHeader: { display: 'flex', alignItems: 'flex-start', gap: tokens.spacingHorizontalM },
  panelIcon: { width: '36px', height: '36px', borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground3, color: tokens.colorNeutralForeground2, display: 'inline-flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 },
  tabStrip: { display: 'flex', width: '100%', borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, gap: tokens.spacingHorizontalL },
  tabButton: { appearance: 'none', border: 0, borderBottom: '2px solid transparent', backgroundColor: 'transparent', color: tokens.colorNeutralForeground3, cursor: 'pointer', padding: `${tokens.spacingVerticalS} 0`, marginBottom: '-1px', fontWeight: tokens.fontWeightSemibold, display: 'inline-flex', alignItems: 'center', gap: tokens.spacingHorizontalXXS },
  tabButtonActive: { color: tokens.colorNeutralForeground1, borderBottomColor: tokens.colorNeutralStroke1 },
  panelBody: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalL, minHeight: 0, overflowY: 'auto', paddingRight: tokens.spacingHorizontalXS },
  sectionHeader: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: tokens.spacingHorizontalM },
  subtle: { color: tokens.colorNeutralForeground3 },
  // Secondary guidance in empty states. colorNeutralForeground2 stays a clearly
  // legible >= 4.5:1 on the card background, unlike the more muted Foreground3.
  emptyHint: { color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase200 },
  emptyIcon: { color: tokens.colorNeutralForeground3 },
  suggestedActions: { display: 'flex', justifyContent: 'flex-end', gap: tokens.spacingHorizontalS },
  emptyCard: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: tokens.spacingVerticalXS, padding: tokens.spacingVerticalXL, textAlign: 'center', backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke2}`, minHeight: '140px', justifyContent: 'center', overflowWrap: 'anywhere' },
  tabLinks: { display: 'flex', flexWrap: 'wrap', gap: tokens.spacingHorizontalS },
  // Dense list-row layout: one template per row so many fit without scrolling.
  // The per-agent roster lives in a focus/hover popover (see TemplateRow), not
  // inline, which is what keeps each row to a single compact line.
  templateList: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS },
  templateRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    width: '100%',
    minWidth: 0,
    boxSizing: 'border-box',
    textAlign: 'left',
    cursor: 'pointer',
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  // Selected uses the same 2px brand-stroke / brand-tint language as the
  // "No blueprint" control: an inner brand ring (via box-shadow, so the row does
  // not reflow) plus a brand-colored border and a subtle brand tint.
  templateRowSelected: {
    backgroundColor: tokens.colorNeutralBackground3,
    borderTopColor: tokens.colorNeutralStroke1,
    borderRightColor: tokens.colorNeutralStroke1,
    borderBottomColor: tokens.colorNeutralStroke1,
    borderLeftColor: tokens.colorNeutralStroke1,
    boxShadow: `inset 0 0 0 1px ${tokens.colorNeutralStroke1}`,
    ':hover': { backgroundColor: tokens.colorNeutralBackground3 },
  },
  rowIcon: { width: '28px', height: '28px', borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground3, color: tokens.colorNeutralForeground2, display: 'inline-flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 },
  rowMain: { display: 'flex', flexDirection: 'column', minWidth: 0, flexGrow: 1 },
  rowTitle: { fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase300, color: tokens.colorNeutralForeground1, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' },
  // colorNeutralForeground2 keeps the muted look while staying >= 4.5:1 on the
  // row background (colorNeutralForeground3 would drop below on the brand tint).
  rowDesc: { color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase200, lineHeight: tokens.lineHeightBase200, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' },
  rowTrailing: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalM, flexShrink: 0, marginLeft: 'auto', minWidth: 0 },
  agentPill: { display: 'inline-flex', alignItems: 'center', gap: tokens.spacingHorizontalXXS, fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3, whiteSpace: 'nowrap', flexShrink: 0 },
  // Compact workflow indicator. Capped width + ellipsis so a long workflow name
  // never reflows the single-line row. colorNeutralForeground3 stays >= 4.5:1 on
  // both the neutral and brand-tint row backgrounds.
  workflowPill: { display: 'inline-flex', alignItems: 'center', gap: tokens.spacingHorizontalXXS, fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3, whiteSpace: 'nowrap', minWidth: 0, maxWidth: '160px' },
  workflowName: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 },
  rosterPop: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS, maxWidth: '240px' },
  rosterList: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS },
  rosterItem: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalXS, fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground1 },
  cardTitleRow: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: tokens.spacingHorizontalS },
  cardTitle: { fontWeight: tokens.fontWeightSemibold },
  cardDescription: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, lineHeight: tokens.lineHeightBase200 },
  chips: { display: 'flex', flexWrap: 'wrap', gap: tokens.spacingHorizontalXS, marginTop: tokens.spacingVerticalXXS },
  roleRows: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS, marginTop: tokens.spacingVerticalXS },
  roleRow: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalXS, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  bindingSection: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS, marginTop: tokens.spacingVerticalXS },
  bindingRows: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS },
  bindingRow: { display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: tokens.spacingHorizontalXS, fontSize: tokens.fontSizeBase200 },
  bindingRole: { color: tokens.colorNeutralForeground1, fontWeight: tokens.fontWeightSemibold },
  roleDot: { width: '14px', height: '14px', borderRadius: tokens.borderRadiusSmall, backgroundColor: tokens.colorPalettePurpleBackground2, color: tokens.colorPalettePurpleForeground2, display: 'inline-flex', alignItems: 'center', justifyContent: 'center', fontSize: '9px', flexShrink: 0 },
  previewCard: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS, padding: tokens.spacingVerticalM },
  metaRow: { display: 'flex', flexWrap: 'wrap', gap: tokens.spacingHorizontalS, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  generateBox: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  generateBar: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS },
  suggestedHeader: { display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: tokens.spacingHorizontalS },
  // Working state shown while a blueprint is being generated — a purposeful
  // "at work" surface, not a static spinner.
  workingCard: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: tokens.spacingVerticalS, padding: tokens.spacingVerticalXL, textAlign: 'center', backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke2}`, minHeight: '140px', justifyContent: 'center' },
  workingBubble: { width: '48px', height: '48px', borderRadius: tokens.borderRadiusCircular, backgroundColor: tokens.colorNeutralBackground3, color: tokens.colorNeutralForeground2, display: 'inline-flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 },
  // Gentle breathe on the sparkle bubble. Motion conveys "working"; disabled
  // under reduced-motion where the bubble simply sits static.
  sparklePulse: {
    animationName: {
      '0%, 100%': { transform: 'scale(1)', opacity: '0.85' },
      '50%': { transform: 'scale(1.12)', opacity: '1' },
    },
    animationDuration: '1.8s',
    animationIterationCount: 'infinite',
    animationTimingFunction: tokens.curveEasyEase,
    '@media (prefers-reduced-motion: reduce)': { animationName: 'none', transform: 'none', opacity: '1' },
  },
  workingStatus: { minHeight: tokens.lineHeightBase300, color: tokens.colorNeutralForeground3 },
  // One-time rise+fade when the generated preview first appears.
  revealCard: {
    animationName: {
      '0%': { transform: 'translateY(8px)', opacity: '0' },
      '100%': { transform: 'translateY(0)', opacity: '1' },
    },
    animationDuration: '340ms',
    animationTimingFunction: tokens.curveDecelerateMid,
    animationFillMode: 'both',
    '@media (prefers-reduced-motion: reduce)': { animationName: 'none', transform: 'none', opacity: '1' },
  },
  // One-time pop on the "Generated" badge as the result lands.
  badgePop: {
    animationName: {
      '0%': { transform: 'scale(0.7)', opacity: '0' },
      '60%': { transform: 'scale(1.08)' },
      '100%': { transform: 'scale(1)', opacity: '1' },
    },
    animationDuration: tokens.durationGentle,
    animationTimingFunction: tokens.curveDecelerateMid,
    animationFillMode: 'both',
    '@media (prefers-reduced-motion: reduce)': { animationName: 'none', transform: 'none', opacity: '1' },
  },
  // Sparkle icon on the Generate button pulses while work is in flight.
  buttonSparkle: {
    display: 'inline-flex',
    animationName: {
      '0%, 100%': { opacity: '0.6', transform: 'scale(0.92)' },
      '50%': { opacity: '1', transform: 'scale(1.1)' },
    },
    animationDuration: '1.4s',
    animationIterationCount: 'infinite',
    animationTimingFunction: tokens.curveEasyEase,
    '@media (prefers-reduced-motion: reduce)': { animationName: 'none', transform: 'none', opacity: '1' },
  },
});

// Product-specific steps that mirror what a blueprint actually is: a squad, a
// workflow, and a review policy. Informative (not decorative) so it may run
// even under reduced-motion.
const GENERATION_STEPS = [
  'Reading your goal',
  'Casting the squad',
  'Choosing a workflow',
  'Setting the review policy',
  'Almost ready',
];

function RotatingStatus() {
  const styles = useStyles();
  const [index, setIndex] = useState(0);
  useEffect(() => {
    if (index >= GENERATION_STEPS.length - 1) return;
    const timer = setTimeout(() => setIndex((i) => Math.min(i + 1, GENERATION_STEPS.length - 1)), 1400);
    return () => clearTimeout(timer);
  }, [index]);
  return <Text size={200} className={styles.workingStatus} aria-live="polite">{GENERATION_STEPS[index]}</Text>;
}

export function useBlueprintCatalog(active: boolean) {
  const [blueprints, setBlueprints] = useState<Blueprint[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!active) return;
    let cancelled = false;
    setLoading(true);
    setError(null);
    apiClient.listBlueprints()
      .then((list) => {
        if (cancelled) return;
        setBlueprints(normalizeBlueprintList(list));
        setLoading(false);
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        setError(err instanceof Error ? err.message : String(err));
        setLoading(false);
      });
    return () => { cancelled = true; };
  }, [active]);

  return { blueprints, loading, error };
}

export function useBlueprintGeneration(onChange: (selection: BlueprintSelection) => void, targetRepository?: string | null) {
  const [generating, setGenerating] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [generated, setGenerated] = useState<{ blueprint: Blueprint; generatedWorkflowYaml?: string | null } | null>(null);

  const generate = async (description: string) => {
    if (!description.trim()) return;
    setGenerating(true);
    setError(null);
    try {
      const res = targetRepository
        ? await apiClient.generateBlueprint(description.trim(), targetRepository)
        : await apiClient.generateBlueprint(description.trim());
      const next = { blueprint: res.blueprint, generatedWorkflowYaml: res.generated_workflow_yaml };
      setGenerated(next);
      onChange({ kind: 'generated', blueprint: next.blueprint, generatedWorkflowYaml: next.generatedWorkflowYaml });
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setGenerating(false);
    }
  };

  return { generated, generating, error, generate, setGenerated };
}

export function BlueprintRosterChips({ roster, limit }: { roster: string[]; limit?: number }) {
  const styles = useStyles();
  const visible = typeof limit === 'number' ? roster.slice(0, limit) : roster;
  const remaining = typeof limit === 'number' ? Math.max(0, roster.length - limit) : 0;
  if (roster.length === 0) return null;
  return (
    <div className={styles.chips}>
      {visible.map((role) => <Badge key={role} appearance="outline" size="small">{role}</Badge>)}
      {remaining > 0 && <Badge appearance="outline" size="small">+{remaining}</Badge>}
    </div>
  );
}

// A blueprint bundles one or more workflows; the first is the default. Prefer the
// full `workflows` set, falling back to the legacy single `workflow` for older
// payloads, and to an empty list when neither is present.
function workflowList(blueprint: Blueprint): string[] {
  if (blueprint.workflows?.length) return blueprint.workflows;
  return blueprint.workflow ? [blueprint.workflow] : [];
}

export function BlueprintMeta({ blueprint }: { blueprint: Blueprint }) {
  const styles = useStyles();
  const workflows = workflowList(blueprint);
  return (
    <div className={styles.metaRow}>
      <span>{blueprint.roster.length} agents</span>
      {workflows.length === 1 && <span>Workflow: {workflows[0]}</span>}
      {workflows.length > 1 && <span>Workflows: {workflows.join(', ')}</span>}
      {blueprint.review_policy && <span>Review: {blueprint.review_policy}</span>}
    </div>
  );
}

export function BlueprintPreviewCard({ blueprint, generated, reveal }: { blueprint: Blueprint; generated?: boolean; reveal?: boolean }) {
  const styles = useStyles();
  return (
    <Card className={mergeClasses(styles.previewCard, reveal && styles.revealCard)} aria-label={generated ? 'Generated blueprint preview' : `${blueprint.name} blueprint preview`}>
      <div className={styles.cardTitleRow}>
        <Text className={styles.cardTitle}>{blueprint.name}</Text>
        {generated && <Badge className={reveal ? styles.badgePop : undefined} appearance="tint" color="success" size="small">Generated</Badge>}
      </div>
      {blueprint.description && <Text className={styles.cardDescription}>{blueprint.description}</Text>}
      <BlueprintRosterChips roster={blueprint.roster} />
      <BlueprintSkillBindings bindings={blueprint.skill_bindings} />
      <BlueprintMeta blueprint={blueprint} />
    </Card>
  );
}

export function BlueprintSkillBindings({ bindings }: { bindings?: Blueprint['skill_bindings'] }) {
  const styles = useStyles();
  if (!bindings?.length) return null;

  return (
    <section className={styles.bindingSection} aria-label="Role-to-skill defaults">
      <Text size={200} weight="semibold">Role-to-skill defaults</Text>
      <div className={styles.bindingRows} role="list">
        {bindings.map((binding) => (
          <div className={styles.bindingRow} role="listitem" key={binding.role_id}>
            <span className={styles.bindingRole}>{binding.role_id}</span>
            <span aria-hidden="true">→</span>
            {binding.skills.map((skill) => <Badge key={skill} appearance="tint" size="small">{skill}</Badge>)}
          </div>
        ))}
      </div>
    </section>
  );
}

function TemplateRosterList({ blueprint }: { blueprint: Blueprint }) {
  const styles = useStyles();
  return (
    <div className={styles.rosterPop}>
      <div className={styles.rosterList}>
        {blueprint.roster.map((role) => (
          <div className={styles.rosterItem} key={role}>
            <span className={styles.roleDot}><BotRegular fontSize={10} /></span>
            <span>{role}</span>
          </div>
        ))}
      </div>
      <BlueprintSkillBindings bindings={blueprint.skill_bindings} />
      <BlueprintMeta blueprint={blueprint} />
    </div>
  );
}

function TemplateRow({ blueprint, selected, onSelect }: { blueprint: Blueprint; selected: boolean; onSelect: () => void }) {
  const styles = useStyles();
  const unavailable = blueprint.exportability?.status === 'unavailable';
  const workflows = workflowList(blueprint);
  const workflowLabel = workflows.length === 1 ? workflows[0] : `${workflows.length} workflows`;
  const workflowAria = workflows.length === 1 ? `Workflow: ${workflows[0]}` : `Workflows: ${workflows.join(', ')}`;
  return (
    // Tooltip portals by default, so the roster is never clipped by the panel's
    // bounded max-height / overflow. relationship="description" wires the roster
    // as aria-describedby, so it is announced on keyboard focus, not hover-only.
    <Tooltip
      relationship="description"
      withArrow
      positioning="after"
      content={<TemplateRosterList blueprint={blueprint} />}
    >
      <div
        className={mergeClasses(styles.templateRow, selected && styles.templateRowSelected)}
        onClick={unavailable ? undefined : onSelect}
        role="radio"
        aria-checked={unavailable ? false : selected}
        aria-disabled={unavailable}
        aria-label={unavailable ? `${blueprint.name} is unavailable: ${blueprint.exportability?.codes.join(', ')}` : blueprint.name}
        tabIndex={unavailable ? -1 : 0}
        onKeyDown={(event) => {
          if (!unavailable && (event.key === 'Enter' || event.key === ' ')) { event.preventDefault(); onSelect(); }
        }}
      >
        <span className={styles.rowIcon}><SparkleRegular /></span>
        <div className={styles.rowMain}>
          <span className={styles.rowTitle}>{blueprint.name}</span>
          {blueprint.description && <span className={styles.rowDesc}>{blueprint.description}</span>}
          {unavailable && <span className={styles.rowDesc}>Unavailable: {blueprint.exportability?.codes.join(', ')}</span>}
        </div>
        <div className={styles.rowTrailing}>
          {workflows.length > 0 && (
            <span className={styles.workflowPill} aria-label={workflowAria}>
              <FlowchartRegular fontSize={14} aria-hidden />
              <span className={styles.workflowName}>{workflowLabel}</span>
            </span>
          )}
          <span className={styles.agentPill} aria-label={`${blueprint.roster.length} agents`}>
            <PeopleTeamRegular fontSize={14} aria-hidden />{blueprint.roster.length}
          </span>
        </div>
      </div>
    </Tooltip>
  );
}

export function BlueprintTemplatePicker({
  blueprints,
  loading,
  error,
  value,
  onChange,
  limit,
}: {
  blueprints: Blueprint[];
  loading: boolean;
  error: string | null;
  value: BlueprintSelection;
  onChange: (selection: BlueprintSelection) => void;
  limit?: number;
}) {
  const styles = useStyles();
  // `limit` optionally caps how many templates are shown; unset, the dense row
  // list surfaces the whole catalog. The list always reflows vertically — it
  // never becomes a horizontally-scrolling strip.
  const visible = typeof limit === 'number' ? blueprints.slice(0, limit) : blueprints;

  return (
    <div className={styles.root} role="radiogroup" aria-label="Blueprint templates">
      {error && <MessageBar intent="warning"><MessageBarBody>Could not load blueprints: {error}</MessageBarBody></MessageBar>}
      {loading && <div className={styles.generateBar}><Spinner size="extra-tiny" /> <Text size={200}>Loading blueprints…</Text></div>}
      <div className={styles.templateList}>
        {visible.map((bp) => (
          <TemplateRow
            key={bp.id}
            blueprint={bp}
            selected={bp.exportability?.status !== 'unavailable' && value.kind === 'predefined' && value.blueprint.id === bp.id}
            onSelect={() => onChange({ kind: 'predefined', blueprint: bp })}
          />
        ))}
      </div>
    </div>
  );
}

export function StarterTemplatesSection({
  title,
  ...props
}: Omit<Parameters<typeof BlueprintTemplatePicker>[0], 'showNoBlueprint'> & { title?: string }) {
  const styles = useStyles();
  return (
    <div className={styles.root}>
      <div className={styles.sectionHeader}>
        <Text weight="semibold">{title ?? 'Starter templates'}</Text>
      </div>
      <BlueprintTemplatePicker {...props} />
    </div>
  );
}

export function GenerateBlueprintBox({
  description,
  onDescriptionChange,
  onGenerate,
  generating,
  error,
}: {
  description: string;
  onDescriptionChange: (value: string) => void;
  onGenerate: () => void;
  generating: boolean;
  error: string | null;
}) {
  const styles = useStyles();
  return (
    <div className={styles.generateBox}>
      <Field label="Describe what you want Agentweaver to do" hint="Agentweaver tailors a squad, a workflow, and a review policy to match.">
        <Textarea
          aria-label="Describe what you want Agentweaver to do"
          placeholder="e.g. triage inbound support tickets, research the account, and draft a reply for review"
          value={description}
          onChange={(_, data) => onDescriptionChange(data.value)}
          resize="vertical"
        />
      </Field>
      <div className={styles.generateBar}>
        <Button appearance="primary" icon={<span className={generating ? styles.buttonSparkle : undefined}><SparkleRegular /></span>} aria-label="Generate blueprint" disabled={!description.trim() || generating} onClick={onGenerate}>
          {generating ? 'Generating' : 'Generate Blueprint'}
        </Button>
      </div>
      {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
    </div>
  );
}

export function GeneratedBlueprintPane({ generated, generating }: { generated: { blueprint: Blueprint; generatedWorkflowYaml?: string | null } | null; generating?: boolean }) {
  const styles = useStyles();
  if (generating) {
    return (
      <Card className={styles.workingCard} aria-busy="true" aria-label="Generating blueprint">
        <span className={mergeClasses(styles.workingBubble, styles.sparklePulse)}><SparkleRegular fontSize={24} /></span>
        <Text weight="semibold">Designing your blueprint</Text>
        <RotatingStatus />
      </Card>
    );
  }
  if (!generated) {
    return (
      <Card className={styles.emptyCard}>
        <SparkleRegular fontSize={28} />
        <Text weight="semibold">Your generated blueprint will appear here</Text>
        <Text className={styles.subtle}>Describe what you want to accomplish and click Generate Blueprint. We'll create a custom squad and workflow tailored to your goals.</Text>
      </Card>
    );
  }
  return <BlueprintPreviewCard blueprint={generated.blueprint} generated reveal />;
}

export function SuggestedBlueprintPanel({
  active,
  repository,
  onChange,
  onViewTemplates,
  onGenerateCustom,
}: {
  active: boolean;
  repository: string;
  onChange: (selection: BlueprintSelection) => void;
  onViewTemplates: () => void;
  onGenerateCustom: () => void;
}) {
  const styles = useStyles();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [suggestion, setSuggestion] = useState<SuggestBlueprintResponse | null>(null);
  const [expanded, setExpanded] = useState(false);
  const normalizedRepo = repository.trim();

  useEffect(() => {
    if (!active || !normalizedRepo) { setSuggestion(null); return; }
    let cancelled = false;
    setLoading(true);
    setError(null);
    apiClient.suggestBlueprint(normalizedRepo)
      .then((res) => { if (!cancelled) { setSuggestion(res); setLoading(false); } })
      .catch((err: unknown) => { if (!cancelled) { setError(err instanceof Error ? err.message : String(err)); setSuggestion(null); setLoading(false); } });
    return () => { cancelled = true; };
  }, [active, normalizedRepo]);

  const recommended = suggestion?.recommended_blueprint ?? null;

  if (!normalizedRepo) {
    return (
      <div className={styles.root}>
        <Card className={styles.emptyCard}>
          <SparkleRegular fontSize={28} className={styles.emptyIcon} aria-hidden />
          <Text weight="semibold">Select a repository and Agentweaver will recommend a blueprint tailored to it.</Text>
          <Text className={styles.emptyHint}>Or choose Templates or Generate above.</Text>
        </Card>
      </div>
    );
  }

  if (loading) return <div className={styles.generateBar}><Spinner size="tiny" /><Text>Analyzing repository…</Text></div>;

  if (error || suggestion?.fallback || !recommended) {
    return (
      <div className={styles.root}>
        <MessageBar intent="warning"><MessageBarBody>{error ? `Could not analyze repo: ${error}` : suggestion?.rationale ?? 'Repository analysis unavailable. Choose a template or generate a custom blueprint instead.'}</MessageBarBody></MessageBar>
        <div className={styles.tabLinks}>
          <Button appearance="secondary" icon={<DocumentRegular />} onClick={onViewTemplates}>Browse templates</Button>
          <Button appearance="secondary" icon={<SparkleRegular />} onClick={onGenerateCustom}>Generate a custom blueprint</Button>
        </div>
      </div>
    );
  }

  const activeSuggestion = suggestion!;

  return (
    <div className={styles.root}>
      <div className={styles.suggestedHeader}>
        <Badge appearance="filled" color="success" size="small" icon={<CheckmarkRegular />}>Recommended for this repository</Badge>
      </div>
      {activeSuggestion.rationale && <Text className={styles.rowDesc}>{activeSuggestion.rationale}</Text>}
      <BlueprintPreviewCard blueprint={recommended} />
      {activeSuggestion.signals.length > 0 && (
        <div className={styles.generateBox}>
          <Button appearance="subtle" size="small" icon={expanded ? <ChevronDownRegular /> : <ChevronRightRegular />} onClick={() => setExpanded(!expanded)}>
            {expanded ? 'Hide signals' : 'Why this blueprint'}
          </Button>
          {expanded && (
            <div className={styles.roleRows}>
              {activeSuggestion.signals.map((s) => (
                <div className={styles.roleRow} key={s}><InfoRegular fontSize={14} /><Text size={200} className={styles.subtle}>{s}</Text></div>
              ))}
            </div>
          )}
        </div>
      )}
      <div className={styles.suggestedActions}>
        <Button appearance="primary" onClick={() => onChange({ kind: 'predefined', blueprint: recommended })}>Use this blueprint</Button>
      </div>
    </div>
  );
}

function BlueprintTabStrip({ tabs, value, onChange }: { tabs: BlueprintPanelTab[]; value: BlueprintPanelTab; onChange: (tab: BlueprintPanelTab) => void }) {
  const styles = useStyles();
  const labelByTab: Record<BlueprintPanelTab, string> = {
    suggested: 'Suggested',
    templates: 'Templates',
    generate: 'Generate',
  };
  const iconByTab: Record<BlueprintPanelTab, ReactElement> = {
    suggested: <SparkleRegular />,
    templates: <DocumentRegular />,
    generate: <SparkleRegular />,
  };
  return (
    <div className={styles.tabStrip}>
      {tabs.map((tab) => (
        <button
          key={tab}
          type="button"
          className={mergeClasses(styles.tabButton, value === tab && styles.tabButtonActive)}
          onClick={() => onChange(tab)}
          aria-current={value === tab ? 'page' : undefined}
        >
          <span aria-hidden="true">{iconByTab[tab]}</span>
          {labelByTab[tab]}
        </button>
      ))}
    </div>
  );
}

export function BlueprintPanel({
  active,
  tabs,
  value,
  onChange,
  targetRepository,
  generated,
  onGenerate,
  generating,
  generationError,
  generateDescription,
  onGenerateDescriptionChange,
}: {
  active: boolean;
  tabs: BlueprintPanelTab[];
  value: BlueprintSelection;
  onChange: (selection: BlueprintSelection) => void;
  targetRepository?: string | null;
  generated: { blueprint: Blueprint; generatedWorkflowYaml?: string | null } | null;
  onGenerate: () => void;
  generating: boolean;
  generationError: string | null;
  generateDescription: string;
  onGenerateDescriptionChange: (value: string) => void;
}) {
  const styles = useStyles();
  const catalog = useBlueprintCatalog(active);
  const [selectedTab, setSelectedTab] = useState<BlueprintPanelTab>(tabs[0]);

  useEffect(() => {
    if (!tabs.includes(selectedTab)) setSelectedTab(tabs[0]);
  }, [selectedTab, tabs]);

  const viewTemplates = () => setSelectedTab('templates');
  const viewGenerate = () => setSelectedTab('generate');

  return (
    <div className={styles.panel}>
      <div className={styles.panelHeader}>
        <span className={styles.panelIcon}><BotRegular /></span>
        <div>
          <Text weight="semibold" size={400}>Blueprint</Text>
          <br />
          <Text className={styles.subtle} size={200}>
            {tabs.includes('suggested')
              ? 'Scaffold your project with a prebuilt or AI-generated squad and workflow.'
              : 'Choose a starting point or generate a custom blueprint.'}
          </Text>
        </div>
      </div>
      <BlueprintTabStrip tabs={tabs} value={selectedTab} onChange={setSelectedTab} />
      <div className={styles.panelBody}>
        {selectedTab === 'suggested' && (
          <SuggestedBlueprintPanel
            active={active}
            repository={targetRepository ?? ''}
            onChange={onChange}
            onViewTemplates={viewTemplates}
            onGenerateCustom={viewGenerate}
          />
        )}
        {selectedTab === 'templates' && <StarterTemplatesSection {...catalog} value={value} onChange={onChange} />}
        {selectedTab === 'generate' && (
          <>
            <GenerateBlueprintBox
              description={generateDescription}
              onDescriptionChange={onGenerateDescriptionChange}
              onGenerate={onGenerate}
              generating={generating}
              error={generationError}
            />
            <GeneratedBlueprintPane generated={generated} generating={generating} />
          </>
        )}
      </div>
    </div>
  );
}

export function BlueprintPicker({ active, value, onChange, targetRepository }: {
  active: boolean;
  value: BlueprintSelection;
  onChange: (selection: BlueprintSelection) => void;
  targetRepository?: string | null;
}) {
  const [description, setDescription] = useState('');
  const generation = useBlueprintGeneration(onChange, targetRepository);

  return (
    <BlueprintPanel
      active={active}
      tabs={['templates', 'generate']}
      value={value}
      onChange={onChange}
      targetRepository={targetRepository}
      generated={generation.generated}
      onGenerate={() => void generation.generate(description)}
      generating={generation.generating}
      generationError={generation.error}
      generateDescription={description}
      onGenerateDescriptionChange={setDescription}
    />
  );
}

export function applyBlueprintToRequest<T extends {
  blueprint_id?: string;
  blueprint?: Blueprint;
  generated_workflow_yaml?: string | null;
}>(req: T, selection: BlueprintSelection): T {
  if (selection.kind === 'predefined') {
    req.blueprint_id = selection.blueprint.id;
  } else if (selection.kind === 'generated') {
    req.blueprint = selection.blueprint;
    req.generated_workflow_yaml = selection.generatedWorkflowYaml ?? null;
  }
  return req;
}
