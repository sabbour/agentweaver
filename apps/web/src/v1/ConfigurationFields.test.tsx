import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ConfigurationFields } from './ConfigurationFields';
import { needsModelCredential } from './modelCredential';
import type {
  ProjectConfiguration,
  ProjectConfigurationChoices,
} from './contracts';

/** A complete configuration with unrelated exact references to preserve during edits. */
function fixture(): ProjectConfiguration {
  return {
    modelSelection: { reference: 'model-default', reasoningEffort: 'high', credentialReference: { id: 'credential', version: '3' } },
    modelTaskOverrides: [{ taskKind: 'planning', selection: { reference: 'planner', reasoningEffort: 'xhigh' } }],
    providerOverrides: [{ seam: 'memory', providerId: 'native' }],
    orderedProviderOverrides: [{ seam: 'tools', providerIds: ['first', 'second'] }],
    agentCharters: [], casting: [], blueprintWorkflowReferences: [], skills: [], runLimits: {},
    sourceControl: { authMode: 'githubApp', identityConnectionId: 'saved-connection' },
  };
}

function ownerChoices(): ProjectConfigurationChoices {
  return {
    projectId: 'project-1',
    projectConfigurationRevision: 4,
    platformRuntimeRevision: 2,
    models: {
      state: 'available',
      unavailableReason: null,
      runtimeEligibility: 'verifiedAtRunStart',
      bindingsRevision: 'models-r7',
      defaultReasoningEffort: 'xhigh',
      choices: [
        { reference: 'platform-model', sourceMode: 'byok', reasoningEffortSource: 'modelBinding', reasoningEfforts: ['none', 'minimal', 'low', 'xhigh'] },
        { reference: 'planner', sourceMode: 'hostedCopilot', reasoningEffortSource: 'verifiedAtRunStart', reasoningEfforts: [] },
      ],
      savedReferences: [],
    },
    providers: {
      projectOverridesSupported: false,
      seams: [{
        seam: 'sandbox',
        cardinality: 'exclusive',
        state: 'selected',
        unavailableReason: null,
        providerIds: ['sandbox-platform'],
      }],
      ignoredProjectOverrides: ['memory', 'tools'],
    },
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
    configuration.modelSelection = { ...configuration.modelSelection!, reference: 'platform-model' };
    const onChange = vi.fn();
    render(<ConfigurationFields configuration={configuration} onChange={onChange}
      choices={ownerChoices()} part="models" />);
    fireEvent.change(screen.getByLabelText('Default reasoning effort'), { target: { value: 'xhigh' } });
    expect(onChange).toHaveBeenCalledWith({
      ...configuration, modelSelection: { ...configuration.modelSelection, reasoningEffort: 'xhigh' },
    });
  });

  it.each(['none', 'minimal'] as const)('accepts %s in model choices and saves it', (effort) => {
    const configuration = fixture();
    configuration.modelSelection = { ...configuration.modelSelection!, reference: 'platform-model' };
    const onChange = vi.fn();
    render(<ConfigurationFields configuration={configuration} onChange={onChange}
      choices={ownerChoices()} part="models" />);
    const select = screen.getByLabelText('Default reasoning effort') as HTMLSelectElement;
    expect(Array.from(select.options).map((option) => option.textContent)).toContain(
      effort === 'none' ? 'None' : 'Minimal');
    fireEvent.change(select, { target: { value: effort } });
    expect(onChange).toHaveBeenCalledWith({
      ...configuration, modelSelection: { ...configuration.modelSelection, reasoningEffort: effort },
    });
  });

  it('shows platform-selected providers and legacy overrides without editable project controls', () => {
    const configuration = fixture();
    const onChange = vi.fn();
    render(<ConfigurationFields configuration={configuration} onChange={onChange} choices={ownerChoices()} part="components" />);
    const sandbox = screen.getByLabelText('sandbox (exclusive)') as HTMLInputElement;
    expect(sandbox.value).toBe('sandbox-platform');
    expect(sandbox.readOnly).toBe(true);
    expect(screen.getByText('Saved project override (ignored)')).toBeTruthy();
    expect(screen.getByText('memory: native')).toBeTruthy();
    expect(screen.getByText('tools: first → second')).toBeTruthy();
    expect(screen.queryByLabelText('memory implementation')).toBeNull();
    expect(onChange).not.toHaveBeenCalled();
  });

  it('uses owner-backed model choices and keeps an unlisted saved value visible as not eligible', () => {
    const configuration = fixture();
    const choices = ownerChoices();
    choices.models.savedReferences = [
      { reference: 'model-default', sources: ['projectSaved'] },
      { reference: 'platform-model', sources: ['platformDefault'] },
    ];
    const onChange = vi.fn();
    render(<ConfigurationFields configuration={configuration} onChange={onChange} choices={choices}
      savedSelection={configuration.modelSelection} part="models" />);
    const defaultModel = screen.getByLabelText('Default model reference') as HTMLSelectElement;
    expect(Array.from(defaultModel.options).map((option) => option.textContent))
      .toEqual(['Use platform default', 'platform-model', 'planner', 'model-default (not currently eligible)']);
    expect(defaultModel.value).toBe('model-default');
    expect(screen.getByLabelText('Planning model').tagName).toBe('SELECT');
    // Only saved references outside the eligible list are called out.
    expect(screen.getByText('Saved model references not currently eligible')).toBeTruthy();
    expect(screen.getByText('model-default (projectSaved)')).toBeTruthy();
    expect(screen.queryByText('platform-model (platformDefault)')).toBeNull();

    fireEvent.change(defaultModel, { target: { value: 'platform-model' } });
    expect((onChange.mock.calls[0][0] as ProjectConfiguration).modelSelection).toEqual({
      ...configuration.modelSelection,
      reference: 'platform-model',
      credentialReference: null,
    });
  });

  it('offers only the reasoning levels a BYOK binding lists and marks an unlisted saved level', () => {
    const configuration: ProjectConfiguration = {
      ...fixture(), modelSelection: { reference: 'platform-model', reasoningEffort: 'high' }, modelTaskOverrides: [],
    };
    render(<ConfigurationFields configuration={configuration} onChange={vi.fn()} choices={ownerChoices()} part="models" />);
    const defaultEffort = screen.getByLabelText('Default reasoning effort') as HTMLSelectElement;
    expect(Array.from(defaultEffort.options).map((option) => option.textContent)).toEqual([
      'Use platform default (Extra high)', 'High (saved; not listed for this model)',
      'None', 'Minimal', 'Low', 'Extra high',
    ]);
    expect(screen.queryByText(/checked when a run starts/)).toBeNull();
  });

  it('does not invent hosted reasoning levels when no recent publication is available', () => {
    render(<ConfigurationFields configuration={fixture()} onChange={vi.fn()} choices={ownerChoices()} part="models" />);
    // The planning task uses the hosted "planner" reference.
    const planningEffort = screen.getByLabelText('Planning reasoning effort') as HTMLSelectElement;
    expect(Array.from(planningEffort.options).map((option) => option.textContent)).toEqual([
      'Use default', 'Extra high (saved; not listed for this model)',
    ]);
    expect(screen.getAllByText(/Recent hosted reasoning levels are unavailable/).length).toBeGreaterThan(0);
    expect(screen.getByText('Recent hosted reasoning levels are unavailable. AgentHost checks a selected level when a run starts.'))
      .toBeTruthy();
  });

  it('uses recent AgentHost-published reasoning levels from Project choices', () => {
    const choices = ownerChoices();
    choices.models.choices[1] = {
      ...choices.models.choices[1],
      reasoningEffortSource: 'hostedModelCatalog',
      reasoningEfforts: ['low', 'high'],
    };

    render(<ConfigurationFields configuration={fixture()} onChange={vi.fn()} choices={choices} part="models" />);
    const planningEffort = screen.getByLabelText('Planning reasoning effort') as HTMLSelectElement;
    expect(Array.from(planningEffort.options).map((option) => option.textContent))
      .toEqual(['Use default', 'Extra high (saved; not listed for this model)', 'Low', 'High']);
    expect(screen.getByText('Recent levels were published by AgentHost. AgentHost checks a selected level when a run starts.'))
      .toBeTruthy();
    expect(screen.queryByText(/Recent hosted reasoning levels are unavailable/)).toBeNull();
  });

  it('distinguishes a fresh hosted catalog with no supported levels from a missing publication', () => {
    const choices = ownerChoices();
    choices.models.choices[1] = {
      ...choices.models.choices[1],
      reasoningEffortSource: 'hostedModelCatalog',
      reasoningEfforts: [],
    };

    render(<ConfigurationFields configuration={fixture()} onChange={vi.fn()} choices={choices} part="models" />);
    const planningEffort = screen.getByLabelText('Planning reasoning effort') as HTMLSelectElement;
    expect(Array.from(planningEffort.options).map((option) => option.textContent))
      .toEqual(['Use default', 'Extra high (saved; not listed for this model)']);
    expect(screen.getByText('AgentHost found no supported reasoning levels for this model.'))
      .toBeTruthy();
  });

  it('uses the platform default model for tasks that inherit both defaults', () => {
    const configuration: ProjectConfiguration = {
      ...fixture(), modelSelection: null, modelTaskOverrides: [],
    };
    const choices = ownerChoices();
    choices.models.choices.push({
      reference: 'platform-hosted',
      sourceMode: 'hostedCopilot',
      reasoningEffortSource: 'verifiedAtRunStart',
      reasoningEfforts: [],
    });
    choices.models.savedReferences.push({ reference: 'platform-hosted', sources: ['platformDefault'] });
    choices.models.choices[2] = {
      ...choices.models.choices[2],
      reasoningEffortSource: 'hostedModelCatalog',
      reasoningEfforts: ['medium', 'high'],
    };

    render(<ConfigurationFields configuration={configuration} onChange={vi.fn()} choices={choices} part="models" />);
    const taskEffort = screen.getByLabelText('Code writing reasoning effort') as HTMLSelectElement;
    expect(Array.from(taskEffort.options).map((option) => option.textContent))
      .toEqual(['Use default', 'Medium', 'High']);
    expect(screen.getAllByText(/Recent levels were published by AgentHost/).length).toBeGreaterThan(0);
  });

  it('restores the full saved hosted binding after clearing to the platform default', () => {
    const configuration: ProjectConfiguration = {
      ...fixture(),
      modelSelection: {
        reference: 'platform-model', reasoningEffort: 'low', sourceMode: 'hostedCopilot', connectionId: 'connection-1',
      },
    };
    const saved = configuration.modelSelection;
    const onChange = vi.fn();
    const { rerender } = render(<ConfigurationFields configuration={configuration} onChange={onChange}
      choices={ownerChoices()} savedSelection={saved} part="models" />);
    fireEvent.change(screen.getByLabelText('Default model reference'), { target: { value: '' } });
    const cleared = onChange.mock.calls[0][0] as ProjectConfiguration;
    expect(cleared.modelSelection).toBeNull();

    rerender(<ConfigurationFields configuration={cleared} onChange={onChange}
      choices={ownerChoices()} savedSelection={saved} part="models" />);
    fireEvent.change(screen.getByLabelText('Default model reference'), { target: { value: 'platform-model' } });
    expect((onChange.mock.calls[1][0] as ProjectConfiguration).modelSelection).toEqual(saved);
  });

  it('reports the missing model-binding list and lists saved references as unverified', () => {
    const configuration = fixture();
    const unavailable = ownerChoices();
    unavailable.models.state = 'unavailable';
    unavailable.models.unavailableReason = 'model_capability_catalog_unavailable';
    unavailable.models.bindingsRevision = null;
    unavailable.models.choices = [];
    unavailable.models.savedReferences = [
      { reference: 'model-default', sources: ['projectSaved'] },
      { reference: 'platform-model', sources: ['platformDefault'] },
    ];
    render(<ConfigurationFields configuration={configuration} onChange={vi.fn()} choices={unavailable} part="models" />);
    expect(screen.getByText(/project service has no runtime model-binding list configured/)).toBeTruthy();
    expect(screen.getByText(/The runtime checks model references and reasoning levels when a run starts\./)).toBeTruthy();
    expect(screen.getByText('Saved model references (eligibility data unavailable)')).toBeTruthy();
    expect(screen.getByText('model-default (projectSaved)')).toBeTruthy();
    expect(screen.getByText('platform-model (platformDefault)')).toBeTruthy();
    // Saved references never become selectable choices.
    expect(screen.getByLabelText('Default model reference').tagName).toBe('INPUT');
    expect(screen.getByLabelText('Planning model').tagName).toBe('INPUT');

    const failedRender = render(<ConfigurationFields configuration={configuration} onChange={vi.fn()}
      choicesError part="models" />);
    expect(screen.getByText(/choices could not be loaded/)).toBeTruthy();
    failedRender.unmount();
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
