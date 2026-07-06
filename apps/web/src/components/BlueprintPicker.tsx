import { useEffect, useState } from 'react';
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
  tokens,
} from '@fluentui/react-components';
import { ChevronDownRegular, ChevronRightRegular, SparkleRegular } from '@fluentui/react-icons';
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
  tabStrip: { display: 'inline-flex', gap: tokens.spacingHorizontalXS, padding: tokens.spacingVerticalXXS, backgroundColor: tokens.colorNeutralBackground3, borderRadius: tokens.borderRadiusXLarge, alignSelf: 'flex-start' },
  tabButton: { minWidth: '96px' },
  panelBody: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM, minHeight: 0, overflowY: 'auto', paddingRight: tokens.spacingHorizontalXS },
  sectionHeader: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: tokens.spacingHorizontalM },
  subtle: { color: tokens.colorNeutralForeground3 },
  emptyCard: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: tokens.spacingVerticalS, padding: tokens.spacingVerticalXXL, textAlign: 'center', backgroundColor: tokens.colorNeutralBackground2, minHeight: '160px', justifyContent: 'center', overflowWrap: 'anywhere' },
  templateGrid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(210px, 1fr))', gap: tokens.spacingHorizontalM, alignItems: 'stretch' },
  radioCard: { width: '100%', minHeight: '180px', cursor: 'pointer' },
  selectedCard: { border: `2px solid ${tokens.colorBrandStroke1}` },
  cardLabel: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS, width: '100%' },
  cardTitleRow: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: tokens.spacingHorizontalS },
  cardTitle: { fontWeight: tokens.fontWeightSemibold },
  cardDescription: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, lineHeight: tokens.lineHeightBase200 },
  chips: { display: 'flex', flexWrap: 'wrap', gap: tokens.spacingHorizontalXS, marginTop: tokens.spacingVerticalXXS },
  roleRows: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS, marginTop: tokens.spacingVerticalXS },
  roleRow: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalXS, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  iconBubble: { width: '28px', height: '28px', borderRadius: tokens.borderRadiusCircular, backgroundColor: tokens.colorBrandBackground2, display: 'inline-flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 },
  previewCard: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS, padding: tokens.spacingVerticalM },
  metaRow: { display: 'flex', flexWrap: 'wrap', gap: tokens.spacingHorizontalS, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  generateBox: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  generateBar: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS },
  suggestedCard: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS, border: `1px solid ${tokens.colorPaletteGreenBorderActive}`, boxShadow: tokens.shadow4, padding: tokens.spacingVerticalM },
  suggestedHeader: { display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: tokens.spacingHorizontalS },
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

function roleLabel(role: string) {
  return role.split('-').map(p => p.charAt(0).toUpperCase() + p.slice(1)).join(' ');
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
  return (
    <Card
      className={`${styles.radioCard} ${selected ? styles.selectedCard : ''}`}
      onClick={onSelect}
      role="radio"
      aria-checked={selected}
      tabIndex={0}
      onKeyDown={(event) => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); onSelect(); } }}
    >
      <div className={styles.cardLabel}>
        <div className={styles.cardTitleRow}>
          <span className={styles.iconBubble}>✦</span>
          <Text className={styles.cardTitle}>{blueprint.name}</Text>
          <Badge appearance="outline" size="small">{blueprint.roster.length} agents</Badge>
        </div>
        <Text className={styles.cardDescription}>{blueprint.description}</Text>
        <div className={styles.roleRows}>
          {visibleRoles.map((role) => (
            <div className={styles.roleRow} key={role}><span>●</span><span>{roleLabel(role)}</span></div>
          ))}
        </div>
        <BlueprintRosterChips roster={blueprint.roster} limit={3} />
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
        <Button appearance="transparent" size="small" onClick={onViewAllTemplates}>View all templates →</Button>
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
        <Button appearance="secondary" icon={<SparkleRegular />} disabled={!description.trim() || generating} onClick={onGenerate}>
          {generating ? 'Generating' : 'Generate blueprint'}
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
        <Text className={styles.subtle}>Describe what you want Agentweaver to accomplish, then click Generate Blueprint.</Text>
      </Card>
    );
  }
  return <BlueprintPreviewCard blueprint={generated.blueprint} generated />;
}

