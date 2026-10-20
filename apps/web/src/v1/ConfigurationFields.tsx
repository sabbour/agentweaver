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
  return value === 'low' || value === 'medium' || value === 'high' || value === 'xhigh' ? value : null;
}

/**
 * Readable settings preserve every unedited field. Changing the default model clears a saved
 * credential pin; returning to the saved model restores it.
 */
export function ConfigurationFields({ configuration, onChange, readOnly = false, savedSelection }: {
  configuration: ProjectConfiguration;
  onChange: (configuration: ProjectConfiguration) => void;
  readOnly?: boolean;
  /** Owner-saved default selection; omit for read-only history. */
  savedSelection?: ModelSelectionSettings | null;
}) {
  const setModelReference = (reference: string) => {
    if (!reference && !configuration.modelSelection) return;
    const current = configuration.modelSelection;
    const credentialReference = savedSelection?.credentialReference
      ? reference === savedSelection.reference ? savedSelection.credentialReference : null
      : current?.credentialReference;
    onChange({ ...configuration, modelSelection: { ...current, reference, credentialReference } });
  };
  const setCredential = (part: 'id' | 'version', value: string) => {
    if (!configuration.modelSelection) return;
    const previous = configuration.modelSelection.credentialReference ?? { id: '', version: '' };
    onChange({
      ...configuration,
      modelSelection: { ...configuration.modelSelection, credentialReference: { ...previous, [part]: value } },
    });
  };
  const missingCredential = needsModelCredential(configuration, savedSelection);
  const showCredential = Boolean(savedSelection?.credentialReference || configuration.modelSelection?.credentialReference);
  const setTask = (taskKind: ModelTaskKind, reference: string | null, effort: ReasoningEffort | null) => {
    const overrides = configuration.modelTaskOverrides ?? [];
    const previous = overrides.find((item) => item.taskKind === taskKind);
    const selection = { ...previous?.selection, reference, reasoningEffort: effort };
    const next = overrides.filter((item) => item.taskKind !== taskKind);
    if (reference || effort) next.push({ taskKind, selection });
    onChange({ ...configuration, modelTaskOverrides: next });
  };
  return (
    <div className="v1-stack">
      <PageSection title="Model defaults" description="Use an existing model reference. Available choices are not listed by this version. Saved changes apply to future runs.">
        <div className="v1-inline-form">
          <Field label="Default model reference">
            <Input value={configuration.modelSelection?.reference ?? ''} readOnly={readOnly}
              onChange={(_, data) => setModelReference(data.value)} />
          </Field>
          <Field label="Default reasoning effort">
            <Select value={configuration.modelSelection?.reasoningEffort ?? ''} disabled={readOnly || !configuration.modelSelection}
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
      <PageSection title="Providers" description="Edit saved provider IDs. Available provider choices are not listed by this version.">
        {configuration.providerOverrides.map((provider, index) => (
          <Field key={`${provider.seam}-${index}`} label={`${provider.seam} provider`}>
            <Input value={provider.providerId} readOnly={readOnly}
              onChange={(_, data) => onChange({
                ...configuration,
                providerOverrides: configuration.providerOverrides.map((item, i) => i === index ? { ...item, providerId: data.value } : item),
              })} />
          </Field>
        ))}
        {configuration.providerOverrides.length === 0 && <p className="v1-muted">No project provider overrides. Platform choices apply.</p>}
        {configuration.orderedProviderOverrides.map((provider, index) => (
          <Field key={`${provider.seam}-${index}`} label={`${provider.seam} provider order`} hint="Separate provider IDs with commas, in priority order.">
            <Input value={provider.providerIds.join(', ')} readOnly={readOnly}
              onChange={(_, data) => onChange({
                ...configuration,
                orderedProviderOverrides: configuration.orderedProviderOverrides.map((item, i) => i === index
                  ? { ...item, providerIds: data.value.split(',').map((id) => id.trim()) } : item),
              })} />
          </Field>
        ))}
      </PageSection>
    </div>
  );
}
