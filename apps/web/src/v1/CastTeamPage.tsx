/**
 * 0.x "Cast a team" wizard on v1 casting proposals. Step 1 picks a template or
 * roles and stages a proposal. When a team exists, the user can add members or
 * replace it. Step 2 reviews the proposed agents and can remove members. Step 3
 * confirms it. The Universe picker uses the Projects & Config catalog and saves
 * its choice in the revision-bound proposal. Formulate and Analyze use an
 * accepted run and keep generated output in the same review flow.
 */
import {
  Accordion, AccordionHeader, AccordionItem, AccordionPanel, Button, Checkbox, Field, MessageBar, MessageBarBody,
  Radio, RadioGroup, Select, Spinner, SpinButton, Text, Textarea, Title3, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components';
import { CheckmarkRegular } from '@fluentui/react-icons';
import { useCallback, useEffect, useRef, useState } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { PageSection } from '../components/ui/PageSection';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import { agentsPath, castableRoles, castingError, draftMembers, draftWithRoles, draftWithoutMember, isConflict } from './castingData';
import type {
  CastingGenerationRequest, ProjectCastingProposal, ProjectCastingRoleDefinition, ProjectCastingTemplateDefinition,
  ProjectConfiguration, VersionedProjectConfiguration,
} from './contracts';
import { ErrorNotice, Loading, PageHeading } from './pageParts';

type Step = 'cast' | 'review' | 'confirm';
type CastingIntent = 'augment' | 'recast';
type CastingMode = 'manual' | 'formulate' | 'analyze';
type GeneratedMode = Exclude<CastingMode, 'manual'>;
interface PendingCastingGeneration {
  projectId: string;
  mode: GeneratedMode;
  prompt: string;
  expectedConfigurationRevision: number;
  castingUniverse: string | null;
  intent: CastingIntent;
  idempotencyKey: string;
  runId: string | null;
  sessionId: string | null;
  proposalId: string | null;
  requestId: string | null;
}
const STEPS: Step[] = ['cast', 'review', 'confirm'];
const STEP_LABELS: Record<Step, string> = { cast: 'Cast', review: 'Review proposal', confirm: 'Confirm' };
const CASTING_GENERATION_STORAGE_KEY = 'agentweaver.v1.casting-generation.';

function isPendingCastingGeneration(value: unknown, projectId: string): value is PendingCastingGeneration {
  if (typeof value !== 'object' || value === null) return false;
  const item = value as Record<string, unknown>;
  return item.projectId === projectId &&
    (item.mode === 'formulate' || item.mode === 'analyze') &&
    typeof item.prompt === 'string' &&
    typeof item.expectedConfigurationRevision === 'number' &&
    (typeof item.castingUniverse === 'string' || item.castingUniverse === null) &&
    (item.intent === 'augment' || item.intent === 'recast') &&
    typeof item.idempotencyKey === 'string' &&
    (typeof item.runId === 'string' || item.runId === null) &&
    (typeof item.sessionId === 'string' || item.sessionId === null) &&
    (typeof item.proposalId === 'string' || item.proposalId === null) &&
    (typeof item.requestId === 'string' || item.requestId === null);
}

function readPendingCastingGeneration(projectId: string): PendingCastingGeneration | null {
  const key = CASTING_GENERATION_STORAGE_KEY + projectId;
  const saved = sessionStorage.getItem(key);
  if (saved === null) return null;
  try {
    const parsed: unknown = JSON.parse(saved);
    if (isPendingCastingGeneration(parsed, projectId)) return parsed;
  } catch {
    // Remove an unreadable local draft; the server remains the source of truth.
  }
  sessionStorage.removeItem(key);
  return null;
}

function buildCastingGenerationPrompt(
  mode: GeneratedMode,
  brief: string,
  teamSize: number,
  preferredRoles: string[],
  castingUniverse: string | null,
  configuration: ProjectConfiguration,
  intent: CastingIntent,
): string {
  const roleInstruction = preferredRoles.length > 0
    ? `Preferred roles: ${preferredRoles.join(', ')}.`
    : '';
  const rosterInstruction = intent === 'augment' && configuration.casting.length > 0
    ? `Keep every current team member and charter unchanged, then add exactly ${teamSize} members.`
    : `Replace the current team with exactly ${teamSize} members.`;
  const universeInstruction = castingUniverse
    ? `Use the ${castingUniverse} universe.`
    : 'Choose any suitable universe.';
  const task = mode === 'formulate'
    ? `Create a team from this brief:\n${brief.trim()}`
    : `Analyze this project and suggest a team. Current project casting context: ${JSON.stringify({
      agentCharters: configuration.agentCharters,
      casting: configuration.casting,
      castingUniverse: configuration.castingUniverse ?? null,
    })}`;
  return [
    'Prepare a project casting proposal for user review.',
    `Return only valid JSON with exactly these properties: "agentCharters" and "casting". Do not use Markdown.`,
    'Each agentCharters item must contain agentId, name, role, and charter.',
    'Each casting item must contain agentId, role, and order. Use the same unique agentId values in both arrays and orders from 0.',
    rosterInstruction,
    universeInstruction,
    roleInstruction,
    task,
  ].filter(Boolean).join('\n\n');
}

function castingGenerationStorageKey(projectId: string): string {
  return CASTING_GENERATION_STORAGE_KEY + projectId;
}

const useStyles = makeStyles({
  wizardLayout: { display: 'grid', gridTemplateColumns: '200px minmax(0, 1fr)', gap: tokens.spacingHorizontalXL, alignItems: 'start' },
  stepRail: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS, position: 'sticky', top: tokens.spacingVerticalL },
  stepItem: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalM, padding: `${tokens.spacingVerticalXS} 0` },
  stepBadge: {
    width: '24px', height: '24px', borderRadius: '50%', display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
    fontSize: tokens.fontSizeBase200, fontWeight: tokens.fontWeightSemibold, flexShrink: 0,
    backgroundColor: tokens.colorNeutralBackground3, color: tokens.colorNeutralForeground3, border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  stepBadgeCurrent: { backgroundColor: tokens.colorNeutralForeground1, color: tokens.colorNeutralForegroundOnBrand, border: 'none' },
  stepBadgeComplete: { color: tokens.colorNeutralForeground1, border: `1px solid ${tokens.colorNeutralStroke1}` },
  stepLabel: { fontSize: tokens.fontSizeBase300, color: tokens.colorNeutralForeground3 },
  stepLabelCurrent: { color: tokens.colorNeutralForeground1, fontWeight: tokens.fontWeightSemibold },
  stepLabelComplete: { color: tokens.colorNeutralForeground2 },
  pane: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalL, minWidth: 0 },
  card: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  templateGrid: { display: 'grid', gridTemplateColumns: '1fr 1fr', gap: tokens.spacingHorizontalM },
  templateCard: {
    padding: tokens.spacingVerticalM, backgroundColor: tokens.colorNeutralBackground2, border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusSmall, cursor: 'pointer', textAlign: 'left', display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS,
    ':hover': { backgroundColor: tokens.colorNeutralBackground3 },
  },
  templateCardSelected: { backgroundColor: tokens.colorNeutralBackground3, border: `2px solid ${tokens.colorNeutralStroke1}` },
  templateTitle: { fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase300 },
  rolesGrid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(160px, 1fr))', gap: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}` },
  navRow: { display: 'flex', gap: tokens.spacingHorizontalM, alignItems: 'center', justifyContent: 'flex-end' },
  castNavRow: { display: 'flex', gap: tokens.spacingHorizontalM, alignItems: 'center', justifyContent: 'space-between' },
  memberList: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  memberCard: {
    padding: tokens.spacingVerticalM, backgroundColor: tokens.colorNeutralBackground2, border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusSmall, display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS,
  },
  memberHeader: { display: 'flex', alignItems: 'center', justifyContent: 'space-between' },
  memberName: { fontWeight: tokens.fontWeightSemibold },
  charterToggle: { alignSelf: 'flex-start', padding: 0, minWidth: 'unset' },
  charterFull: {
    fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3,
    whiteSpace: 'pre-wrap', maxHeight: '200px', overflow: 'auto', margin: 0,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`, paddingTop: tokens.spacingVerticalXS,
  },
});

/** True when two role ID lists hold the same IDs, in any order. */
function sameRoleSet(left: string[], right: string[]): boolean {
  if (left.length !== right.length) return false;
  const set = new Set(right);
  return left.every((id) => set.has(id));
}

/** Route page for `/projects/:projectId/team/cast`. `?proposal=` opens a proposal for review. */
export function CastTeamPage() {
  const styles = useStyles();
  const { projectId = '' } = useParams();
  const [searchParams] = useSearchParams();
  const linkedProposalId = searchParams.get('proposal');
  const navigate = useNavigate();
  const { apiCall, authorize, ensureSession } = useAuth();

  const [step, setStep] = useState<Step>('cast');
  const [templates, setTemplates] = useState<ProjectCastingTemplateDefinition[]>([]);
  const [roles, setRoles] = useState<ProjectCastingRoleDefinition[]>([]);
  const [configuration, setConfiguration] = useState<VersionedProjectConfiguration | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [selectedTemplateId, setSelectedTemplateId] = useState('');
  const [selectedRoleIds, setSelectedRoleIds] = useState<string[]>([]);
  const [castingMode, setCastingMode] = useState<CastingMode>('manual');
  const [generationBrief, setGenerationBrief] = useState('');
  const [generationTeamSize, setGenerationTeamSize] = useState(4);
  const [pendingGeneration, setPendingGeneration] = useState<PendingCastingGeneration | null>(
    () => readPendingCastingGeneration(projectId),
  );
  const [generationNeedsSignIn, setGenerationNeedsSignIn] = useState(false);
  const generationInFlight = useRef(false);
  const [universes, setUniverses] = useState<string[]>([]);
  const [universe, setUniverse] = useState('');
  const [castingIntent, setCastingIntent] = useState<CastingIntent>('augment');
  const [proposalIntent, setProposalIntent] = useState<CastingIntent | null>(null);
  const [proposal, setProposal] = useState<ProjectCastingProposal | null>(null);
  // True when this page created the proposal, so Back may discard it.
  const [createdHere, setCreatedHere] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());

  const persistPendingGeneration = (value: PendingCastingGeneration | null) => {
    if (value) sessionStorage.setItem(castingGenerationStorageKey(projectId), JSON.stringify(value));
    else sessionStorage.removeItem(castingGenerationStorageKey(projectId));
    setPendingGeneration(value);
  };

  /** Re-reads the current configuration, whose revision fences new proposals. */
  const readConfiguration = useCallback(async () => {
    const result = await apiCall((token, tenant) => gatewayClient.getProjectConfiguration(token, projectId, tenant), null);
    // Never fence a proposal with another project's revision.
    if (result.projectId !== projectId) throw new Error('The server returned configuration for a different project.');
    setConfiguration(result);
    return result;
  }, [apiCall, projectId]);

  /** Re-reads one proposal, for example after a revision conflict. */
  const readProposal = useCallback(async (proposalId: string) => {
    const result = await apiCall((token, tenant) => gatewayClient.getCastingProposal(token, projectId, proposalId, tenant), null);
    if (result.projectId !== projectId) throw new Error('The server returned a proposal for a different project.');
    setProposal(result);
    return result;
  }, [apiCall, projectId]);

  useEffect(() => {
    let cancelled = false;
    // Load on the next tick so state updates stay out of the effect body.
    const timer = window.setTimeout(() => {
      void (async () => {
        setLoading(true);
        setLoadError(null);
        // Keep a prior project's explicit universe from carrying into this project.
        setUniverse('');
        setPendingGeneration(readPendingCastingGeneration(projectId));
        setGenerationNeedsSignIn(false);
        try {
          const [templateList, roleList, , universeList] = await Promise.all([
            apiCall((token, tenant) => gatewayClient.listCastingTemplates(token, tenant), null),
            apiCall((token, tenant) => gatewayClient.listCatalogRoles(token, tenant), null),
            readConfiguration(),
            apiCall((token, tenant) => gatewayClient.listCastingUniverses(token, projectId, tenant), null),
          ]);
          if (cancelled) return;
          setTemplates(templateList);
          setRoles(castableRoles(roleList));
          setUniverses(universeList.universes);
          // A link from the Agents page opens that proposal on the review step.
          if (linkedProposalId) {
            await readProposal(linkedProposalId);
            if (!cancelled) { setCreatedHere(false); setStep('review'); }
          }
        } catch (reason) {
          if (!cancelled) setLoadError(castingError(reason, false));
        } finally {
          if (!cancelled) setLoading(false);
        }
      })();
    }, 0);
    return () => { cancelled = true; window.clearTimeout(timer); };
  }, [apiCall, linkedProposalId, projectId, readConfiguration, readProposal]);

  const selectedTemplate = templates.find((item) => item.id === selectedTemplateId) ?? null;
  // Changing the template's roles turns the request into a manual pick of the checked roles.
  const rolesOverridden = !selectedTemplate || !sameRoleSet(selectedRoleIds, selectedTemplate.roleIds);
  const canReview = selectedRoleIds.length > 0 && configuration !== null;

  const chooseTemplate = (template: ProjectCastingTemplateDefinition) => {
    setSelectedTemplateId(template.id);
    setSelectedRoleIds(template.roleIds);
  };

  /** Runs one write. On a revision conflict it re-reads the data, then shows the message. */
  const write = async (operation: () => Promise<void>, onConflict: () => Promise<unknown>) => {
    setBusy(true);
    setError(null);
    try {
      await operation();
    } catch (reason) {
      if (isConflict(reason)) {
        // Re-read first, so the message about the latest version is true.
        try { await onConflict(); } catch { /* the conflict message below still applies */ }
      }
      setError(castingError(reason, true));
    } finally {
      setBusy(false);
    }
  };

  const processCastingGeneration = async (initial: PendingCastingGeneration) => {
    if (generationInFlight.current) return;
    generationInFlight.current = true;
    try {
      let current = initial;
      if (!current.runId || !current.sessionId) {
        try {
          const created = await apiCall((token, tenant) => gatewayClient.createProjectRun(
            token,
            projectId,
            {
              firstMessage: current.prompt,
              expectedProjectConfigRevision: current.expectedConfigurationRevision,
            },
            current.idempotencyKey,
            tenant,
          ), null);
          if (!created.runId || !created.rootSessionId)
            throw new Error('The service returned an incomplete run identity.');
          current = { ...current, runId: created.runId, sessionId: created.rootSessionId };
          persistPendingGeneration(current);
        } catch (reason) {
          if (isConflict(reason)) persistPendingGeneration(null);
          throw reason;
        }
      }

      const runId = current.runId;
      const sessionId = current.sessionId;
      if (!runId || !sessionId) throw new Error('The service did not create a casting run.');
      const binding = { projectId, runId };
      if (!await ensureSession(binding)) {
        setGenerationNeedsSignIn(true);
        return;
      }
      setGenerationNeedsSignIn(false);

      if (!current.proposalId) {
        const request: CastingGenerationRequest = {
          expectedConfigurationRevision: current.expectedConfigurationRevision,
          runId,
          sessionId,
          idempotencyKey: current.idempotencyKey,
          prompt: current.prompt,
          castingUniverse: current.castingUniverse,
        };
        const accepted = await apiCall((token, tenant) => current.mode === 'formulate'
          ? gatewayClient.formulateCastingProposal(token, projectId, request, tenant)
          : gatewayClient.analyzeCastingProposal(token, projectId, request, tenant), binding);
        if (accepted.runId !== runId || accepted.mode !== current.mode ||
            !accepted.proposalId || !accepted.requestId)
          throw new Error('The service returned a different generation request.');
        current = {
          ...current,
          proposalId: accepted.proposalId,
          requestId: accepted.requestId,
        };
        persistPendingGeneration(current);
      }

      let result = await readProposal(current.proposalId);
      let generation = result.generation;
      if (!generation || generation.requestId !== current.requestId || generation.mode !== current.mode)
        throw new Error('The service returned a different generation proposal.');
      for (let attempt = 0; attempt < 30 && generation.state === 'pending'; attempt++) {
        await new Promise<void>((resolve) => window.setTimeout(resolve, 1000));
        result = await readProposal(current.proposalId);
        generation = result.generation;
        if (!generation || generation.requestId !== current.requestId || generation.mode !== current.mode)
          throw new Error('The service returned a different generation proposal.');
      }

      setProposalIntent(current.intent);
      setCreatedHere(true);
      setStep('review');
      if (generation.state !== 'pending') persistPendingGeneration(null);
    } finally {
      generationInFlight.current = false;
    }
  };

  const continueCastingGeneration = () => write(async () => {
    if (pendingGeneration) await processCastingGeneration(pendingGeneration);
  }, readConfiguration);

  const beginCastingGeneration = (mode: GeneratedMode) => write(async () => {
    if (pendingGeneration)
      throw new Error('Resume or reject the saved generation before starting another.');
    if (mode === 'formulate' && !generationBrief.trim())
      throw new Error('Describe the team you want first.');
    const latest = await readConfiguration();
    const preferredRoles = selectedRoleIds.map((id) =>
      roles.find((role) => role.id === id)?.title ?? id);
    const prompt = buildCastingGenerationPrompt(
      mode,
      generationBrief,
      generationTeamSize,
      preferredRoles,
      universe || null,
      latest.configuration,
      castingIntent,
    );
    const pending: PendingCastingGeneration = {
      projectId,
      mode,
      prompt,
      expectedConfigurationRevision: latest.revision,
      castingUniverse: universe || null,
      intent: castingIntent,
      idempotencyKey: crypto.randomUUID(),
      runId: null,
      sessionId: null,
      proposalId: null,
      requestId: null,
    };
    persistPendingGeneration(pending);
    await processCastingGeneration(pending);
  }, readConfiguration);

  /** Stages either a complete augment draft or the owner-built replacement proposal. */
  const createProposal = () => write(async () => {
    if (!configuration) return;
    const expectedConfigurationRevision = configuration.revision;
    let created: ProjectCastingProposal;
    if (currentCount > 0 && castingIntent === 'augment') {
      const selectedRoles: ProjectCastingRoleDefinition[] = [];
      for (const id of selectedRoleIds) {
        const role = roles.find((item) => item.id === id);
        if (!role) throw new Error('One or more selected roles are no longer available. Reload the page and try again.');
        selectedRoles.push(role);
      }
      // Augment must preserve the accepted roster, so stage the complete draft through the same owner.
      created = await apiCall((token, tenant) => gatewayClient.createCastingDraftProposal(token, projectId, {
        expectedConfigurationRevision,
        // Keep the universe choice in the same revision-bound draft as the added members.
        draft: { ...draftWithRoles(configuration.configuration, selectedRoles), castingUniverse: universe || null },
      }, tenant), null);
      setProposalIntent('augment');
    } else {
      // Scenario and manual routes keep replacement construction in Projects & Config.
      created = await apiCall((token, tenant) => gatewayClient.createCastingProposal(token, projectId, !rolesOverridden && selectedTemplate
        ? { mode: 'scenario', expectedConfigurationRevision, templateId: selectedTemplate.id, universe: universe || null }
        : { mode: 'manual', expectedConfigurationRevision, roleIds: selectedRoleIds, universe: universe || null }, tenant), null);
      setProposalIntent('recast');
    }
    setProposal(created);
    setCreatedHere(true);
    setStep('review');
  }, readConfiguration);

  /** Removes one member from the pending draft. */
  const removeMember = (agentId: string) => write(async () => {
    if (!proposal || proposal.generation && proposal.generation.state !== 'completed') return;
    const updated = await apiCall((token, tenant) => gatewayClient.updateCastingProposal(token, projectId, proposal.proposalId, {
      expectedDraftRevision: proposal.draftRevision,
      draft: draftWithoutMember(proposal.draft, agentId),
    }, tenant), null);
    setProposal(updated);
  }, () => readProposal(proposal!.proposalId));

  /** Rejects the proposal, then goes to `next`. */
  const reject = (next: () => void) => write(async () => {
    if (!proposal) return;
    await apiCall((token, tenant) => gatewayClient.transitionCastingProposal(
      token, projectId, proposal.proposalId, 'reject', proposal.draftRevision, tenant,
    ), null);
    if (proposal.generation) persistPendingGeneration(null);
    setProposal(null);
    next();
  }, () => readProposal(proposal!.proposalId));

  /** Confirms the proposal, which saves it as a new configuration revision. */
  const confirm = () => write(async () => {
    if (!proposal || proposal.generation && proposal.generation.state !== 'completed') return;
    await apiCall((token, tenant) => gatewayClient.transitionCastingProposal(
      token, projectId, proposal.proposalId, 'confirm', proposal.draftRevision, tenant,
    ), null);
    navigate(agentsPath(projectId));
  }, () => readProposal(proposal!.proposalId));

  const toggleCharter = (agentId: string) => setExpanded((previous) => {
    const next = new Set(previous);
    if (next.has(agentId)) next.delete(agentId); else next.add(agentId);
    return next;
  });

  const members = proposal ? draftMembers(proposal.draft) : [];
  const currentCount = configuration?.configuration.casting.length ?? 0;
  const existingAgentIds = new Set(configuration?.configuration.casting.map((item) => item.agentId) ?? []);
  const addedMembers = members.filter((member) => !existingAgentIds.has(member.agentId));
  // Linked proposals do not retain their original action, so recognize an augment by its unchanged roster plus additions.
  const preservesCurrentRoster = Boolean(proposal && configuration?.configuration.casting.every((existing) =>
    proposal.draft.casting.some((candidate) =>
      candidate.agentId === existing.agentId && candidate.role === existing.role && candidate.order === existing.order)));
  const augmentProposal = proposalIntent === 'augment' ||
    (proposalIntent === null && currentCount > 0 && addedMembers.length > 0 && preservesCurrentRoster);
  const pending = proposal?.state === 'pending';
  const generationStatus = proposal?.generation ?? null;
  const generatedDraftReady = generationStatus === null || generationStatus.state === 'completed';
  const canEditProposal = pending && generatedDraftReady;
  const currentIndex = STEPS.indexOf(step);
  const errorBar = error && <ErrorNotice>{error}</ErrorNotice>;

  return (
    <>
      <PageHeading title="Cast a team" description="Choose a casting mode, then review the proposal before it changes the project." page="Cast a team" />
      {loading ? <Loading label="Loading casting options" /> : loadError ? <ErrorNotice>{loadError}</ErrorNotice> : (
        <div className={styles.wizardLayout}>
          <div className={styles.stepRail} aria-label="Casting progress">
            {STEPS.map((item, index) => {
              const complete = index < currentIndex;
              const current = item === step;
              return (
                <div key={item} className={styles.stepItem} aria-current={current ? 'step' : undefined}>
                  <span className={mergeClasses(styles.stepBadge, complete && styles.stepBadgeComplete, current && styles.stepBadgeCurrent)}>
                    {complete ? <CheckmarkRegular fontSize={14} /> : index + 1}
                  </span>
                  <Text className={mergeClasses(styles.stepLabel, complete && styles.stepLabelComplete, current && styles.stepLabelCurrent)}>
                    {STEP_LABELS[item]}
                  </Text>
                </div>
              );
            })}
          </div>

          <div className={styles.pane}>
            {step === 'cast' && (
              <>
                <PageSection title="Casting mode" description="Choose how to prepare the proposal.">
                  <RadioGroup
                    value={castingMode}
                    onChange={(_, data) => {
                      if (data.value === 'manual' || data.value === 'formulate' || data.value === 'analyze')
                        setCastingMode(data.value);
                    }}
                    aria-label="Casting mode"
                  >
                    <Radio value="manual" label="Template or roles" />
                    <Radio value="formulate" label="Formulate" />
                    <Radio value="analyze" label="Analyze" />
                  </RadioGroup>
                </PageSection>
                {castingMode === 'manual' && (
                  <>
                    <PageSection title="Template" description="Start from a built-in team. You can change its roles below.">
                      {templates.length === 0 ? <Text className={styles.muted}>No templates available.</Text> : (
                        <div className={styles.templateGrid}>
                          {templates.map((template) => (
                            <button
                              key={template.id}
                              type="button"
                              className={mergeClasses(styles.templateCard, selectedTemplateId === template.id && styles.templateCardSelected)}
                              aria-pressed={selectedTemplateId === template.id}
                              onClick={() => chooseTemplate(template)}
                            >
                              <Text className={styles.templateTitle}>{template.title}</Text>
                              <Text className={styles.muted}>{template.description}</Text>
                            </button>
                          ))}
                        </div>
                      )}
                    </PageSection>
                    <PageSection title="Roles" description="Tune the role mix before reviewing the proposal.">
                      <div className={styles.rolesGrid}>
                        {roles.map((role) => (
                          <Checkbox
                            key={role.id}
                            label={role.title}
                            checked={selectedRoleIds.includes(role.id)}
                            onChange={(_, data) => setSelectedRoleIds((previous) => data.checked
                              ? [...previous, role.id]
                              : previous.filter((id) => id !== role.id))}
                          />
                        ))}
                      </div>
                    </PageSection>
                  </>
                )}
                {castingMode === 'formulate' && (
                  <PageSection title="Formulate" description="Describe the team you need.">
                    <Textarea
                      aria-label="Team brief"
                      value={generationBrief}
                      onChange={(_, data) => setGenerationBrief(data.value)}
                      placeholder="Describe the project team"
                      rows={4}
                    />
                    <Field label="Team size">
                      <SpinButton
                        min={2}
                        max={10}
                        step={1}
                        value={generationTeamSize}
                        onChange={(_, data) => {
                          if (data.value !== undefined && data.value !== null)
                            setGenerationTeamSize(Math.min(10, Math.max(2, data.value)));
                        }}
                      />
                    </Field>
                    {errorBar}
                    <Button
                      appearance="primary"
                      disabled={!generationBrief.trim() || busy || pendingGeneration !== null}
                      onClick={() => void beginCastingGeneration('formulate')}
                    >
                      Formulate
                    </Button>
                  </PageSection>
                )}
                {castingMode === 'analyze' && (
                  <PageSection title="Analyze" description="Review the current team context and suggest roles.">
                    <Text className={styles.muted}>The system will analyze the project and suggest a team.</Text>
                    <Field label="Team size">
                      <SpinButton
                        min={2}
                        max={10}
                        step={1}
                        value={generationTeamSize}
                        onChange={(_, data) => {
                          if (data.value !== undefined && data.value !== null)
                            setGenerationTeamSize(Math.min(10, Math.max(2, data.value)));
                        }}
                      />
                    </Field>
                    {errorBar}
                    <Button
                      appearance="primary"
                      disabled={!configuration || busy || pendingGeneration !== null}
                      onClick={() => void beginCastingGeneration('analyze')}
                    >
                      Analyze
                    </Button>
                  </PageSection>
                )}
                <Accordion collapsible>
                  <AccordionItem value="universe">
                    <AccordionHeader>Universe</AccordionHeader>
                    <AccordionPanel>
                      <Select
                        aria-label="Universe"
                        value={universe}
                        onChange={(_, data) => setUniverse(data.value)}
                      >
                        <option value="">Random (any universe)</option>
                        {universes.map((item) => <option key={item} value={item}>{item}</option>)}
                      </Select>
                    </AccordionPanel>
                  </AccordionItem>
                </Accordion>
                {currentCount > 0 && (
                  <PageSection title="Current team" description="Choose what this proposal will do when confirmed.">
                    <RadioGroup
                      value={castingIntent}
                      onChange={(_, data) => {
                        if (data.value === 'augment' || data.value === 'recast') setCastingIntent(data.value);
                      }}
                      aria-label="Team change"
                    >
                      <Radio value="augment" label="Augment — add new members to the current team" />
                      <Radio value="recast" label="Recast — replace the current team" />
                    </RadioGroup>
                  </PageSection>
                )}
                {currentCount > 0 && (
                  <Text className={styles.muted}>
                    This project has {currentCount} team member{currentCount === 1 ? '' : 's'}. Runs that already started keep their accepted team.
                  </Text>
                )}
                {pendingGeneration && (
                  <MessageBar intent="warning">
                    <MessageBarBody>
                      {pendingGeneration.proposalId
                        ? 'Your generation is saved. Resume to check the proposal.'
                        : 'Your generation run is saved. Continue to prepare the proposal.'}
                    </MessageBarBody>
                    <div className={styles.navRow}>
                      <Button appearance="secondary" disabled={busy} onClick={() => void continueCastingGeneration()}>
                        Continue
                      </Button>
                      {generationNeedsSignIn && pendingGeneration.runId && (
                        <Button appearance="primary" disabled={busy} onClick={() => {
                          const runId = pendingGeneration.runId;
                          if (runId) void authorize({ projectId: pendingGeneration.projectId, runId });
                        }}>Sign in</Button>
                      )}
                    </div>
                  </MessageBar>
                )}
                {errorBar}
                <div className={styles.castNavRow}>
                  <Button appearance="secondary" onClick={() => navigate(agentsPath(projectId))}>Cancel</Button>
                  <div className={styles.navRow}>
                    {busy && <Spinner size="extra-tiny" aria-hidden="true" />}
                    {castingMode === 'manual' && (
                      <Button appearance="primary" disabled={!canReview || busy || pendingGeneration !== null} onClick={() => void createProposal()}>
                        {busy ? 'Casting' : 'Review'}
                      </Button>
                    )}
                  </div>
                </div>
              </>
            )}

            {step === 'review' && proposal && (
              <div className={styles.card}>
                <Title3>Review proposal</Title3>
                {!pending && (
                  <MessageBar intent="info"><MessageBarBody>This proposal was already {proposal.state}. It can no longer be changed.</MessageBarBody></MessageBar>
                )}
                {generationStatus && (
                  <MessageBar intent={generationStatus.state === 'completed' ? 'info' : 'warning'}>
                    <MessageBarBody>
                      {generationStatus.state === 'completed' && 'Generated draft is ready. Review every member before confirming.'}
                      {generationStatus.state === 'pending' && 'The proposal is still generating. It cannot be confirmed yet.'}
                      {generationStatus.state === 'failed' && 'Generation did not finish. Reject the proposal and try again.'}
                      {generationStatus.state === 'stale' && 'The project changed before generation finished. Start again with the latest configuration.'}
                      {generationStatus.state === 'cancelled' && 'Generation was cancelled.'}
                    </MessageBarBody>
                    {generationStatus.state === 'pending' && pendingGeneration && (
                      <div className={styles.navRow}>
                        <Button appearance="secondary" disabled={busy} onClick={() => void continueCastingGeneration()}>
                          Check again
                        </Button>
                        {generationNeedsSignIn && pendingGeneration.runId && (
                          <Button appearance="primary" disabled={busy} onClick={() => {
                            const runId = pendingGeneration.runId;
                            if (runId) void authorize({ projectId: pendingGeneration.projectId, runId });
                          }}>Sign in</Button>
                        )}
                      </div>
                    )}
                  </MessageBar>
                )}
                {pending && configuration && proposal.baseConfigurationRevision !== configuration.revision && (
                  <MessageBar intent="warning">
                    <MessageBarBody>The project configuration changed after this proposal was made. Confirming it may be refused.</MessageBarBody>
                  </MessageBar>
                )}
                {generatedDraftReady && (
                  <>
                    <Text className={styles.muted}>
                      {augmentProposal
                        ? `This proposal adds ${addedMembers.length} member${addedMembers.length === 1 ? '' : 's'} to the current team.`
                        : `This proposal sets the team to ${members.length} member${members.length === 1 ? '' : 's'}.`}
                    </Text>
                    <div className={styles.memberList} aria-label="Proposed members">
                      {members.map((member) => (
                        <div key={member.agentId} className={styles.memberCard}>
                          <div className={styles.memberHeader}>
                            <div>
                              <Text className={styles.memberName}>{member.name}</Text>
                              <Text className={styles.muted}> — {member.role}</Text>
                            </div>
                            {canEditProposal && (!augmentProposal || !existingAgentIds.has(member.agentId)) && (
                              <Button appearance="subtle" size="small" disabled={busy} aria-label={`Remove ${member.name}`} onClick={() => void removeMember(member.agentId)}>
                                Remove
                              </Button>
                            )}
                          </div>
                          {member.charter && (
                            <>
                              <Button appearance="transparent" size="small" className={styles.charterToggle} onClick={() => toggleCharter(member.agentId)}>
                                {expanded.has(member.agentId) ? 'Hide charter' : 'View charter'}
                              </Button>
                              {expanded.has(member.agentId) && <pre className={styles.charterFull}>{member.charter}</pre>}
                            </>
                          )}
                        </div>
                      ))}
                    </div>
                  </>
                )}
                {errorBar}
                <div className={styles.navRow}>
                  {/* Back discards a proposal made on this page, so it does not stay pending. */}
                  {pending && createdHere && <Button appearance="secondary" disabled={busy} onClick={() => void reject(() => setStep('cast'))}>Back</Button>}
                  {pending
                    ? <Button appearance="secondary" disabled={busy} onClick={() => void reject(() => navigate(agentsPath(projectId)))}>Reject</Button>
                    : <Button appearance="secondary" onClick={() => navigate(agentsPath(projectId))}>Back to Agents</Button>}
                  {canEditProposal && (
                    <Button appearance="primary" disabled={busy} onClick={() => setStep('confirm')}>Confirm</Button>
                  )}
                </div>
              </div>
            )}

            {step === 'confirm' && proposal && (
              <div className={styles.card}>
                <Title3>Cast team</Title3>
                <Text>
                  {augmentProposal
                    ? `You are about to add ${addedMembers.length} member${addedMembers.length === 1 ? '' : 's'} to the current team.`
                    : `You are about to set the team to ${members.length} member${members.length === 1 ? '' : 's'}.`}
                  {currentCount > 0 && ' Runs that already started keep their accepted team.'}
                </Text>
                {errorBar}
                <div className={styles.navRow}>
                  <Button appearance="secondary" disabled={busy} onClick={() => setStep('review')}>Back</Button>
                  <Button appearance="secondary" disabled={busy} onClick={() => void reject(() => navigate(agentsPath(projectId)))}>Reject</Button>
                  <Button appearance="primary" disabled={busy || !canEditProposal} onClick={() => void confirm()}>{busy ? 'Casting' : 'Cast team'}</Button>
                  {busy && <Spinner size="extra-tiny" aria-hidden="true" />}
                </div>
              </div>
            )}
          </div>
        </div>
      )}
    </>
  );
}
