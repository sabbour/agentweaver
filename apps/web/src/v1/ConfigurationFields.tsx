import { Field, Input, Select } from '@fluentui/react-components';
import { PageSection } from '../components/ui/PageSection';
import type { ModelSelectionSettings, ModelTaskKind, ProjectConfiguration, ReasoningEffort } from './contracts';
import { needsModelCredential } from './modelCredential';

/** Owner-defined task names, not an invented model eligibility catalog. */
const TASKS: ReadonlyArray<{ value: ModelTaskKind; label: string }> = [
  { value: 'planning', label: 'Planning' }, { value: 'review', label: 'Review' },
  { value: 'design', label: 'Design' }, { value: 'security', label: 'Security' },
  { value: 'creativeWriting', label: 'Creative writing' }, { value: 'complex', label: 'Complex work' },
  { value: 'codeWriting', label: 'Code writing' }, { value: 'straightforward', label: 'Straightforward work' },
];

/** Narrows a select value to the reasoning levels accepted by the owner. */
function reasoning(value: string): ReasoningEffort | null {
  // Any other value, including the empty "use default" option, means no override.
  return value === 'low' || value === 'medium' || value === 'high' || value === 'xhigh' ? value : null;
}

/**
 * Readable settings preserve every unedited field. Changing the default model clears a saved
 * credential pin; returning to the saved model restores it. `part` limits the output to the
 * model fields or the platform component implementation fields; omit it to show both.
 */
export function ConfigurationFields({ configuration, onChange, readOnly = false, savedSelection, part }: {
  configuration: ProjectConfiguration;
  onChange: (configuration: ProjectConfiguration) => void;
  readOnly?: boolean;
  /** Owner-saved default selection; omit for read-only history. */
  savedSelection?: ModelSelectionSettings | null;
  part?: 'models' | 'components';
}) {
  /** Changes the default model and decides what happens to its credential pin. */
  const setModelReference = (reference: string) => {
    // Typing into an empty field and clearing it again should not create an
    // empty model selection that would then be saved.
    if (!reference && !configuration.modelSelection) return;
    const current = configuration.modelSelection;
    // A credential pin belongs to one model. When the saved selection has a pin:
    // - back on the saved model: restore the saved pin;
    // - on any other model: clear it, so the user must choose a credential.
    // When nothing was pinned, keep whatever the draft already has.
    const credentialReference = savedSelection?.credentialReference
      ? reference === savedSelection.reference ? savedSelection.credentialReference : null
      : current?.credentialReference;
    // Spread the current selection so reasoning effort and other fields survive.
    onChange({ ...configuration, modelSelection: { ...current, reference, credentialReference } });
  };
  /** Edits the credential ID or version and keeps the other part. */
  const setCredential = (part: 'id' | 'version', value: string) => {
    // A credential only makes sense with a model selection. The fields are
    // shown only in that case, so this is a safety guard.
    if (!configuration.modelSelection) return;
    // Start from empty strings when no pin exists yet, so the first edit
    // creates a complete object.
    const previous = configuration.modelSelection.credentialReference ?? { id: '', version: '' };
    onChange({
      ...configuration,
      modelSelection: { ...configuration.modelSelection, credentialReference: { ...previous, [part]: value } },
    });
  };
  const missingCredential = needsModelCredential(configuration, savedSelection);
  // Show the credential fields when a pin exists now or existed when saved.
  // The second case keeps the fields visible after a model change clears the pin.
  const showCredential = Boolean(savedSelection?.credentialReference || configuration.modelSelection?.credentialReference);
  /** Sets or clears the model override for one task kind. */
  const setTask = (taskKind: ModelTaskKind, reference: string | null, effort: ReasoningEffort | null) => {
    const overrides = configuration.modelTaskOverrides ?? [];
    const previous = overrides.find((item) => item.taskKind === taskKind);
    // Keep any other fields from the previous override for this task.
    const selection = { ...previous?.selection, reference, reasoningEffort: effort };
    // Remove the old entry and add the new one at the end. When both model and
    // effort are empty, leave the entry out so the task uses the default.
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
              // Reasoning effort is stored on the model selection, so it can only
              // change after a default model exists.
              onChange={(_, data) => {
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
              // Split on commas and trim each ID, keeping the order. Empty
              // entries are not removed here.
              onChange={(_, data) => onChange({
                ...configuration,
                orderedProviderOverrides: configuration.orderedProviderOverrides.map((item, i) => i === index
                  ? { ...item, providerIds: data.value.split(',').map((id) => id.trim()) } : item),
              })} />
          </Field>
        ))}
      </PageSection>}
    </div>
  );
}
