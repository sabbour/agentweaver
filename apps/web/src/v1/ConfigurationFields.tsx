import { Field, Input, Select } from '@fluentui/react-components';
import { PageSection } from '../components/ui/PageSection';
import type {
  ModelSelectionSettings,
  ModelTaskKind,
  ProjectConfiguration,
  ProjectConfigurationChoices,
  ReasoningEffort,
} from './contracts';
import { needsModelCredential } from './modelCredential';

/** Owner-defined task names, not an invented model eligibility catalog. */
const TASKS: ReadonlyArray<{ value: ModelTaskKind; label: string }> = [
  { value: 'planning', label: 'Planning' }, { value: 'review', label: 'Review' },
  { value: 'design', label: 'Design' }, { value: 'security', label: 'Security' },
  { value: 'creativeWriting', label: 'Creative writing' }, { value: 'complex', label: 'Complex work' },
  { value: 'codeWriting', label: 'Code writing' }, { value: 'straightforward', label: 'Straightforward work' },
];

const REASONING_EFFORTS: ReasoningEffort[] = ['low', 'medium', 'high', 'xhigh'];
const REASONING_LABELS: Record<ReasoningEffort, string> = {
  low: 'Low',
  medium: 'Medium',
  high: 'High',
  xhigh: 'Extra high',
};

