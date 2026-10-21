import { Field, Input, Select } from '@fluentui/react-components';
import { PageSection } from '../components/ui/PageSection';
import type { ModelSelectionSettings, ModelTaskKind, ProjectConfiguration, ReasoningEffort } from './contracts';
import { needsModelCredential } from './modelCredential';

/**
 * Task kinds the service accepts for per-task model overrides. This is only the
 * list of task names; it does not say which models suit each task.
 */
const TASKS: ReadonlyArray<{ value: ModelTaskKind; label: string }> = [
  { value: 'planning', label: 'Planning' }, { value: 'review', label: 'Review' },
  { value: 'design', label: 'Design' }, { value: 'security', label: 'Security' },
  { value: 'creativeWriting', label: 'Creative writing' }, { value: 'complex', label: 'Complex work' },
  { value: 'codeWriting', label: 'Code writing' }, { value: 'straightforward', label: 'Straightforward work' },
];

/** Narrows a select value to the reasoning levels accepted by the owner. */
function reasoning(value: string): ReasoningEffort | null {
  return value === 'low' || value === 'medium' || value === 'high' || value === 'xhigh' ? value : null;
}

/**
 * Form fields for model defaults, per-task models, and platform component
 * implementations. Each change copies the whole configuration and replaces only
 * the edited field, so fields this form does not show are kept as they were.
 * Changing the default model clears a saved credential; switching back to the
 * saved model restores it. `part` limits the output to the model fields or the
 * platform component implementation fields; omit it to show both.
 */
