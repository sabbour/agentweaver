import { useEffect, useMemo, useState } from 'react';
import {
  Badge,
  Button,
  Card,
  Field,
  MessageBar,
  MessageBarBody,
  Radio,
  RadioGroup,
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

const NONE_KEY = '__none__';
const GENERATED_KEY = '__generated__';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  tabs: { display: 'inline-flex', gap: tokens.spacingHorizontalXS, padding: tokens.spacingVerticalXXS, backgroundColor: tokens.colorNeutralBackground3, borderRadius: tokens.borderRadiusXLarge },
  tabButton: { minWidth: '96px' },
  sectionHeader: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: tokens.spacingHorizontalM },
  subtle: { color: tokens.colorNeutralForeground3 },
  emptyCard: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: tokens.spacingVerticalS, padding: tokens.spacingVerticalXXL, textAlign: 'center', backgroundColor: tokens.colorNeutralBackground2 },
  templateGrid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: tokens.spacingHorizontalM },
  radioCard: { width: '100%' },
  cardLabel: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS, width: '100%' },
  cardTitleRow: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: tokens.spacingHorizontalS },
  cardTitle: { fontWeight: tokens.fontWeightSemibold },
  cardDescription: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  chips: { display: 'flex', flexWrap: 'wrap', gap: tokens.spacingHorizontalXS, marginTop: tokens.spacingVerticalXXS },
  roleRows: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS, marginTop: tokens.spacingVerticalXS },
  roleRow: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalXS, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  iconBubble: { width: '28px', height: '28px', borderRadius: tokens.borderRadiusCircular, backgroundColor: tokens.colorBrandBackground2, display: 'inline-flex', alignItems: 'center', justifyContent: 'center' },
  previewCard: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS, padding: tokens.spacingVerticalM },
  metaRow: { display: 'flex', flexWrap: 'wrap', gap: tokens.spacingHorizontalS, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  generateBox: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  generateBar: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS },
  suggestedCard: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS, border: `1px solid ${tokens.colorPaletteGreenBorderActive}`, boxShadow: tokens.shadow4, padding: tokens.spacingVerticalM },
  suggestedHeader: { display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: tokens.spacingHorizontalS },
  compactList: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  compactCard: { padding: tokens.spacingVerticalS },
  customRow: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: tokens.spacingHorizontalM, padding: tokens.spacingVerticalM, border: `1px dashed ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium },
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

function BlueprintRadioCard({ blueprint }: { blueprint: Blueprint }) {
  const styles = useStyles();
  const visibleRoles = blueprint.roster.slice(0, 3);
  return (
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
  );
}

export function BlueprintTemplatePicker({
  blueprints,
  loading,
  error,
  value,
  onChange,
  limit,
  showNoBlueprint = true,
}: {
  blueprints: Blueprint[];
  loading: boolean;
  error: string | null;
  value: BlueprintSelection;
  onChange: (selection: BlueprintSelection) => void;
  limit?: number;
  showNoBlueprint?: boolean;
}) {
  const styles = useStyles();
  const visible = typeof limit === 'number' ? blueprints.slice(0, limit) : blueprints;
  const selectedKey = value.kind === 'none' ? NONE_KEY : value.kind === 'generated' ? GENERATED_KEY : value.blueprint.id;

  const handleRadio = (key: string) => {
    if (key === NONE_KEY) return onChange(NO_BLUEPRINT);
    const bp = blueprints.find((b) => b.id === key);
    if (bp) onChange({ kind: 'predefined', blueprint: bp });
  };

  return (
    <div className={styles.root}>
      {error && <MessageBar intent="warning"><MessageBarBody>Could not load blueprints: {error}</MessageBarBody></MessageBar>}
      {loading && <div className={styles.generateBar}><Spinner size="extra-tiny" /> <Text size={200}>Loading blueprints…</Text></div>}
      <RadioGroup aria-label="Blueprint" value={selectedKey} onChange={(_, data) => handleRadio(data.value)}>
        {showNoBlueprint && <Radio value={NONE_KEY} label="No blueprint" />}
        <div className={styles.templateGrid}>
          {visible.map((bp) => (
            <Card key={bp.id} className={styles.radioCard}>
              <Radio value={bp.id} label={<BlueprintRadioCard blueprint={bp} />} />
            </Card>
          ))}
        </div>
      </RadioGroup>
    </div>
  );
}

export function StarterTemplatesSection(props: Omit<Parameters<typeof BlueprintTemplatePicker>[0], 'showNoBlueprint'> & { title?: string }) {
  const styles = useStyles();
  return (
    <div className={styles.root}>
      <div className={styles.sectionHeader}>
        <Text weight="semibold">{props.title ?? 'Starter templates'}</Text>
        <Button appearance="transparent" size="small">View all templates →</Button>
      </div>
      <BlueprintTemplatePicker {...props} showNoBlueprint={false} />
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
  onGenerateClick,
}: {
  active: boolean;
  repository: string;
  blueprints: Blueprint[];
  value: BlueprintSelection;
  onChange: (selection: BlueprintSelection) => void;
  onGenerateClick: () => void;
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
  const otherBlueprints = useMemo(() => {
    const recId = recommended?.id;
    return blueprints.filter((bp) => bp.id !== recId).slice(0, 3);
  }, [blueprints, recommended]);

  if (!normalizedRepo) {
    return <Card className={styles.emptyCard}><Text weight="semibold">Select a repository first</Text><Text className={styles.subtle}>Agentweaver will analyze it and suggest a matching blueprint.</Text></Card>;
  }

  if (loading) return <div className={styles.generateBar}><Spinner size="tiny" /><Text>Analyzing repository…</Text></div>;

  if (error || suggestion?.fallback || !recommended) {
    return (
      <div className={styles.root}>
        <MessageBar intent="warning"><MessageBarBody>{error ? `Could not analyze repo: ${error}` : suggestion?.rationale ?? 'Repository analysis unavailable. Choose a template instead.'}</MessageBarBody></MessageBar>
        <StarterTemplatesSection blueprints={blueprints} loading={false} error={null} value={value} onChange={onChange} limit={3} title="Templates" />
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

      <div className={styles.sectionHeader}>
        <Text weight="semibold">Other blueprints</Text>
        <Button appearance="transparent" size="small">View all templates →</Button>
      </div>
      <div className={styles.compactList}>
        {otherBlueprints.map((bp) => (
          <Card key={bp.id} className={styles.compactCard}>
            <div className={styles.cardTitleRow}>
              <div className={styles.cardLabel}>
                <Text className={styles.cardTitle}>{bp.name}</Text>
                <Text className={styles.cardDescription}>{bp.description}</Text>
              </div>
              <Button appearance={value.kind === 'predefined' && value.blueprint.id === bp.id ? 'primary' : 'secondary'} onClick={() => onChange({ kind: 'predefined', blueprint: bp })}>Use</Button>
            </div>
          </Card>
        ))}
      </div>
      <div className={styles.customRow}>
        <div>
          <Text weight="semibold">Custom blueprint</Text><br />
          <Text className={styles.subtle}>Describe what you want to build and we'll generate it for you.</Text>
        </div>
        <Button appearance="secondary" icon={<SparkleRegular />} onClick={onGenerateClick}>Generate</Button>
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
  const styles = useStyles();
  const catalog = useBlueprintCatalog(active);
  const [tab, setTab] = useState<'templates' | 'generate'>('templates');
  const [description, setDescription] = useState('');
  const generation = useBlueprintGeneration(onChange, targetRepository);

  return (
    <div className={styles.root}>
      <div className={styles.tabs}>
        <Button className={styles.tabButton} size="small" appearance={tab === 'templates' ? 'primary' : 'subtle'} onClick={() => setTab('templates')}>Templates</Button>
        <Button className={styles.tabButton} size="small" appearance={tab === 'generate' ? 'primary' : 'subtle'} onClick={() => setTab('generate')}>Generate</Button>
      </div>
      {tab === 'templates' ? (
        <BlueprintTemplatePicker {...catalog} value={value} onChange={onChange} />
      ) : (
        <>
          <GenerateBlueprintBox
            description={description}
            onDescriptionChange={setDescription}
            onGenerate={() => void generation.generate(description)}
            generating={generation.generating}
            error={generation.error}
          />
          {generation.generated && <BlueprintPreviewCard blueprint={generation.generated.blueprint} generated />}
        </>
      )}
    </div>
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