/** Narrows a select value to the reasoning levels accepted by the owner. */
function reasoning(value: string): ReasoningEffort | null {
  // Any other value, including the empty "use default" option, means no override.
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
export function ConfigurationFields({ configuration, onChange, readOnly = false, savedSelection, part, choices, choicesError = false }: {
  configuration: ProjectConfiguration;
  onChange: (configuration: ProjectConfiguration) => void;
  readOnly?: boolean;
  /** Owner-saved default selection; omit for read-only history. */
  savedSelection?: ModelSelectionSettings | null;
  part?: 'models' | 'components';
  /** Current owner-backed selection choices; they do not imply runtime provisioning. */
  choices?: ProjectConfigurationChoices | null;
  /** True when the choices endpoint could not be read. */
  choicesError?: boolean;
}) {
  const ownerChoicesAvailable = choices?.models.state === 'available';
  const modelReferences = ownerChoicesAvailable
    ? choices.models.choices.map((choice) => choice.reference)
    : [];
  const reasoningEfforts = ownerChoicesAvailable
    ? choices.models.reasoningEfforts
    : REASONING_EFFORTS;
  const modelOption = (reference: string) => (
    <option key={reference} value={reference}>
      {modelReferences.includes(reference) ? reference : `${reference} (saved)`}
    </option>
  );
  /** Changes the default model and decides what happens to its credential pin. */
  const setModelReference = (reference: string) => {
    if (!reference) {
      if (configuration.modelSelection)
        onChange({ ...configuration, modelSelection: null });
      return;
    }
    const current = configuration.modelSelection
      // After "Use platform default", returning to the saved model restores its full binding.
      ?? (savedSelection && reference === savedSelection.reference ? savedSelection : null);
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
  const showCredential = Boolean(configuration.modelSelection &&
    (savedSelection?.credentialReference || configuration.modelSelection.credentialReference));
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
      <PageSection title="Model defaults" description="Use an owner-listed model reference when available. Choices describe eligibility only; they do not provision models or change runs that already started.">
        {choicesError && <p className="v1-muted" role="status">
          Owner-backed model choices could not be loaded. Enter an existing model reference; the runtime checks availability when a run starts.
        </p>}
        {choices?.models.state === 'unavailable' && <div role="status">
          <p className="v1-muted">
            {choices.models.unavailableReason === 'model_capability_catalog_unavailable'
              ? 'Eligible model choices are unavailable: the project service has no model capability catalog yet. '
              : 'Eligible model choices are unavailable. '}
            Model references and reasoning levels entered here are not verified until a run starts.
          </p>
          {choices.models.savedReferences.length > 0 && <>
            <p className="v1-muted">Saved model references (not verified as eligible)</p>
            <ul>
              {choices.models.savedReferences.map((saved) =>
                <li key={saved.reference}>{saved.reference} ({saved.sources.join(', ')})</li>)}
            </ul>
          </>}
        </div>}
        <div className="v1-inline-form">
          <Field label="Default model reference">
            {ownerChoicesAvailable ? <Select value={configuration.modelSelection?.reference ?? ''} disabled={readOnly}
              onChange={(_, data) => setModelReference(data.value)}>
              <option value="">Use platform default</option>
              {modelReferences.map(modelOption)}
              {configuration.modelSelection?.reference &&
                !modelReferences.includes(configuration.modelSelection.reference) &&
                modelOption(configuration.modelSelection.reference)}
            </Select> : <Input value={configuration.modelSelection?.reference ?? ''} readOnly={readOnly}
              onChange={(_, data) => setModelReference(data.value)} />}
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
              <option value="">
                {`Use platform default${ownerChoicesAvailable ? ` (${REASONING_LABELS[choices.models.defaultReasoningEffort]})` : ''}`}
              </option>
              {configuration.modelSelection?.reasoningEffort &&
                !reasoningEfforts.includes(configuration.modelSelection.reasoningEffort) &&
                <option value={configuration.modelSelection.reasoningEffort}>
                  {`${REASONING_LABELS[configuration.modelSelection.reasoningEffort]} (saved)`}
                </option>}
              {reasoningEfforts.map((effort) =>
                <option key={effort} value={effort}>{REASONING_LABELS[effort]}</option>)}
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
      <PageSection title="Models by task" description="Leave a field empty to inherit its default. Owner-listed references are eligible choices; the runtime checks availability when a run starts.">
        {TASKS.map(({ value, label }) => {
          const selected = configuration.modelTaskOverrides?.find((item) => item.taskKind === value)?.selection;
          return <div className="v1-inline-form" key={value}>
            <Field label={`${label} model`}>
              {ownerChoicesAvailable ? <Select value={selected?.reference ?? ''} disabled={readOnly}
                onChange={(_, data) => setTask(value, data.value || null, selected?.reasoningEffort ?? null)}>
                <option value="">Use default</option>
                {modelReferences.map(modelOption)}
                {selected?.reference && !modelReferences.includes(selected.reference) && modelOption(selected.reference)}
              </Select> : <Input value={selected?.reference ?? ''} placeholder="Use default" readOnly={readOnly}
                onChange={(_, data) => setTask(value, data.value || null, selected?.reasoningEffort ?? null)} />}
            </Field>
            <Field label={`${label} reasoning effort`}>
              <Select value={selected?.reasoningEffort ?? ''} disabled={readOnly}
                onChange={(_, data) => setTask(value, selected?.reference ?? null, reasoning(data.value))}>
                <option value="">Use default</option>
                {selected?.reasoningEffort && !reasoningEfforts.includes(selected.reasoningEffort) &&
                  <option value={selected.reasoningEffort}>
                    {`${REASONING_LABELS[selected.reasoningEffort]} (saved)`}
                  </option>}
                {reasoningEfforts.map((effort) =>
                  <option key={effort} value={effort}>{REASONING_LABELS[effort]}</option>)}
              </Select>
            </Field>
          </div>;
        })}
      </PageSection>
      </>}
      {part !== 'models' && <PageSection title="Platform component implementations" description="Provider components are selected by the platform and cannot be overridden by a project. These selections do not imply provisioning.">
        {choicesError && <p className="v1-muted" role="status">Platform component selections could not be loaded.</p>}
        {choices && choices.providers.seams.map((seam) => (
          <Field key={seam.seam} label={`${seam.seam} (${seam.cardinality})`}>
            <Input value={seam.state === 'selected'
              ? seam.providerIds.join(', ')
              : `Unavailable (${seam.unavailableReason ?? 'unknown'}): ${seam.providerIds.join(', ')}`}
              readOnly />
          </Field>
        ))}
        {!choicesError && !choices && <p className="v1-muted">Platform component selections are not available for this view.</p>}
        {choices && choices.providers.seams.length === 0 && <p className="v1-muted">No platform provider selections are configured.</p>}
        <p className="v1-muted">Projects can override model configuration only; provider components are selected by the platform.</p>
        {(configuration.providerOverrides.length > 0 || configuration.orderedProviderOverrides.length > 0) && (
          <div>
            <p className="v1-muted">Saved project override (ignored)</p>
            <ul>
              {configuration.providerOverrides.map((provider, index) =>
                <li key={`single-${provider.seam}-${index}`}>{provider.seam}: {provider.providerId}</li>)}
              {configuration.orderedProviderOverrides.map((provider, index) =>
                <li key={`ordered-${provider.seam}-${index}`}>{provider.seam}: {provider.providerIds.join(' → ')}</li>)}
            </ul>
          </div>
        )}
      </PageSection>}
    </div>
  );
}
