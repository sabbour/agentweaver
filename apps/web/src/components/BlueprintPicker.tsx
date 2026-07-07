import { type ReactElement, useEffect, useRef, useState } from 'react';
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
  InfoRegular,
  PeopleTeamRegular,
  SparkleRegular,
} from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { normalizeBlueprintList } from '../api/client';
import type { Blueprint, SuggestBlueprintResponse } from '../api/types';

export type BlueprintSelection =
  | { kind: 'none' }
  | { kind: 'predefined'; blueprint: Blueprint }
  | { kind: 'generated'; blueprint: Blueprint; generatedWorkflowYaml?: string | null };

export const NO_BLUEPRINT: BlueprintSelection = { kind: 'none' };

export type BlueprintPanelTab = 'generated' | 'suggested' | 'templates' | 'generate';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM, minHeight: 0 },
  panel: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM, minHeight: 0, height: '100%' },
  panelHeader: { display: 'flex', alignItems: 'flex-start', gap: tokens.spacingHorizontalM },
  panelIcon: { width: '36px', height: '36px', borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorBrandBackground2, color: tokens.colorBrandForeground1, display: 'inline-flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 },
  tabStrip: { display: 'flex', width: '100%', borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, gap: tokens.spacingHorizontalL },
  tabButton: { appearance: 'none', border: 0, borderBottom: '2px solid transparent', backgroundColor: 'transparent', color: tokens.colorNeutralForeground3, cursor: 'pointer', padding: `${tokens.spacingVerticalS} 0`, marginBottom: '-1px', fontWeight: tokens.fontWeightSemibold, display: 'inline-flex', alignItems: 'center', gap: tokens.spacingHorizontalXXS },
  tabButtonActive: { color: tokens.colorBrandForeground1, borderBottomColor: tokens.colorBrandStroke1 },
  panelBody: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalL, minHeight: 0, overflowY: 'auto', paddingRight: tokens.spacingHorizontalXS },
  sectionHeader: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: tokens.spacingHorizontalM },
  subtle: { color: tokens.colorNeutralForeground3 },
  emptyCard: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: tokens.spacingVerticalXS, padding: tokens.spacingVerticalXL, textAlign: 'center', backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke2}`, minHeight: '140px', justifyContent: 'center', overflowWrap: 'anywhere' },
  tabLinks: { display: 'flex', flexWrap: 'wrap', gap: tokens.spacingHorizontalS },
  // Always reflows (auto-fit columns down to 1 on narrow containers) instead of
  // scrolling horizontally — a fixed-width dialog column should never need an
  // inner horizontal scrollbar to see the rest of the templates.
  templateGrid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(190px, 1fr))', gap: tokens.spacingHorizontalM, alignItems: 'stretch' },
  radioCard: { width: '100%', minWidth: '180px', minHeight: '220px', cursor: 'pointer', padding: tokens.spacingVerticalM },
  selectedCard: { border: `2px solid ${tokens.colorBrandStroke1}` },
  cardLabel: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS, width: '100%' },
  cardTitleRow: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: tokens.spacingHorizontalS },
  cardTitle: { fontWeight: tokens.fontWeightSemibold },
  cardDescription: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, lineHeight: tokens.lineHeightBase200 },
  chips: { display: 'flex', flexWrap: 'wrap', gap: tokens.spacingHorizontalXS, marginTop: tokens.spacingVerticalXXS },
  roleRows: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS, marginTop: tokens.spacingVerticalXS },
  roleRow: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalXS, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  roleDot: { width: '14px', height: '14px', borderRadius: tokens.borderRadiusSmall, backgroundColor: tokens.colorPalettePurpleBackground2, color: tokens.colorPalettePurpleForeground2, display: 'inline-flex', alignItems: 'center', justifyContent: 'center', fontSize: '9px', flexShrink: 0 },
  iconBubble: { width: '32px', height: '32px', borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorBrandBackground2, color: tokens.colorBrandForeground1, display: 'inline-flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 },
  inlineMeta: { display: 'inline-flex', alignItems: 'center', gap: tokens.spacingHorizontalXXS },
  previewCard: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS, padding: tokens.spacingVerticalM },
  metaRow: { display: 'flex', flexWrap: 'wrap', gap: tokens.spacingHorizontalS, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  generateBox: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  generateBar: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS },
  suggestedCard: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM, border: `1px solid ${tokens.colorPaletteGreenBorderActive}`, boxShadow: tokens.shadow4, padding: tokens.spacingVerticalL },
  suggestedHeader: { display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: tokens.spacingHorizontalS },
  suggestedFooter: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: tokens.spacingHorizontalM, borderTop: `1px solid ${tokens.colorNeutralStroke2}`, paddingTop: tokens.spacingVerticalM },
});

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
      {visible.map((role) => <Badge key={role} appearance="tint" color="brand" size="small">{role}</Badge>)}
      {remaining > 0 && <Badge appearance="outline" size="small">+{remaining}</Badge>}
    </div>
  );
}

export function BlueprintPreviewCard({ blueprint, generated }: { blueprint: Blueprint; generated?: boolean }) {
  const styles = useStyles();
  return (
    <Card className={styles.previewCard} aria-label={generated ? 'Generated blueprint preview' : `${blueprint.name} blueprint preview`}>
      <div className={styles.cardTitleRow}>
        <Text className={styles.cardTitle}>{blueprint.name}</Text>
        {generated && <Badge appearance="tint" color="success" size="small">Generated</Badge>}
      </div>
      {blueprint.description && <Text className={styles.cardDescription}>{blueprint.description}</Text>}
      <BlueprintRosterChips roster={blueprint.roster} />
      <div className={styles.metaRow}>
        <span>{blueprint.roster.length} agents</span>
        <span>Workflow: {blueprint.workflow}</span>
        <span>Review: {blueprint.review_policy}</span>
      </div>
    </Card>
  );
}

function BlueprintCard({ blueprint, selected, onSelect }: { blueprint: Blueprint; selected: boolean; onSelect: () => void }) {
  const styles = useStyles();
  const visibleRoles = blueprint.roster.slice(0, 3);
  const remaining = Math.max(0, blueprint.roster.length - visibleRoles.length);
  return (
    <Card
      className={mergeClasses(styles.radioCard, selected && styles.selectedCard)}
      onClick={onSelect}
      role="radio"
      aria-checked={selected}
      tabIndex={0}
      onKeyDown={(event) => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); onSelect(); } }}
    >
      <div className={styles.cardLabel}>
        <span className={styles.iconBubble}><SparkleRegular /></span>
        <div>
          <Text className={styles.cardTitle}>{blueprint.name}</Text>
          <br />
          <Text className={styles.subtle} size={200}>{blueprint.roster.length} agents</Text>
        </div>
        <Text className={styles.cardDescription}>{blueprint.description}</Text>
        <div className={styles.roleRows}>
          {visibleRoles.map((role) => (
            <div className={styles.roleRow} key={role}><span className={styles.roleDot}><BotRegular fontSize={10} /></span><span>{role}</span></div>
          ))}
          {remaining > 0 && <Badge appearance="outline" size="small">+{remaining}</Badge>}
        </div>
      </div>
    </Card>
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
  // `limit` only caps how many templates are shown (the rest are one click away
  // via "View all templates"); it never changes the layout algorithm — this grid
  // always reflows, it never becomes a horizontally-scrolling row.
  const visible = typeof limit === 'number' ? blueprints.slice(0, limit) : blueprints;

  return (
    <div className={styles.root} role="radiogroup" aria-label="Blueprint templates">
      {error && <MessageBar intent="warning"><MessageBarBody>Could not load blueprints: {error}</MessageBarBody></MessageBar>}
      {loading && <div className={styles.generateBar}><Spinner size="extra-tiny" /> <Text size={200}>Loading blueprints…</Text></div>}
      <div className={styles.templateGrid}>
        {visible.map((bp) => (
          <BlueprintCard
            key={bp.id}
            blueprint={bp}
            selected={value.kind === 'predefined' && value.blueprint.id === bp.id}
            onSelect={() => onChange({ kind: 'predefined', blueprint: bp })}
          />
        ))}
      </div>
    </div>
  );
}

export function StarterTemplatesSection({
  title,
  onViewAllTemplates,
  ...props
}: Omit<Parameters<typeof BlueprintTemplatePicker>[0], 'showNoBlueprint'> & { title?: string; onViewAllTemplates?: () => void }) {
  const styles = useStyles();
  return (
    <div className={styles.root}>
      <div className={styles.sectionHeader}>
        <Text weight="semibold">{title ?? 'Starter templates'}</Text>
        {onViewAllTemplates && <Button appearance="transparent" size="small" onClick={onViewAllTemplates}>View all templates →</Button>}
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
      <Field label="Describe what Agentweaver should do">
        <Textarea
          aria-label="Describe your project"
          placeholder="e.g. handle job searches: research roles, triage postings, draft outreach, track follow-ups"
          value={description}
          onChange={(_, data) => onDescriptionChange(data.value)}
          resize="vertical"
        />
      </Field>
      <div className={styles.generateBar}>
        <Button appearance="primary" icon={<SparkleRegular />} aria-label="Generate blueprint" disabled={!description.trim() || generating} onClick={onGenerate}>
          {generating ? 'Generating' : 'Generate Blueprint'}
        </Button>
        {generating && <Spinner size="extra-tiny" aria-hidden="true" />}
      </div>
      {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
    </div>
  );
}

export function GeneratedBlueprintPane({ generated }: { generated: { blueprint: Blueprint; generatedWorkflowYaml?: string | null } | null }) {
  const styles = useStyles();
  if (!generated) {
    return (
      <Card className={styles.emptyCard}>
        <SparkleRegular fontSize={28} />
        <Text weight="semibold">Your generated blueprint will appear here</Text>
        <Text className={styles.subtle}>Describe what you want to accomplish and click Generate Blueprint. We'll create a custom squad and workflow tailored to your goals.</Text>
      </Card>
    );
  }
  return <BlueprintPreviewCard blueprint={generated.blueprint} generated />;
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
        <MessageBar intent="info"><MessageBarBody>Select a repository and Agentweaver will recommend a blueprint tailored to it.</MessageBarBody></MessageBar>
        <div className={styles.tabLinks}>
          <Button appearance="transparent" size="small" icon={<DocumentRegular />} onClick={onViewTemplates}>Browse templates</Button>
          <Button appearance="transparent" size="small" icon={<SparkleRegular />} onClick={onGenerateCustom}>Generate a custom blueprint</Button>
        </div>
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
      <Card className={styles.suggestedCard}>
        <div className={styles.suggestedHeader}>
          <div className={styles.cardLabel}>
            <div className={styles.cardTitleRow}>
              <span className={styles.iconBubble}><SparkleRegular /></span>
              <Text className={styles.cardTitle}>{recommended.name}</Text>
              <Badge appearance="filled" color="success" size="small" icon={<CheckmarkRegular />}>Recommended</Badge>
            </div>
            <Text className={styles.cardDescription}>{activeSuggestion.rationale || recommended.description}</Text>
            <BlueprintRosterChips roster={recommended.roster} limit={5} />
          </div>
          <Button appearance="subtle" icon={expanded ? <ChevronDownRegular /> : <ChevronRightRegular />} onClick={() => setExpanded(!expanded)} aria-label="Toggle suggestion details" />
        </div>
        {expanded && activeSuggestion.signals.length > 0 && (
          <div className={styles.roleRows}>{activeSuggestion.signals.map((s) => (
            <div className={styles.roleRow} key={s}><InfoRegular fontSize={14} /><Text size={200} className={styles.subtle}>{s}</Text></div>
          ))}</div>
        )}
        <div className={styles.suggestedFooter}>
          <Text className={styles.subtle}>
            <span className={styles.inlineMeta}><PeopleTeamRegular /><span>{recommended.roster.length} agents</span></span>
          </Text>
          <Button appearance="primary" onClick={() => onChange({ kind: 'predefined', blueprint: recommended })}>Use this blueprint</Button>
        </div>
      </Card>
    </div>
  );
}

function BlueprintTabStrip({ tabs, value, onChange }: { tabs: BlueprintPanelTab[]; value: BlueprintPanelTab; onChange: (tab: BlueprintPanelTab) => void }) {
  const styles = useStyles();
  const labelByTab: Record<BlueprintPanelTab, string> = {
    generated: 'Generated',
    suggested: 'Suggested',
    templates: 'Templates',
    generate: 'Generate',
  };
  const iconByTab: Record<BlueprintPanelTab, ReactElement> = {
    generated: <SparkleRegular />,
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

  // When a blueprint is generated, surface it: if this panel has a dedicated
  // "generated" tab (the blank-project dialog), jump to it so the fresh preview
  // isn't hidden behind whatever tab the user was browsing.
  const lastGeneratedId = useRef<string | null>(null);
  useEffect(() => {
    const id = generated?.blueprint.id ?? null;
    if (id && id !== lastGeneratedId.current && tabs.includes('generated')) {
      setSelectedTab('generated');
    }
    lastGeneratedId.current = id;
  }, [generated, tabs]);

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
        {selectedTab === 'generated' && <GeneratedBlueprintPane generated={generated} />}
        {selectedTab === 'suggested' && (
          <SuggestedBlueprintPanel
            active={active}
            repository={targetRepository ?? ''}
            onChange={onChange}
            onViewTemplates={viewTemplates}
            onGenerateCustom={viewGenerate}
          />
        )}
        {selectedTab === 'templates' && <StarterTemplatesSection {...catalog} value={value} onChange={onChange} onViewAllTemplates={viewTemplates} />}
        {selectedTab === 'generate' && (
          <>
            <GenerateBlueprintBox
              description={generateDescription}
              onDescriptionChange={onGenerateDescriptionChange}
              onGenerate={onGenerate}
              generating={generating}
              error={generationError}
            />
            {generated && <GeneratedBlueprintPane generated={generated} />}
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
