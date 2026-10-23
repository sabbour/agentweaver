/**
 * 0.x "Cast a team" wizard on v1 casting proposals. Step 1 picks a template or
 * roles and stages a proposal. Step 2 reviews the proposed agents and can
 * remove members. Step 3 confirms it. A proposal changes nothing until it is
 * confirmed. The Universe picker uses the Projects & Config catalog and saves
 * its choice in the revision-bound proposal.
 */
import {
  Accordion, AccordionHeader, AccordionItem, AccordionPanel, Button, Checkbox, MessageBar, MessageBarBody,
  Select, Spinner, Text, Title3, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components';
import { CheckmarkRegular } from '@fluentui/react-icons';
import { useCallback, useEffect, useState } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { PageSection } from '../components/ui/PageSection';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import { agentsPath, castableRoles, castingError, draftMembers, draftWithoutMember, isConflict } from './castingData';
import type {
  ProjectCastingProposal, ProjectCastingRoleDefinition, ProjectCastingTemplateDefinition, VersionedProjectConfiguration,
} from './contracts';
import { ErrorNotice, Loading, PageHeading } from './pageParts';

type Step = 'cast' | 'review' | 'confirm';
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

/** Route page for `/projects/:projectId/team/cast`. `?proposal=` opens a proposal for review. */
export function CastTeamPage() {
  const styles = useStyles();
  const { projectId = '' } = useParams();
  const [searchParams] = useSearchParams();
  const linkedProposalId = searchParams.get('proposal');
  const navigate = useNavigate();
  const { apiCall } = useAuth();

  const [step, setStep] = useState<Step>('cast');
  const [templates, setTemplates] = useState<ProjectCastingTemplateDefinition[]>([]);
  const [roles, setRoles] = useState<ProjectCastingRoleDefinition[]>([]);
  const [configuration, setConfiguration] = useState<VersionedProjectConfiguration | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [selectedTemplateId, setSelectedTemplateId] = useState('');
  const [selectedRoleIds, setSelectedRoleIds] = useState<string[]>([]);
  const [universes, setUniverses] = useState<string[]>([]);
  const [universe, setUniverse] = useState('');
  const [proposal, setProposal] = useState<ProjectCastingProposal | null>(null);
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

  /** Stages a proposal from the template, or from the checked roles when they differ from it. */
  const createProposal = () => write(async () => {
    if (!configuration) return;
    const expectedConfigurationRevision = configuration.revision;
    const created = await apiCall((token, tenant) => gatewayClient.createCastingProposal(token, projectId, !rolesOverridden && selectedTemplate
      ? { mode: 'scenario', expectedConfigurationRevision, templateId: selectedTemplate.id, universe: universe || null }
      : { mode: 'manual', expectedConfigurationRevision, roleIds: selectedRoleIds, universe: universe || null }, tenant), null);
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
                  <Text className={styles.muted}>
                    This project already has {currentCount} member{currentCount === 1 ? '' : 's'}. A cast proposal replaces the whole team. Use Add member on the Agents page to add one member instead.
                  </Text>
                )}
                {errorBar}
                <div className={styles.castNavRow}>
                  <Button appearance="secondary" onClick={() => navigate(agentsPath(projectId))}>Cancel</Button>
                  <div className={styles.navRow}>
                    {busy && <Spinner size="extra-tiny" aria-hidden="true" />}
                    <Button appearance="primary" disabled={!canReview || busy} onClick={() => void createProposal()}>
                      {busy ? 'Casting' : 'Review'}
                    </Button>
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
                {pending && configuration && proposal.baseConfigurationRevision !== configuration.revision && (
                  <MessageBar intent="warning">
                    <MessageBarBody>The project configuration changed after this proposal was made. Confirming it may be refused.</MessageBarBody>
                  </MessageBar>
                )}
                <Text className={styles.muted}>
                  The current team has {currentCount} member{currentCount === 1 ? '' : 's'}. This proposal sets the team to {members.length} member{members.length === 1 ? '' : 's'}.
                </Text>
                <div className={styles.memberList} aria-label="Proposed members">
                  {members.map((member) => (
                    <div key={member.agentId} className={styles.memberCard}>
                      <div className={styles.memberHeader}>
                        <div>
                          <Text className={styles.memberName}>{member.name}</Text>
                          <Text className={styles.muted}> — {member.role}</Text>
                        </div>
                        {pending && (
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
                {errorBar}
                <div className={styles.navRow}>
                  {/* Back discards a proposal made on this page, so it does not stay pending. */}
                  {pending && createdHere && <Button appearance="secondary" disabled={busy} onClick={() => void reject(() => setStep('cast'))}>Back</Button>}
                  {pending
                    ? <Button appearance="secondary" disabled={busy} onClick={() => void reject(() => navigate(agentsPath(projectId)))}>Reject</Button>
                    : <Button appearance="secondary" onClick={() => navigate(agentsPath(projectId))}>Back to Agents</Button>}
                  {pending && (
                    <Button appearance="primary" disabled={members.length === 0 || busy} onClick={() => setStep('confirm')}>Confirm</Button>
                  )}
                </div>
              </div>
            )}

            {step === 'confirm' && proposal && (
              <div className={styles.card}>
                <Title3>Cast team</Title3>
                <Text>
                  You are about to set the team to {members.length} member{members.length === 1 ? '' : 's'}.
                  {currentCount > 0 && ' The current team is replaced. Runs that already started keep their team.'}
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
