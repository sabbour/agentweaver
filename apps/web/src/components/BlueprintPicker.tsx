import { useEffect, useState } from 'react';
import {
  Badge,
  Button,
  Card,
  Divider,
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
import { SparkleRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { normalizeBlueprintList } from '../api/client';
import type { Blueprint } from '../api/types';

// The blueprint a user has chosen to apply when creating a project. `none` keeps
// project creation unchanged; `predefined` sends a blueprint_id; `generated` sends
// the inline blueprint. Blueprints roster only existing catalog roles.
export type BlueprintSelection =
  | { kind: 'none' }
  | { kind: 'predefined'; blueprint: Blueprint }
  | { kind: 'generated'; blueprint: Blueprint; generatedWorkflowYaml?: string | null };

export const NO_BLUEPRINT: BlueprintSelection = { kind: 'none' };

const GENERATED_KEY = '__generated__';
const NONE_KEY = '__none__';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  sectionLabel: {
    fontWeight: tokens.fontWeightSemibold,
  },
  radioRow: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  bpName: {
    fontWeight: tokens.fontWeightSemibold,
  },
  bpDesc: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  chips: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalXS,
    marginTop: tokens.spacingVerticalXXS,
  },
  describeRow: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  generateBar: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  previewCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: tokens.spacingVerticalM,
  },
  metaRow: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalS,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
});

function RosterChips({ roster }: { roster: string[] }) {
  const styles = useStyles();
  if (roster.length === 0) return null;
  return (
    <div className={styles.chips}>
      {roster.map((role) => (
        <Badge key={role} appearance="tint" color="brand" size="small">
          {role}
        </Badge>
      ))}
    </div>
  );
}

interface BlueprintPickerProps {
  // Mount/load trigger — the parent dialog passes its open state so the catalog
  // loads when the dialog opens.
  active: boolean;
  value: BlueprintSelection;
  onChange: (selection: BlueprintSelection) => void;
}

export function BlueprintPicker({ active, value, onChange }: BlueprintPickerProps) {
  const styles = useStyles();

  const [blueprints, setBlueprints] = useState<Blueprint[]>([]);
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);

  const [description, setDescription] = useState('');
  const [generating, setGenerating] = useState(false);
  const [genError, setGenError] = useState<string | null>(null);
  const [generated, setGenerated] = useState<{ blueprint: Blueprint; generatedWorkflowYaml?: string | null } | null>(null);

  // Load the predefined blueprint catalog when the picker becomes active.
  useEffect(() => {
    if (!active) return;
    let cancelled = false;
    setLoading(true);
    setLoadError(null);
    apiClient
      .listBlueprints()
      .then((list) => {
        if (cancelled) return;
        setBlueprints(normalizeBlueprintList(list));
        setLoading(false);
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        setLoadError(err instanceof Error ? err.message : String(err));
        setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [active]);

  const selectedKey =
    value.kind === 'none'
      ? NONE_KEY
      : value.kind === 'generated'
        ? GENERATED_KEY
        : value.blueprint.id;

  const handleRadio = (key: string) => {
    if (key === NONE_KEY) {
      onChange(NO_BLUEPRINT);
      return;
    }
    if (key === GENERATED_KEY) {
      if (generated) onChange({ kind: 'generated', blueprint: generated.blueprint, generatedWorkflowYaml: generated.generatedWorkflowYaml });
      return;
    }
    const bp = blueprints.find((b) => b.id === key);
    if (bp) onChange({ kind: 'predefined', blueprint: bp });
  };

  const handleGenerate = async () => {
    if (!description.trim()) return;
    setGenerating(true);
    setGenError(null);
    try {
      const res = await apiClient.generateBlueprint(description.trim());
      setGenerated({ blueprint: res.blueprint, generatedWorkflowYaml: res.generated_workflow_yaml });
      // Auto-apply the freshly generated blueprint.
      onChange({ kind: 'generated', blueprint: res.blueprint, generatedWorkflowYaml: res.generated_workflow_yaml });
    } catch (err) {
      setGenError(err instanceof Error ? err.message : String(err));
    } finally {
      setGenerating(false);
    }
  };

  return (
    <div className={styles.root}>
      <Text className={styles.sectionLabel}>Blueprint (optional)</Text>

      {loadError && (
        <MessageBar intent="warning">
          <MessageBarBody>Could not load blueprints: {loadError}</MessageBarBody>
        </MessageBar>
      )}

      <RadioGroup
        aria-label="Blueprint"
        value={selectedKey}
        onChange={(_, data) => handleRadio(data.value)}
      >
        <Radio value={NONE_KEY} label="No blueprint" />

        {loading && (
          <div className={styles.generateBar}>
            <Spinner size="extra-tiny" /> <Text size={200}>Loading blueprints…</Text>
          </div>
        )}

        {blueprints.map((bp) => (
          <Radio
            key={bp.id}
            value={bp.id}
            label={
              <div className={styles.radioRow}>
                <Text className={styles.bpName}>{bp.name}</Text>
                <Text className={styles.bpDesc}>{bp.description}</Text>
                <RosterChips roster={bp.roster} />
              </div>
            }
          />
        ))}

        {generated && (
          <Radio
            value={GENERATED_KEY}
            label={
              <div className={styles.radioRow}>
                <Text className={styles.bpName}>{generated.blueprint.name}</Text>
                <Badge appearance="tint" color="success" size="small">generated</Badge>
              </div>
            }
          />
        )}
      </RadioGroup>

      <Divider />

      <div className={styles.describeRow}>
        <Text className={styles.sectionLabel}>Or describe the work Agentweaver should run</Text>
        <Text className={styles.bpDesc}>
          Generated blueprints configure Agentweaver agents, workflow, review policy, and sandbox posture
          for operating the use-case; they are not software-product specs unless you explicitly ask to build software.
        </Text>
        <Textarea
          aria-label="Describe your project"
          placeholder="e.g. handle job searches: research roles, triage postings, draft outreach, track follow-ups"
          value={description}
          onChange={(_, data) => setDescription(data.value)}
          resize="vertical"
        />
        <div className={styles.generateBar}>
          <Button
            appearance="secondary"
            icon={<SparkleRegular />}
            disabled={!description.trim() || generating}
            onClick={() => void handleGenerate()}
          >
            {generating ? 'Generating' : 'Generate blueprint'}
          </Button>
          {generating && <Spinner size="extra-tiny" aria-hidden="true" />}
        </div>
        {genError && (
          <MessageBar intent="error">
            <MessageBarBody>{genError}</MessageBarBody>
          </MessageBar>
        )}
      </div>

      {generated && (
        <Card className={styles.previewCard} aria-label="Generated blueprint preview">
          <Text className={styles.bpName}>{generated.blueprint.name}</Text>
          {generated.blueprint.description && (
            <Text className={styles.bpDesc}>{generated.blueprint.description}</Text>
          )}
          <RosterChips roster={generated.blueprint.roster} />
          <div className={styles.metaRow}>
            <span>Workflow: {generated.blueprint.workflow}</span>
            <span>Review: {generated.blueprint.review_policy}</span>
            <span>Sandbox: {generated.blueprint.sandbox_profile}</span>
          </div>
        </Card>
      )}
    </div>
  );
}

// Maps a blueprint selection onto the create-project request fields.
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
