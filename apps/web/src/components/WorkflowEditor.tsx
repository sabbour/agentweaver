import {
  apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import {
  Button,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Text,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular, WarningRegular } from '@fluentui/react-icons';
import { PageHeader } from './ui';
import { useCallback, useEffect, useRef, useState } from 'react';
import type { WorkflowDetailDto } from '../api/types';
// US7 — YAML workflow editor. Presents a workflow as an editable YAML document with:
// - save (PUT /api/projects/{id}/workflows/{workflowId}) with server-side validation feedback
// - discard (revert to last saved state)
// - close with unsaved-changes guard
// - header showing the workflow id + name extracted from the current YAML

export interface WorkflowEditorProps {
  projectId: string;
  /** The workflow id used for the route.  For new workflows pass the id from the blank template. */
  workflowId: string;
  initialYaml: string;
  onSave?: (workflow: WorkflowDetailDto) => void;
  onClose?: () => void;
}

const BLANK_TEMPLATE = `id: my-workflow
name: My Workflow
description: Describe what this workflow does and when to use it.
version: "1.0"

start: agent

nodes:
  - id: agent
    type: prompt
    label: Agent
    agent: lead

  - id: done
    type: terminal
    label: Done

edges:
  - from: agent
    to: done
`;

/** Re-export so WorkflowsPage can use the blank template directly. */
export { BLANK_TEMPLATE };

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingHorizontalL,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusLarge,
    minHeight: '480px',
  },
  textarea: {
    flexGrow: 1,
    width: '100%',
    minHeight: '360px',
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase300,
    padding: tokens.spacingVerticalS,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusSmall,
    backgroundColor: tokens.colorNeutralBackground2,
    color: tokens.colorNeutralForeground1,
    resize: 'vertical',
    outline: 'none',
    boxSizing: 'border-box',
  },
  footer: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  footerActions: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    marginLeft: 'auto',
  },
  statusWarning: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    color: tokens.colorStatusWarningForeground1,
    fontSize: tokens.fontSizeBase200,
  },
});

/** Extracts a top-level scalar field value from YAML text using a simple line-pattern match.
 *  Handles quoted and unquoted values. Returns null when the field is absent. */
function extractYamlScalar(yaml: string, field: string): string | null {
  const m = yaml.match(new RegExp(`^${field}:\\s*(.+)$`, 'm'));
  if (!m) return null;
  return m[1].trim().replace(/^['"]|['"]$/g, '');
}

/** Parses the 400-error body from the API, which may be JSON { error, line? }. */
function parseApiError400(err: unknown): { message: string; line: number | null } {
  if (!(err instanceof ApiError) || err.status !== 400) {
    const msg = err instanceof Error ? err.message : String(err);
    return { message: msg, line: null };
  }
  try {
    const parsed = JSON.parse(err.body) as { error?: string; line?: number | null };
    return {
      message: parsed.error ?? err.body,
      line: parsed.line ?? null,
    };
  } catch {
    return { message: err.body, line: null };
  }
}

export function WorkflowEditor({ projectId, workflowId, initialYaml, onSave, onClose }: WorkflowEditorProps) {
  const styles = useStyles();

  const [yaml, setYaml] = useState(initialYaml);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<{ message: string; line: number | null } | null>(null);
  const isDirty = yaml !== initialYaml;

  // Keep a mutable ref so the beforeunload handler always sees the latest dirty state.
  const isDirtyRef = useRef(isDirty);
  useEffect(() => { isDirtyRef.current = isDirty; }, [isDirty]);

  // Warn on browser-level navigation when there are unsaved changes.
  useEffect(() => {
    const handler = (e: BeforeUnloadEvent) => {
      if (!isDirtyRef.current) return;
      e.preventDefault();
    };
    window.addEventListener('beforeunload', handler);
    return () => window.removeEventListener('beforeunload', handler);
  }, []);

  const handleSave = useCallback(async () => {
    setSaving(true);
    setSaveError(null);
    // Extract the id from the current YAML so the route matches the declared id.
    const yamlId = extractYamlScalar(yaml, 'id') ?? workflowId;
    try {
      const saved = await apiClient.saveWorkflowYaml(projectId, yamlId, yaml);
      onSave?.(saved);
    } catch (err) {
      setSaveError(parseApiError400(err));
    } finally {
      setSaving(false);
    }
  }, [projectId, workflowId, yaml, onSave]);

  const handleDiscard = useCallback(() => {
    setYaml(initialYaml);
    setSaveError(null);
  }, [initialYaml]);

  const handleClose = useCallback(() => {
    if (isDirtyRef.current) {
      if (!window.confirm('You have unsaved changes. Close without saving?')) return;
    }
    onClose?.();
  }, [onClose]);

  const displayName = extractYamlScalar(yaml, 'name') ?? workflowId;
  const displayId = extractYamlScalar(yaml, 'id') ?? workflowId;

  return (
    <div className={styles.root}>
      <PageHeader
        title={displayName}
        description={displayId}
        actions={
          <Button
            appearance="subtle"
            size="small"
            icon={<DismissRegular />}
            onClick={handleClose}
            aria-label="Close"
          />
        }
      />

      {saveError && (
        <MessageBar intent="error">
          <MessageBarBody>
            {saveError.line != null
              ? `Line ${saveError.line}: ${saveError.message}`
              : saveError.message}
          </MessageBarBody>
        </MessageBar>
      )}

      <textarea
        className={styles.textarea}
        value={yaml}
        onChange={(e) => { setYaml(e.target.value); setSaveError(null); }}
        spellCheck={false}
        aria-label="Workflow YAML"
      />

      <div className={styles.footer}>
        {isDirty && (
          <span className={styles.statusWarning}>
            <WarningRegular fontSize={14} aria-hidden="true" />
            <Text size={200}>Unsaved changes</Text>
          </span>
        )}
        <div className={styles.footerActions}>
          <Button
            appearance="primary"
            disabled={saving}
            onClick={() => { void handleSave(); }}
          >
            {saving ? 'Saving...' : 'Save'}
          </Button>
          <Button
            appearance="secondary"
            disabled={saving || !isDirty}
            onClick={handleDiscard}
          >
            Discard changes
          </Button>
        </div>
      </div>
    </div>
  );
}