export function SuggestedBlueprintPanel({
  active,
  repository,
  blueprints,
  value,
  onChange,
  onViewTemplates,
}: {
  active: boolean;
  repository: string;
  blueprints: Blueprint[];
  value: BlueprintSelection;
  onChange: (selection: BlueprintSelection) => void;
  onViewTemplates: () => void;
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
    return <Card className={styles.emptyCard}><Text weight="semibold">Select a repository first</Text><Text className={styles.subtle}>Agentweaver will analyze it and suggest a matching blueprint.</Text></Card>;
  }

  if (loading) return <div className={styles.generateBar}><Spinner size="tiny" /><Text>Analyzing repository…</Text></div>;

  if (error || suggestion?.fallback || !recommended) {
    return (
      <div className={styles.root}>
        <MessageBar intent="warning"><MessageBarBody>{error ? `Could not analyze repo: ${error}` : suggestion?.rationale ?? 'Repository analysis unavailable. Choose a template instead.'}</MessageBarBody></MessageBar>
        <Button appearance="secondary" onClick={onViewTemplates}>View all templates →</Button>
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
              <span className={styles.iconBubble}>✦</span>
              <Text className={styles.cardTitle}>{recommended.name}</Text>
              <Badge appearance="filled" color="success" size="small">Recommended</Badge>
            </div>
            <Text className={styles.cardDescription}>{activeSuggestion.rationale || recommended.description}</Text>
            <BlueprintRosterChips roster={recommended.roster} limit={5} />
            <Text size={200} className={styles.subtle}>{recommended.roster.length} agents · confidence {Math.round(activeSuggestion.confidence * 100)}%</Text>
          </div>
          <Button appearance="subtle" icon={expanded ? <ChevronDownRegular /> : <ChevronRightRegular />} onClick={() => setExpanded(!expanded)} aria-label="Toggle suggestion details" />
        </div>
        {expanded && activeSuggestion.signals.length > 0 && (
          <div className={styles.roleRows}>{activeSuggestion.signals.map((s) => <Text key={s} size={200} className={styles.subtle}>• {s}</Text>)}</div>
        )}
        <Button appearance="primary" onClick={() => onChange({ kind: 'predefined', blueprint: recommended })}>Use this blueprint</Button>
      </Card>
      <Button appearance="transparent" onClick={onViewTemplates}>View all templates →</Button>
      {blueprints.length === 0 && value.kind !== 'predefined' ? null : null}
    </div>
  );
}

function BlueprintTabStrip({ tabs, value, onChange }: { tabs: BlueprintPanelTab[]; value: BlueprintPanelTab; onChange: (tab: BlueprintPanelTab) => void }) {
  const styles = useStyles();
  return (
    <div className={styles.tabStrip}>
      {tabs.map((tab) => (
        <Button key={tab} className={styles.tabButton} size="small" appearance={value === tab ? 'primary' : 'subtle'} onClick={() => onChange(tab)}>
          {tab.charAt(0).toUpperCase() + tab.slice(1)}
        </Button>
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
  const showStarterTemplates = selectedTab === 'generated' && tabs.includes('generated');

  return (
    <div className={styles.panel}>
      <Text weight="semibold">Blueprint</Text>
      <BlueprintTabStrip tabs={tabs} value={selectedTab} onChange={setSelectedTab} />
      <div className={styles.panelBody}>
        {selectedTab === 'generated' && <GeneratedBlueprintPane generated={generated} />}
        {selectedTab === 'suggested' && (
          <SuggestedBlueprintPanel
            active={active}
            repository={targetRepository ?? ''}
            blueprints={catalog.blueprints}
            value={value}
            onChange={onChange}
            onViewTemplates={viewTemplates}
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
        {showStarterTemplates && (
          <StarterTemplatesSection {...catalog} value={value} onChange={onChange} limit={4} onViewAllTemplates={viewTemplates} />
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
