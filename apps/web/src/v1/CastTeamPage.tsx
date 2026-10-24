/**
 * 0.x "Cast a team" wizard on v1 casting proposals. Step 1 picks a template or
 * roles and stages a proposal. When a team exists, the user can add members or
 * replace it. Step 2 reviews the proposed agents and can remove members. Step 3
 * confirms it. The Universe picker uses the Projects & Config catalog and saves
 * its choice in the revision-bound proposal. Formulate and Analyze use accepted
 * project runs and remain pending until the user confirms them.
 */
import {
  Accordion, AccordionHeader, AccordionItem, AccordionPanel, Button, Checkbox, Field, Input, MessageBar, MessageBarBody,
  Radio, RadioGroup, Select, Spinner, SpinButton, Tab, TabList, Text, Textarea, Title3, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components';
import { CheckmarkRegular } from '@fluentui/react-icons';
import { useCallback, useEffect, useState } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { AppDialog } from '../components/ui/AppDialog';
import { PageSection } from '../components/ui/PageSection';
import { useAuth } from './AuthContext';
import { GatewayError, gatewayClient } from './api';
import { agentsPath, castableRoles, castingError, castingGenerationPrompt, draftMembers, draftWithRoles, draftWithoutMember, isConflict, type DraftMember } from './castingData';
import type {
  AnalyzeCastingProposalRequest, FormulateCastingProposalRequest, ProjectCastingProposal,
  ProjectCastingRoleDefinition, ProjectCastingTemplateDefinition, VersionedProjectConfiguration,
} from './contracts';
import { ErrorNotice, Loading, PageHeading } from './pageParts';

type Step = 'cast' | 'review' | 'confirm';
type CastingIntent = 'augment' | 'recast';
type CastingSource = 'formulate' | 'template' | 'analyze';
type CastingGenerationAttempt = {
  mode: 'formulate' | 'analyze';
  expectedConfigurationRevision: number;
  prompt: string;
  goal: string | null;
  teamSize: number;
  intent: CastingIntent;
  preferredRoleIds: string[];
  universe: string | null;
  createRunIdempotencyKey: string;
  idempotencyKey: string;
  runId?: string;
  sessionId?: string;
};
const STEPS: Step[] = ['cast', 'review', 'confirm'];
const STEP_LABELS: Record<Step, string> = { cast: 'Cast', review: 'Review proposal', confirm: 'Confirm' };

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

/** Keep uncertain server failures replayable; completed invalid output is terminal for this attempt. */
function canRetryGeneration(reason: unknown): boolean {
  // Network errors and transient server responses may hide an accepted request; invalid output cannot succeed on replay.
  if (!(reason instanceof GatewayError)) return true;
  return reason.status === 408 || reason.status === 429 ||
    (reason.status >= 500 && reason.code !== 'casting_generation_output_invalid');
}

/** Reuses the 0.x Re-role fields while applying changes to the open proposal draft. */
function ProposalMemberRoleDialog({ member, roles, disabled, error, onSave }: {
  member: DraftMember;
  roles: ProjectCastingRoleDefinition[];
  disabled: boolean;
  error: string | null;
  onSave: (roleId: string, customRoleTitle: string | null) => Promise<boolean>;
}) {
  const [open, setOpen] = useState(false);
  const [roleId, setRoleId] = useState('');
  const [customRoleTitle, setCustomRoleTitle] = useState('');

  const save = async () => {
    if (!roleId) return;
    // Close only after the owner accepts the revision-bound draft update.
    if (await onSave(roleId, customRoleTitle.trim() || null)) setOpen(false);
  };

  return (
    <AppDialog
      open={open}
      onOpenChange={(next) => {
        setOpen(next);
        if (next) {
          setRoleId(roles.some((role) => role.id === member.role) ? member.role : '');
          setCustomRoleTitle(member.customRoleTitle ?? '');
        }
      }}
      trigger={<Button appearance="subtle" size="small" disabled={disabled}>Re-role</Button>}
      title={`Re-role ${member.name}`}
      description="Choose a catalog role. A custom title changes its label only."
      primaryAction={{ label: disabled ? 'Saving…' : 'Re-role', onClick: () => void save(), disabled: !roleId || disabled, loading: disabled }}
      modalType="alert"
    >
      <div>
        <Field label="New role" required>
          <Select value={roleId} disabled={disabled} onChange={(_, data) => setRoleId(data.value)}>
            <option value="">Select a role</option>
            {roles.map((role) => <option key={role.id} value={role.id}>{role.title}</option>)}
          </Select>
        </Field>
        <Field label="Custom role title (optional)">
          <Input
            value={customRoleTitle}
            maxLength={120}
            disabled={disabled}
            placeholder="Override the role title"
            onChange={(_, data) => setCustomRoleTitle(data.value)}
          />
        </Field>
        {error && <ErrorNotice>{error}</ErrorNotice>}
      </div>
    </AppDialog>
  );
}

/** Route page for `/projects/:projectId/team/cast`. `?proposal=` opens a proposal for review. */
export function CastTeamPage() {
  const styles = useStyles();
  const { projectId = '' } = useParams();
  const [searchParams] = useSearchParams();
  const linkedProposalId = searchParams.get('proposal');
  const navigate = useNavigate();
  const { apiCall, ensureSession, authorize } = useAuth();

  const [step, setStep] = useState<Step>('cast');
  const [source, setSource] = useState<CastingSource>('formulate');
  const [templates, setTemplates] = useState<ProjectCastingTemplateDefinition[]>([]);
  const [roles, setRoles] = useState<ProjectCastingRoleDefinition[]>([]);
  const [configuration, setConfiguration] = useState<VersionedProjectConfiguration | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [selectedTemplateId, setSelectedTemplateId] = useState('');
  const [selectedRoleIds, setSelectedRoleIds] = useState<string[]>([]);
  const [goal, setGoal] = useState('');
  const [teamSize, setTeamSize] = useState(4);
  const [universes, setUniverses] = useState<string[]>([]);
  const [universe, setUniverse] = useState('');
  const [castingIntent, setCastingIntent] = useState<CastingIntent>('augment');
  const [proposalIntent, setProposalIntent] = useState<CastingIntent | null>(null);
  const [proposal, setProposal] = useState<ProjectCastingProposal | null>(null);
  const [generationAttempt, setGenerationAttempt] = useState<CastingGenerationAttempt | null>(null);
  const [needsRunSignIn, setNeedsRunSignIn] = useState(false);
  // True when this page created the proposal, so Back may discard it.
  const [createdHere, setCreatedHere] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());

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
        setGenerationAttempt(null);
        setNeedsRunSignIn(false);
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
  const write = async (operation: () => Promise<void>, onConflict: () => Promise<unknown>): Promise<boolean> => {
    setBusy(true);
    setError(null);
    try {
      await operation();
      return true;
    } catch (reason) {
      if (isConflict(reason)) {
        // Re-read before claiming the latest version is visible; report both failures if that read also fails.
        try {
          await onConflict();
        } catch (refreshReason) {
          setError(`The project or proposal changed, and its latest version could not be loaded. ${castingError(refreshReason, false)}`);
          return false;
        }
      }
      setError(castingError(reason, true));
      return false;
    } finally {
      setBusy(false);
    }
  };

  /** Creates or resumes the accepted run, then asks Gateway to stage its output as a proposal. */
  const executeGeneration = (attempt: CastingGenerationAttempt) => write(async () => {
    try {
      let activeAttempt = attempt;
      if (!activeAttempt.runId || !activeAttempt.sessionId) {
        // Save the exact prompt first; the owner later checks that this is the run's first message.
        const created = await apiCall((token, tenant) => gatewayClient.createProjectRun(
          token,
          projectId,
          {
            firstMessage: activeAttempt.prompt,
            expectedProjectConfigRevision: activeAttempt.expectedConfigurationRevision,
          },
          activeAttempt.createRunIdempotencyKey,
          tenant,
        ), null);
        if (created.binding.projectId !== projectId ||
            created.binding.runId !== created.runId ||
            !created.rootSessionId)
          throw new GatewayError(502, { code: 'owner_contract_invalid' }, 'The service returned a different casting run.');
        activeAttempt = { ...activeAttempt, runId: created.runId, sessionId: created.rootSessionId };
        setGenerationAttempt(activeAttempt);
      }

      const runId = activeAttempt.runId;
      const sessionId = activeAttempt.sessionId;
      if (!runId || !sessionId)
        throw new GatewayError(502, { code: 'owner_contract_invalid' }, 'The casting run is not ready.');
      const binding = { projectId, runId };
      // Keep the run-bound session separate from the unbound project-writer token.
      if (!await ensureSession(binding, false)) {
        setNeedsRunSignIn(true);
        return;
      }
      setNeedsRunSignIn(false);

      const requestBase = {
        expectedConfigurationRevision: activeAttempt.expectedConfigurationRevision,
        runId,
        sessionId,
        idempotencyKey: activeAttempt.idempotencyKey,
        prompt: activeAttempt.prompt,
        teamSize: activeAttempt.teamSize,
        intent: activeAttempt.intent,
        preferredRoleIds: activeAttempt.preferredRoleIds,
        universe: activeAttempt.universe,
      };
      // Gateway uses the run token for Orchestrator and the unbound token only for Projects & Config.
      const generated = await apiCall((projectToken, projectTenant) => apiCall((runToken) =>
        activeAttempt.mode === 'formulate'
          ? gatewayClient.formulateCastingProposal(
            runToken,
            projectToken,
            projectId,
            { ...requestBase, goal: activeAttempt.goal ?? '' } satisfies FormulateCastingProposalRequest,
            projectTenant,
          )
          : gatewayClient.analyzeCastingProposal(
            runToken,
            projectToken,
            projectId,
            requestBase satisfies AnalyzeCastingProposalRequest,
            projectTenant,
          ), binding), null);
      if (generated.projectId !== projectId ||
          generated.baseConfigurationRevision !== activeAttempt.expectedConfigurationRevision ||
          generated.state !== 'pending' ||
          generated.generation?.mode !== activeAttempt.mode ||
          generated.generation.state !== 'completed')
        throw new GatewayError(502, { code: 'owner_contract_invalid' }, 'The service returned a proposal that is not ready for review.');
      // Display only a completed, still-pending owner proposal; confirmation remains a separate action.
      setProposal(generated);
      setProposalIntent(activeAttempt.intent);
      setCreatedHere(true);
      setGenerationAttempt(null);
      setStep('review');
    } catch (reason) {
      // Preserve the same request key whenever the server may have accepted work before the response was lost.
      if (reason instanceof GatewayError) {
        setNeedsRunSignIn(false);
      }
      if (!canRetryGeneration(reason)) setGenerationAttempt(null);
      throw reason;
    }
  }, readConfiguration);

  /** Starts one AI mode with a prompt shared by the browser and Gateway. */
  const startGeneration = (mode: 'formulate' | 'analyze') => {
    if (!configuration || generationAttempt) return;
    // A blank goal is invalid only for Formulate; Analyze gets its signals from the repository.
    const trimmedGoal = mode === 'formulate' ? goal.trim() : null;
    if (mode === 'formulate' && !trimmedGoal) return;
    const intent: CastingIntent = currentCount > 0 ? castingIntent : 'recast';
    // Analyze does not accept hand-picked role preferences in the 0.x flow.
    const existingRoleIds = configuration.configuration.casting
      .slice()
      .sort((left, right) => left.order - right.order)
      .map((item) => item.role)
      .filter((roleId, index, all) => all.indexOf(roleId) === index);
    const preferredRoleIds = mode === 'formulate' ? [...selectedRoleIds] : [];
    const prompt = castingGenerationPrompt({
      mode,
      teamSize,
      intent,
      preferredRoleIds,
      universe: universe || null,
      goal: trimmedGoal,
      existingRoleIds,
      roles,
    });
    const attempt: CastingGenerationAttempt = {
      mode,
      expectedConfigurationRevision: configuration.revision,
      prompt,
      goal: trimmedGoal,
      teamSize,
      intent,
      preferredRoleIds,
      universe: universe || null,
      createRunIdempotencyKey: crypto.randomUUID(),
      idempotencyKey: crypto.randomUUID(),
    };
    setGenerationAttempt(attempt);
    void executeGeneration(attempt);
  };

  /** Cancels a run created for an unfinished generation attempt. */
  const cancelGenerationAttempt = () => write(async () => {
    const runId = generationAttempt?.runId;
    // A run that was never created needs no owner-side cancellation.
    if (runId)
      await apiCall((token, tenant) => gatewayClient.cancelProjectRun(
        token,
        projectId,
        runId,
        crypto.randomUUID(),
        'Casting proposal generation was cancelled.',
        tenant,
      ), null);
    setGenerationAttempt(null);
    setNeedsRunSignIn(false);
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
    if (!proposal) return;
    const updated = await apiCall((token, tenant) => gatewayClient.updateCastingProposal(token, projectId, proposal.proposalId, {
      expectedDraftRevision: proposal.draftRevision,
      draft: draftWithoutMember(proposal.draft, agentId),
    }, tenant), null);
    setProposal(updated);
  }, () => readProposal(proposal!.proposalId));

  /** Updates only the selected member and advances the proposal's draft revision. */
  const reroleMember = (agentId: string, roleId: string, customRoleTitle: string | null) => write(async () => {
    if (!proposal) return;
    // The role endpoint keeps the catalog ID and optional display title separate in the proposal.
    const updated = await apiCall((token, tenant) => gatewayClient.updateCastingProposalMemberRole(
      token,
      projectId,
      proposal.proposalId,
      agentId,
      { expectedDraftRevision: proposal.draftRevision, roleId, customRoleTitle },
      tenant,
    ), null);
    setProposal(updated);
  }, () => readProposal(proposal!.proposalId));

  /** Rejects the proposal, then goes to `next`. */
  const reject = (next: () => void) => write(async () => {
    if (!proposal) return;
    await apiCall((token, tenant) => gatewayClient.transitionCastingProposal(
      token, projectId, proposal.proposalId, 'reject', proposal.draftRevision, tenant,
    ), null);
    setProposal(null);
    next();
  }, () => readProposal(proposal!.proposalId));

  /** Confirms the proposal, which saves it as a new configuration revision. */
  const confirm = () => write(async () => {
    if (!proposal) return;
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
  const canStartGeneration = configuration !== null &&
    (source === 'analyze' || (source === 'formulate' && goal.trim().length > 0)) &&
    !generationAttempt;
  const existingAgentIds = new Set(configuration?.configuration.casting.map((item) => item.agentId) ?? []);
  const addedMembers = members.filter((member) => !existingAgentIds.has(member.agentId));
  // Linked proposals do not retain their original action, so recognize an augment by its unchanged roster plus additions.
  const preservesCurrentRoster = Boolean(proposal && configuration?.configuration.casting.every((existing) =>
    proposal.draft.casting.some((candidate) =>
      candidate.agentId === existing.agentId && candidate.role === existing.role && candidate.order === existing.order)));
  const augmentProposal = proposalIntent === 'augment' ||
    (proposalIntent === null && currentCount > 0 && addedMembers.length > 0 && preservesCurrentRoster);
  const pending = proposal?.state === 'pending';
  const currentIndex = STEPS.indexOf(step);
  const errorBar = error && <ErrorNotice>{error}</ErrorNotice>;

  return (
    <>
      <PageHeading title="Cast a team" description="Choose roles from a template or pick them yourself, then review the proposed team." page="Cast a team" />
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
                <TabList
                  selectedValue={source}
                  onTabSelect={(_, data) => {
                    if (data.value === 'formulate' || data.value === 'template' || data.value === 'analyze')
                      setSource(data.value);
                  }}
                  aria-label="Casting mode"
                >
                  <Tab value="formulate" disabled={busy || Boolean(generationAttempt)}>Formulate</Tab>
                  <Tab value="template" disabled={busy || Boolean(generationAttempt)}>Template</Tab>
                  <Tab value="analyze" disabled={busy || Boolean(generationAttempt)}>Analyze</Tab>
                </TabList>

                {source === 'formulate' && (
                  <>
                    <PageSection title="Formulate" description="Describe the project goal. The AI suggests roles for a team.">
                      <Field label="Project goal" required>
                        <Textarea
                          value={goal}
                          rows={3}
                          maxLength={4000}
                          disabled={busy || Boolean(generationAttempt)}
                          placeholder="For example, build a small team to ship a SaaS MVP."
                          onChange={(_, data) => setGoal(data.value)}
                        />
                      </Field>
                      <Field label="Team size">
                        <SpinButton
                          min={2}
                          max={10}
                          step={1}
                          value={teamSize}
                          disabled={busy || Boolean(generationAttempt)}
                          onChange={(_, data) => {
                            if (data.value !== undefined && data.value !== null) setTeamSize(data.value);
                            else if (data.displayValue) {
                              const parsed = Number.parseInt(data.displayValue, 10);
                              if (!Number.isNaN(parsed)) setTeamSize(Math.min(10, Math.max(2, parsed)));
                            }
                          }}
                        />
                      </Field>
                      <div>
                        <Text>Preferred roles (optional)</Text>
                        <Text className={styles.muted}>The AI can choose other roles too.</Text>
                        <div className={styles.rolesGrid}>
                          {roles.map((role) => (
                            <Checkbox
                              key={role.id}
                              label={role.title}
                              checked={selectedRoleIds.includes(role.id)}
                              disabled={busy || Boolean(generationAttempt)}
                              onChange={(_, data) => setSelectedRoleIds((previous) => data.checked
                                ? [...previous, role.id]
                                : previous.filter((id) => id !== role.id))}
                            />
                          ))}
                        </div>
                      </div>
                    </PageSection>
                  </>
                )}

                {source === 'analyze' && (
                  <PageSection title="Analyze" description="Use project files to suggest roles. File access is read-only.">
                    <Field label="Team size">
                      <SpinButton
                        min={2}
                        max={10}
                        step={1}
                        value={teamSize}
                        disabled={busy || Boolean(generationAttempt)}
                        onChange={(_, data) => {
                          if (data.value !== undefined && data.value !== null) setTeamSize(data.value);
                          else if (data.displayValue) {
                            const parsed = Number.parseInt(data.displayValue, 10);
                            if (!Number.isNaN(parsed)) setTeamSize(Math.min(10, Math.max(2, parsed)));
                          }
                        }}
                      />
                    </Field>
                  </PageSection>
                )}

                {source === 'template' && (
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
                              disabled={busy}
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
                            disabled={busy}
                            onChange={(_, data) => setSelectedRoleIds((previous) => data.checked
                              ? [...previous, role.id]
                              : previous.filter((id) => id !== role.id))}
                          />
                        ))}
                      </div>
                    </PageSection>
                  </>
                )}
                <Accordion collapsible>
                  <AccordionItem value="universe">
                    <AccordionHeader>Universe</AccordionHeader>
                    <AccordionPanel>
                      <Select
                        aria-label="Universe"
                        value={universe}
                        disabled={busy || Boolean(generationAttempt)}
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
                      disabled={busy || Boolean(generationAttempt)}
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
                {generationAttempt && (
                  <MessageBar intent="info">
                    <MessageBarBody>
                      {needsRunSignIn
                        ? `Sign in to continue this ${generationAttempt.mode === 'formulate' ? 'Formulate' : 'Analyze'} request.`
                        : error
                          ? 'The request did not finish here. Retry uses the same request while the service may still be working.'
                        : `${generationAttempt.mode === 'formulate' ? 'Formulate' : 'Analyze'} is preparing a proposal. The team will not change until you confirm it.`}
                    </MessageBarBody>
                    {needsRunSignIn && generationAttempt.runId && (
                      <Button
                        appearance="primary"
                        size="small"
                        disabled={busy}
                        onClick={() => {
                          // Open sign-in from the user gesture, then resume the same idempotent attempt.
                          const runId = generationAttempt.runId;
                          if (!runId) return;
                          void authorize({ projectId, runId })
                            .then(() => executeGeneration(generationAttempt))
                            .catch((reason: unknown) => setError(castingError(reason, true)));
                        }}
                      >
                        Sign in and continue
                      </Button>
                    )}
                    {error && !needsRunSignIn && !busy && (
                      <Button
                        appearance="primary"
                        size="small"
                        onClick={() => void executeGeneration(generationAttempt)}
                      >
                        Retry
                      </Button>
                    )}
                    {!busy && (
                      <Button appearance="secondary" size="small" onClick={() => void cancelGenerationAttempt()}>
                        Cancel
                      </Button>
                    )}
                  </MessageBar>
                )}
                {currentCount > 0 && (
                  <Text className={styles.muted}>
                    This project has {currentCount} team member{currentCount === 1 ? '' : 's'}. Runs that already started keep their accepted team.
                  </Text>
                )}
                {errorBar}
                <div className={styles.castNavRow}>
                  <Button appearance="secondary" disabled={busy || Boolean(generationAttempt)} onClick={() => navigate(agentsPath(projectId))}>Cancel</Button>
                  <div className={styles.navRow}>
                    {busy && <Spinner size="extra-tiny" aria-hidden="true" />}
                    {source === 'template'
                      ? <Button appearance="primary" disabled={!canReview || busy} onClick={() => void createProposal()}>
                        {busy ? 'Casting' : 'Review proposal'}
                      </Button>
                      : <Button
                        appearance="primary"
                        disabled={!canStartGeneration || busy}
                        onClick={() => startGeneration(source)}
                      >
                        {busy ? 'Preparing proposal' : source === 'formulate' ? 'Formulate' : 'Analyze'}
                      </Button>}
                  </div>
                </div>
              </>
            )}

            {step === 'review' && proposal && (
              <div className={styles.card}>
                <Title3>Review proposal</Title3>
                {proposal.generation && (
                  <MessageBar intent="info">
                    <MessageBarBody>
                      {proposal.generation.mode === 'formulate' ? 'Formulate' : 'Analyze'} proposal ·
                      {' '}{proposal.generation.state === 'completed' ? 'ready to review' : proposal.generation.state}
                    </MessageBarBody>
                  </MessageBar>
                )}
                {!pending && (
                  <MessageBar intent="info"><MessageBarBody>This proposal was already {proposal.state}. It can no longer be changed.</MessageBarBody></MessageBar>
                )}
                {pending && configuration && proposal.baseConfigurationRevision !== configuration.revision && (
                  <MessageBar intent="warning">
                    <MessageBarBody>The project configuration changed after this proposal was made. Confirming it may be refused.</MessageBarBody>
                  </MessageBar>
                )}
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
                          <Text className={styles.muted}> — {member.customRoleTitle || roles.find((role) => role.id === member.role)?.title || member.role}</Text>
                        </div>
                        {pending && (
                          <div className={styles.navRow}>
                            <ProposalMemberRoleDialog
                              member={member}
                              roles={roles}
                              disabled={busy}
                              error={error}
                              onSave={(roleId, customRoleTitle) => reroleMember(member.agentId, roleId, customRoleTitle)}
                            />
                            {(!augmentProposal || !existingAgentIds.has(member.agentId)) && (
                              <Button appearance="subtle" size="small" disabled={busy} aria-label={`Remove ${member.name}`} onClick={() => void removeMember(member.agentId)}>
                                Remove
                              </Button>
                            )}
                          </div>
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
                {errorBar}
                <div className={styles.navRow}>
                  {/* Back discards a proposal made on this page, so it does not stay pending. */}
                  {pending && createdHere && <Button appearance="secondary" disabled={busy} onClick={() => void reject(() => setStep('cast'))}>Back</Button>}
                  {pending
                    ? <Button appearance="secondary" disabled={busy} onClick={() => void reject(() => navigate(agentsPath(projectId)))}>Reject</Button>
                    : <Button appearance="secondary" onClick={() => navigate(agentsPath(projectId))}>Back to Agents</Button>}
                  {pending && (
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
                  <Button appearance="primary" disabled={busy || !pending} onClick={() => void confirm()}>{busy ? 'Casting' : 'Cast team'}</Button>
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
