import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ConfigurationFields } from './ConfigurationFields';
import { needsModelCredential } from './modelCredential';
import type { ProjectConfiguration } from './contracts';

/** A complete configuration with unrelated exact references to preserve during edits. */
function fixture(): ProjectConfiguration {
  return {
    modelSelection: { reference: 'model-default', reasoningEffort: 'high', credentialReference: { id: 'credential', version: '3' } },
    modelTaskOverrides: [{ taskKind: 'planning', selection: { reference: 'planner', reasoningEffort: 'xhigh' } }],
    providerOverrides: [{ seam: 'memory', providerId: 'native' }],
    orderedProviderOverrides: [{ seam: 'tools', providerIds: ['first', 'second'] }],
    agentCharters: [], casting: [], blueprintWorkflowReferences: [], skills: [], runLimits: {},
    sourceControl: { authMode: 'githubApp', appConnectionId: 'saved-connection' },
  };
}

describe('readable project configuration', () => {
  it('edits a task model without dropping other tasks, references or configuration', () => {
    const configuration = fixture();
    const onChange = vi.fn();
    render(<ConfigurationFields configuration={configuration} onChange={onChange} />);
    fireEvent.change(screen.getByLabelText('Code writing model'), { target: { value: 'writer' } });
    const changed = onChange.mock.calls[0][0] as ProjectConfiguration;
    // The new task is appended; the existing planning override and every other
    // part of the configuration must be unchanged.
    expect(changed.modelTaskOverrides).toEqual([
      ...configuration.modelTaskOverrides!, { taskKind: 'codeWriting', selection: { reference: 'writer', reasoningEffort: null } },
    ]);
    expect(changed.sourceControl).toEqual(configuration.sourceControl);
    expect(changed.modelSelection).toEqual(configuration.modelSelection);
    expect(changed.providerOverrides).toEqual(configuration.providerOverrides);
  });

  it('edits reasoning effort without changing a model credential reference', () => {
    const configuration = fixture();
    const onChange = vi.fn();
    render(<ConfigurationFields configuration={configuration} onChange={onChange} />);
    fireEvent.change(screen.getByLabelText('Default reasoning effort'), { target: { value: 'medium' } });
    expect(onChange).toHaveBeenCalledWith({
      ...configuration, modelSelection: { ...configuration.modelSelection, reasoningEffort: 'medium' },
    });
  });

  it('preserves provider ordering and all unrelated settings when editing one provider', () => {
    const configuration = fixture();
    const onChange = vi.fn();
    render(<ConfigurationFields configuration={configuration} onChange={onChange} />);
    fireEvent.change(screen.getByLabelText('memory implementation'), { target: { value: 'other' } });
    expect(onChange).toHaveBeenCalledWith({
      ...configuration, providerOverrides: [{ seam: 'memory', providerId: 'other' }],
    });
  });

  it('clears a credential pin when the default model changes and restores it for the saved model', () => {
    const configuration = fixture();
    const saved = configuration.modelSelection;
    const onChange = vi.fn();
    const { rerender } = render(<ConfigurationFields configuration={configuration} onChange={onChange} savedSelection={saved} />);
    fireEvent.change(screen.getByLabelText('Default model reference'), { target: { value: 'model-other' } });
    const changed = onChange.mock.calls[0][0] as ProjectConfiguration;
    // A different model must drop the saved pin and flag the missing credential.
    expect(changed.modelSelection).toEqual({ ...saved, reference: 'model-other', credentialReference: null });
    expect(needsModelCredential(changed, saved)).toBe(true);

    // Switching back to the saved model must restore the saved pin exactly.
    rerender(<ConfigurationFields configuration={changed} onChange={onChange} savedSelection={saved} />);
    expect(screen.getByText('Choose a credential for this model.')).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Default model reference'), { target: { value: 'model-default' } });
    expect((onChange.mock.calls[1][0] as ProjectConfiguration).modelSelection).toEqual(saved);
  });

  it('accepts a new credential pin for a changed model', () => {
    const configuration = fixture();
    const saved = configuration.modelSelection;
    const changed = { ...configuration, modelSelection: { ...saved!, reference: 'model-other', credentialReference: null } };
    const onChange = vi.fn();
    const { rerender } = render(<ConfigurationFields configuration={changed} onChange={onChange} savedSelection={saved} />);
    fireEvent.change(screen.getByLabelText('Model credential ID'), { target: { value: 'credential-other' } });
    const withId = onChange.mock.calls[0][0] as ProjectConfiguration;
    // An ID without a version is still incomplete.
    expect(needsModelCredential(withId, saved)).toBe(true);
    rerender(<ConfigurationFields configuration={withId} onChange={onChange} savedSelection={saved} />);
    fireEvent.change(screen.getByLabelText('Model credential version'), { target: { value: '1' } });
    const complete = onChange.mock.calls[1][0] as ProjectConfiguration;
    expect(complete.modelSelection?.credentialReference).toEqual({ id: 'credential-other', version: '1' });
    expect(needsModelCredential(complete, saved)).toBe(false);
    // Hosted Copilot models need no project credential.
    expect(needsModelCredential({ ...changed, modelSelection: { ...changed.modelSelection, sourceMode: 'hostedCopilot' } }, saved)).toBe(false);
  });

  it('keeps historical settings read-only', () => {
    render(<ConfigurationFields configuration={fixture()} onChange={vi.fn()} readOnly />);
    expect((screen.getByLabelText('Default model reference') as HTMLInputElement).readOnly).toBe(true);
    expect((screen.getByLabelText('Default reasoning effort') as HTMLSelectElement).disabled).toBe(true);
  });
});
