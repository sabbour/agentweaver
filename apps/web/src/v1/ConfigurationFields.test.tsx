import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ConfigurationFields } from './ConfigurationFields';
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
    fireEvent.change(screen.getByLabelText('memory provider'), { target: { value: 'other' } });
    expect(onChange).toHaveBeenCalledWith({
      ...configuration, providerOverrides: [{ seam: 'memory', providerId: 'other' }],
    });
  });

  it('keeps historical settings read-only', () => {
    render(<ConfigurationFields configuration={fixture()} onChange={vi.fn()} readOnly />);
    expect((screen.getByLabelText('Default model reference') as HTMLInputElement).readOnly).toBe(true);
    expect((screen.getByLabelText('Default reasoning effort') as HTMLSelectElement).disabled).toBe(true);
  });
});