export function ConfigurationFields({ configuration, onChange, readOnly = false, savedSelection, part }: {
  configuration: ProjectConfiguration;
  onChange: (configuration: ProjectConfiguration) => void;
  readOnly?: boolean;
  /** Owner-saved default selection; omit for read-only history. */
  savedSelection?: ModelSelectionSettings | null;
  part?: 'models' | 'components';
}) {
  const setModelReference = (reference: string) => {
    // Clearing an input that never had a model selection is a no-op. This avoids
    // creating an empty selection object just because the field was touched.
    if (!reference && !configuration.modelSelection) return;
    const current = configuration.modelSelection;
    // A saved credential belongs to the saved model. If the user picks another
    // model, drop the credential so it is not sent with the wrong model. If they
    // switch back to the saved model, put the saved credential back.
    // With no saved credential, keep whatever credential the draft already has.
    const credentialReference = savedSelection?.credentialReference
      ? reference === savedSelection.reference ? savedSelection.credentialReference : null
      : current?.credentialReference;
    onChange({ ...configuration, modelSelection: { ...current, reference, credentialReference } });
  };
  const setCredential = (part: 'id' | 'version', value: string) => {
    // A credential only makes sense for a chosen model.
    if (!configuration.modelSelection) return;
    // Start from empty strings so editing one part does not lose the other part.
    const previous = configuration.modelSelection.credentialReference ?? { id: '', version: '' };
    onChange({
      ...configuration,
      modelSelection: { ...configuration.modelSelection, credentialReference: { ...previous, [part]: value } },
    });
  };
  const missingCredential = needsModelCredential(configuration, savedSelection);
  // Show credential fields only when a credential is saved or already in the draft.
  // This version does not ask for a credential for models that never had one.
  const showCredential = Boolean(savedSelection?.credentialReference || configuration.modelSelection?.credentialReference);
  const setTask = (taskKind: ModelTaskKind, reference: string | null, effort: ReasoningEffort | null) => {
    const overrides = configuration.modelTaskOverrides ?? [];
    const previous = overrides.find((item) => item.taskKind === taskKind);
    // Keep other fields of the existing override (such as a credential) and replace only model and effort.
    const selection = { ...previous?.selection, reference, reasoningEffort: effort };
    // Remove the old entry for this task. Add it back only if something is still set,
    // so an override with both fields empty is deleted and the task inherits the default.
    const next = overrides.filter((item) => item.taskKind !== taskKind);
    if (reference || effort) next.push({ taskKind, selection });
    onChange({ ...configuration, modelTaskOverrides: next });
  };
  return (
    <div className="v1-stack">
      {part !== 'components' && <>
      <PageSection title="Model defaults" description="Use an existing model reference. Available choices are not listed by this version. Saved changes apply to future runs.">
        <div className="v1-inline-form">
          <Field label="Default model reference">
            <Input value={configuration.modelSelection?.reference ?? ''} readOnly={readOnly}
              onChange={(_, data) => setModelReference(data.value)} />
          </Field>
          <Field label="Default reasoning effort">
            <Select value={configuration.modelSelection?.reasoningEffort ?? ''} disabled={readOnly || !configuration.modelSelection}
              onChange={(_, data) => {
                // Effort is part of the default model selection, so it can only be set after a model is chosen.
                if (configuration.modelSelection) onChange({
                  ...configuration,
                  modelSelection: { ...configuration.modelSelection, reasoningEffort: reasoning(data.value) },
                });
              }}>
              <option value="">Use platform default</option>
              <option value="low">Low</option><option value="medium">Medium</option>
              <option value="high">High</option><option value="xhigh">Extra high</option>
            </Select>
          </Field>
        </div>
        {showCredential && (
          <div className="v1-inline-form">
            <Field label="Model credential ID" validationState={missingCredential ? 'error' : 'none'}
              validationMessage={missingCredential ? 'Choose a credential for this model.' : undefined}>
              <Input value={configuration.modelSelection?.credentialReference?.id ?? ''} readOnly={readOnly}
                onChange={(_, data) => setCredential('id', data.value)} />
            </Field>
            <Field label="Model credential version">
              <Input value={configuration.modelSelection?.credentialReference?.version ?? ''} readOnly={readOnly}
                onChange={(_, data) => setCredential('version', data.value)} />
            </Field>
          </div>
        )}
      </PageSection>
      <PageSection title="Models by task" description="Leave a field empty to inherit its default. The service validates references and supported reasoning levels.">
        {TASKS.map(({ value, label }) => {
          const selected = configuration.modelTaskOverrides?.find((item) => item.taskKind === value)?.selection;
          return <div className="v1-inline-form" key={value}>
            <Field label={`${label} model`}>
              <Input value={selected?.reference ?? ''} placeholder="Use default" readOnly={readOnly}
                onChange={(_, data) => setTask(value, data.value || null, selected?.reasoningEffort ?? null)} />
            </Field>
            <Field label={`${label} reasoning effort`}>
              <Select value={selected?.reasoningEffort ?? ''} disabled={readOnly}
                onChange={(_, data) => setTask(value, selected?.reference ?? null, reasoning(data.value))}>
                <option value="">Use default</option><option value="low">Low</option>
                <option value="medium">Medium</option><option value="high">High</option><option value="xhigh">Extra high</option>
              </Select>
            </Field>
          </div>;
        })}
      </PageSection>
      </>}
      {/* Implementation choices can only be edited, not added: the list shows the overrides already saved. */}
      {part !== 'models' && <PageSection title="Platform component implementations" description="Choose which implementation a platform component uses in this project. Enter saved implementation IDs; this version does not list the choices.">
        {configuration.providerOverrides.map((provider, index) => (
          <Field key={`${provider.seam}-${index}`} label={`${provider.seam} implementation`}>
            <Input value={provider.providerId} readOnly={readOnly}
              onChange={(_, data) => onChange({
                ...configuration,
                providerOverrides: configuration.providerOverrides.map((item, i) => i === index ? { ...item, providerId: data.value } : item),
              })} />
          </Field>
        ))}
        {configuration.providerOverrides.length === 0 && <p className="v1-muted">No project choices. Platform defaults apply.</p>}
        {configuration.orderedProviderOverrides.map((provider, index) => (
          <Field key={`${provider.seam}-${index}`} label={`${provider.seam} implementation order`} hint="Separate implementation IDs with commas, in priority order.">
            <Input value={provider.providerIds.join(', ')} readOnly={readOnly}
              onChange={(_, data) => onChange({
                ...configuration,
                // Split on commas and trim each ID. Order matters: the first ID has the highest priority.
                orderedProviderOverrides: configuration.orderedProviderOverrides.map((item, i) => i === index
                  ? { ...item, providerIds: data.value.split(',').map((id) => id.trim()) } : item),
              })} />
          </Field>
        ))}
      </PageSection>}
    </div>
  );
}
