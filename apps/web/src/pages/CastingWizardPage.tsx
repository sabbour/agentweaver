import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import {
  Accordion,
  AccordionHeader,
  AccordionItem,
  AccordionPanel,
  Button,
  Checkbox,
  Field,
  MessageBar,
  MessageBarBody,
  Radio,
  RadioGroup,
  Select,
  SpinButton,
  Spinner,
  Tab,
  TabList,
  Text,
  Textarea,
  Title2,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import {
  SparkleRegular,
  DocumentBulletListRegular,
  SearchRegular,
} from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type {
  TeamTemplateDto,
  CastProposalDto,
  ProposedMemberDto,
  CreateProposalRequest,
  ConfirmProposalRequest,
} from '../api/types';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    maxWidth: '760px',
  },
  breadcrumb: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
  },
  breadcrumbLink: {
    color: tokens.colorBrandForeground1,
    textDecoration: 'none',
  },
  card: {
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  stepIndicator: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  stepActive: {
    color: tokens.colorBrandForeground1,
    fontWeight: tokens.fontWeightSemibold,
  },
  navRow: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
    justifyContent: 'flex-end',
  },
  castNavRow: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  castNavRowRight: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
  },
  memberCard: {
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusSmall,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  memberHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  memberName: {
    fontWeight: tokens.fontWeightSemibold,
  },
  memberRole: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
  },
  memberList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  memberSummary: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
    paddingTop: tokens.spacingVerticalXS,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  charterToggle: {
    alignSelf: 'flex-start',
    color: tokens.colorBrandForeground1,
    fontSize: tokens.fontSizeBase200,
    padding: '0',
    minWidth: 'unset',
  },
  charterFull: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    whiteSpace: 'pre-wrap',
    maxHeight: '200px',
    overflow: 'auto',
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    paddingTop: tokens.spacingVerticalXS,
    margin: '0',
  },
  panelDesc: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
  },
  panelActionRow: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
    justifyContent: 'flex-end',
  },
  rationaleBox: {
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground2,
    borderRadius: tokens.borderRadiusMedium,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  rationaleLabel: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
    textTransform: 'uppercase' as const,
    letterSpacing: '0.05em',
  },
  teamSizeRow: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'flex-start',
  },
  tabContent: {
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: tokens.spacingVerticalM,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  templateGrid: {
    display: 'grid',
    gridTemplateColumns: '1fr 1fr',
    gap: tokens.spacingHorizontalM,
  },
  templateCard: {
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusSmall,
    cursor: 'pointer',
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground3,
    },
  },
  templateCardSelected: {
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorBrandBackground2,
    border: `2px solid ${tokens.colorBrandStroke1}`,
    borderRadius: tokens.borderRadiusSmall,
    cursor: 'pointer',
  },
  templateTitle: {
    fontWeight: tokens.fontWeightSemibold,
    display: 'block',
  },
  roleGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(180px, 1fr))',
    gap: tokens.spacingVerticalS,
  },
  rolesSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  rolesSectionLabel: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
  },
  rolesGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(160px, 1fr))',
    gap: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`,
  },
});

type Step = 'cast' | 'review' | 'confirm';

function buildRationale(proposal: CastProposalDto | null, selectedTemplate: TeamTemplateDto | null): string {
  if (!proposal) return '';
  if (proposal.rationale) return proposal.rationale;
  // Fallback for scenario mode (template description from frontend)
  if (proposal.mode === 'scenario' && selectedTemplate?.description)
    return selectedTemplate.description;
  return '';
}
type ActivePanel = 'formulate' | 'template' | 'analyze';

const STEPS: Step[] = ['cast', 'review', 'confirm'];
const STEP_LABELS: Record<Step, string> = {
  cast: 'Cast',
  review: 'Review proposal',
  confirm: 'Confirm',
};

export function CastingWizardPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();
  const navigate = useNavigate();

  const [step, setStep] = useState<Step>('cast');

  // Cast step
  const [activePanel, setActivePanel] = useState<ActivePanel>('formulate');
  const [goal, setGoal] = useState('');
  const [universe, setUniverse] = useState('');
  const [selectedTemplateId, setSelectedTemplateId] = useState('');
  const [teamSize, setTeamSize] = useState(4);

  // Formulate panel
  const [formulateProposal, setFormulateProposal] = useState<CastProposalDto | null>(null);
  const [formulateLoading, setFormulateLoading] = useState(false);
  const [formulateError, setFormulateError] = useState<string | null>(null);

  // Analyze panel
  const [analyzeProposal, setAnalyzeProposal] = useState<CastProposalDto | null>(null);
  const [analyzeLoading, setAnalyzeLoading] = useState(false);
  const [analyzeError, setAnalyzeError] = useState<string | null>(null);

  // Template cast loading
  const [castLoading, setCastLoading] = useState(false);
  const [castError, setCastError] = useState<string | null>(null);

  // Templates data
  const [templates, setTemplates] = useState<TeamTemplateDto[]>([]);
  const [templatesLoading, setTemplatesLoading] = useState(true);

  // Universes (fetched from backend policy)
  const [universes, setUniverses] = useState<string[]>([]);

  // Configure panel
  const [selectedRoleIds, setSelectedRoleIds] = useState<string[]>([]);

  // Proposal (review / confirm)
  const [proposal, setProposal] = useState<CastProposalDto | null>(null);
  const [proposalLoading, setProposalLoading] = useState(false);
  const [proposalError, setProposalError] = useState<string | null>(null);

  // Charter expand
  const [expandedCharters, setExpandedCharters] = useState<Set<string>>(new Set());
  const toggleCharter = (name: string) => {
    setExpandedCharters((prev) => {
      const next = new Set(prev);
      if (next.has(name)) next.delete(name); else next.add(name);
      return next;
    });
  };

  // Review intent
  const [intent, setIntent] = useState<'augment' | 'recast'>('augment');

  // Confirm
  const [confirming, setConfirming] = useState(false);
  const [confirmError, setConfirmError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    apiClient.getTemplates()
      .then((data) => { if (!cancelled) setTemplates(data); })
      .catch(() => { if (!cancelled) setTemplates([]); })
      .finally(() => { if (!cancelled) setTemplatesLoading(false); });
    return () => { cancelled = true; };
  }, []);

  useEffect(() => {
    if (!projectId) return;
    let cancelled = false;
    apiClient.getUniverses(projectId)
      .then((data) => { if (!cancelled) setUniverses(data.universes); })
      .catch(() => { /* leave universes empty; dropdown will be hidden */ });
    return () => { cancelled = true; };
  }, [projectId]);

  if (!projectId) return null;

  const goBack = () => {
    const idx = STEPS.indexOf(step);
    if (idx > 0) setStep(STEPS[idx - 1]);
    else navigate(`/projects/${projectId}/team`);
  };

  const handleCancel = async () => {
    if (proposal) {
      try { await apiClient.rejectProposal(projectId, proposal.proposal_id); } catch { /* best-effort */ }
    }
    navigate(`/projects/${projectId}/team`);
  };

  const handleFormulate = async () => {
    setFormulateLoading(true);
    setFormulateError(null);
    try {
      let composedGoal = goal;
      if (selectedRoleIds.length > 0) {
        const roleTitles = selectedRoleIds
          .map(id => allCatalogRoles.find(r => r.id === id)?.title ?? id)
          .join(', ');
        composedGoal += `\n\nPreferred roles: ${roleTitles}`;
      }
      const req: CreateProposalRequest = { mode: 'free_text', goal: composedGoal };
      if (universe) req.universe = universe;
      if (teamSize !== 4) req.team_size = teamSize;
      const p = await apiClient.createProposal(projectId, req);
      setFormulateProposal(p);
      setSelectedRoleIds(p.members.map((m) => m.role.id));
    } catch (err) {
      setFormulateError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error ? err.message : String(err),
      );
    } finally {
      setFormulateLoading(false);
    }
  };

  const handleAnalyze = async () => {
    setAnalyzeLoading(true);
    setAnalyzeError(null);
    try {
      const req: CreateProposalRequest = { mode: 'analysis' };
      if (universe) req.universe = universe;
      if (teamSize !== 4) req.team_size = teamSize;
      const p = await apiClient.createProposal(projectId, req);
      setAnalyzeProposal(p);
      setSelectedRoleIds(p.members.map((m) => m.role.id));
    } catch (err) {
      setAnalyzeError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error ? err.message : String(err),
      );
    } finally {
      setAnalyzeLoading(false);
    }
  };

  const handleCastTeam = async () => {
    if (activePanel === 'formulate' && formulateProposal) {
      setProposal(formulateProposal);
      setStep('review');
      return;
    }
    if (activePanel === 'analyze' && analyzeProposal) {
      setProposal(analyzeProposal);
      setStep('review');
      return;
    }
    if (activePanel === 'template' && selectedTemplateId) {
      setCastLoading(true);
      setCastError(null);
      try {
        const req: CreateProposalRequest = { mode: 'scenario', template_id: selectedTemplateId };
        if (universe) req.universe = universe;
        const p = await apiClient.createProposal(projectId, req);
        setProposal(p);
        setStep('review');
      } catch (err) {
        setCastError(
          err instanceof ApiError
            ? `API error ${err.status}: ${err.body}`
            : err instanceof Error ? err.message : String(err),
        );
      } finally {
        setCastLoading(false);
      }
    }
  };

  const canCastTeam =
    (activePanel === 'formulate' && formulateProposal !== null) ||
    (activePanel === 'template' && selectedTemplateId !== '') ||
    (activePanel === 'analyze' && analyzeProposal !== null);

  const handleRemoveMember = async (member: ProposedMemberDto) => {
    if (!proposal) return;
    setProposalLoading(true);
    setProposalError(null);
    try {
      const updated = await apiClient.amendProposal(projectId, proposal.proposal_id, {
        members: proposal.members.filter((m) => m.proposed_name !== member.proposed_name),
      });
      setProposal(updated);
    } catch (err) {
      setProposalError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error ? err.message : String(err),
      );
    } finally {
      setProposalLoading(false);
    }
  };


  const handleConfirm = async () => {
    if (!proposal) return;
    setConfirming(true);
    setConfirmError(null);
    try {
      const req: ConfirmProposalRequest = {};
      if (proposal.existing_team_present) req.intent = intent;
      await apiClient.confirmProposal(projectId, proposal.proposal_id, req);
      navigate(`/projects/${projectId}/team`);
    } catch (err) {
      setConfirmError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error ? err.message : String(err),
      );
    } finally {
      setConfirming(false);
    }
  };

  const allCatalogRoles = templates.flatMap((t) => t.roles).filter(
    (r, i, arr) => arr.findIndex((x) => x.id === r.id) === i,
  );

  const selectedTemplate: TeamTemplateDto | null = templates.find((t) => t.id === selectedTemplateId) ?? null;

  const castRationale = buildRationale(
    activePanel === 'formulate' ? formulateProposal :
    activePanel === 'analyze' ? analyzeProposal : null,
    selectedTemplate,
  );

  return (
    <div className={styles.root}>
      <div className={styles.breadcrumb}>
        <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
        <span>/</span>
        <Link to={`/projects/${projectId}/team`} className={styles.breadcrumbLink}>Team</Link>
        <span>/</span>
        <span>Cast</span>
      </div>

      <Title2>Cast a team</Title2>

      <div className={styles.stepIndicator}>
        {STEPS.map((s, i) => (
          <span key={s} className={s === step ? styles.stepActive : undefined}>
            {i + 1}. {STEP_LABELS[s]}
          </span>
        ))}
      </div>

      {/* Step 1: Cast */}
      {step === 'cast' && (
        <>
          <TabList
            selectedValue={activePanel}
            onTabSelect={(_, data) => setActivePanel(data.value as ActivePanel)}
          >
            <Tab icon={<SparkleRegular />} value="formulate">Formulate</Tab>
            <Tab icon={<DocumentBulletListRegular />} value="template">Template</Tab>
            <Tab icon={<SearchRegular />} value="analyze">Analyze</Tab>
          </TabList>

          <div className={styles.tabContent}>
            {activePanel === 'formulate' && (
              <>
                <Text className={styles.panelDesc}>
                  Sketch the team in plain language; AI picks a universe, team size, and required roles.
                </Text>
                <Textarea
                  value={goal}
                  onChange={(_, v) => setGoal(v.value)}
                  placeholder="e.g. a small team of 3 to ship a SaaS MVP fast..."
                  rows={3}
                />
                <div className={styles.teamSizeRow}>
                  <Field label="Team size">
                    <SpinButton
                      min={2}
                      max={10}
                      step={1}
                      value={teamSize}
                      onChange={(_, data) => {
                        if (data.value !== undefined && data.value !== null) setTeamSize(data.value);
                      }}
                    />
                  </Field>
                </div>
                {formulateError && (
                  <MessageBar intent="error">
                    <MessageBarBody>{formulateError}</MessageBarBody>
                  </MessageBar>
                )}
                <div className={styles.panelActionRow}>
                  {formulateLoading && <Spinner size="extra-tiny" aria-hidden="true" />}
                  <Button
                    appearance="primary"
                    disabled={goal.trim() === '' || formulateLoading}
                    onClick={() => void handleFormulate()}
                  >
                    {formulateLoading ? 'Formulating' : 'Formulate \u2192'}
                  </Button>
                </div>
              </>
            )}

            {activePanel === 'template' && (
              <>
                {templatesLoading && <Spinner label="Loading templates..." size="small" />}
                {!templatesLoading && templates.length === 0 && (
                  <Text className={styles.panelDesc}>No templates available.</Text>
                )}
                {!templatesLoading && templates.length > 0 && (
                  <div className={styles.templateGrid}>
                    {templates.map((t) => (
                      <div
                        key={t.id}
                        className={selectedTemplateId === t.id ? styles.templateCardSelected : styles.templateCard}
                        onClick={() => {
                          setSelectedTemplateId(t.id);
                          setSelectedRoleIds(t.roles.map((r) => r.id));
                        }}
                        role="button"
                        tabIndex={0}
                        onKeyDown={(e) => {
                          if (e.key === 'Enter' || e.key === ' ') {
                            setSelectedTemplateId(t.id);
                            setSelectedRoleIds(t.roles.map((r) => r.id));
                          }
                        }}
                        aria-pressed={selectedTemplateId === t.id}
                      >
                        <Text className={styles.templateTitle}>{t.title}</Text>
                      </div>
                    ))}
                  </div>
                )}
              </>
            )}

            {activePanel === 'analyze' && (
              <>
                <div className={styles.teamSizeRow}>
                  <Field label="Team size">
                    <SpinButton
                      value={teamSize}
                      min={2}
                      max={10}
                      step={1}
                      onChange={(_, data) => {
                        if (data.value !== undefined && data.value !== null) setTeamSize(data.value);
                        else if (data.displayValue) {
                          const n = parseInt(data.displayValue, 10);
                          if (!isNaN(n)) setTeamSize(Math.min(10, Math.max(2, n)));
                        }
                      }}
                    />
                  </Field>
                </div>
                <div style={{ display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalM }}>
                  <Text className={styles.panelDesc} style={{ flex: 1 }}>
                    The system will analyze your project and suggest roles.
                  </Text>
                  {analyzeLoading && <Spinner size="extra-tiny" aria-hidden="true" />}
                  <Button
                    appearance="primary"
                    disabled={analyzeLoading}
                    onClick={() => void handleAnalyze()}
                  >
                    {analyzeLoading ? 'Analyzing' : 'Analyze \u2192'}
                  </Button>
                </div>
                {analyzeError && (
                  <MessageBar intent="error">
                    <MessageBarBody>{analyzeError}</MessageBarBody>
                  </MessageBar>
                )}
              </>
            )}

          </div>

          {castRationale && (
            <div className={styles.rationaleBox}>
              <Text className={styles.rationaleLabel}>Why this team</Text>
              <Text>{castRationale}</Text>
            </div>
          )}

          {/* Roles section */}
          <div className={styles.rolesSection}>
            <Text className={styles.rolesSectionLabel}>Roles</Text>
            {templatesLoading && <Spinner size="extra-tiny" />}
            {!templatesLoading && (
              <div className={styles.rolesGrid}>
                {allCatalogRoles.map((role) => (
                  <Checkbox
                    key={role.id}
                    label={role.title}
                    checked={selectedRoleIds.includes(role.id)}
                    onChange={(_, data) => {
                      setSelectedRoleIds((prev) =>
                        data.checked
                          ? [...prev, role.id]
                          : prev.filter((id) => id !== role.id)
                      );
                    }}
                  />
                ))}
              </div>
            )}
          </div>

          <Accordion collapsible>
            <AccordionItem value="universe">
              <AccordionHeader>Universe</AccordionHeader>
              <AccordionPanel>
                <Select
                  value={universe}
                  onChange={(_, data) => {
                    setUniverse(data.value);
                    setFormulateProposal(null);
                    setAnalyzeProposal(null);
                  }}
                >
                  <option value="">Random (any universe)</option>
                  {universes.map((u) => (
                    <option key={u} value={u}>{u}</option>
                  ))}
                </Select>
              </AccordionPanel>
            </AccordionItem>
          </Accordion>

          {castError && (
            <MessageBar intent="error">
              <MessageBarBody>{castError}</MessageBarBody>
            </MessageBar>
          )}

          <div className={styles.castNavRow}>
            <Button appearance="secondary" onClick={() => void handleCancel()}>Cancel</Button>
            <div className={styles.castNavRowRight}>
              {castLoading && <Spinner size="extra-tiny" aria-hidden="true" />}
              <Button
                appearance="primary"
                disabled={!canCastTeam || castLoading}
                onClick={() => void handleCastTeam()}
              >
                {castLoading ? 'Casting' : 'Review'}
              </Button>
            </div>
          </div>
        </>
      )}

      {/* Step 2: Review proposal */}
      {step === 'review' && proposal && (
        <div className={styles.card}>
          <Title3>Review proposal</Title3>

          {proposal.warnings.length > 0 && proposal.warnings.map((w, i) => (
            <MessageBar key={i} intent="warning">
              <MessageBarBody>{w}</MessageBarBody>
            </MessageBar>
          ))}

          {proposal.existing_team_present && (
            <div>
              <Text>An existing team is present. How would you like to proceed?</Text>
              <RadioGroup
                value={intent}
                onChange={(_, data) => setIntent(data.value as 'augment' | 'recast')}
              >
                <Radio value="augment" label="Augment — add new members to the existing team" />
                <Radio value="recast" label="Recast — replace the existing team" />
              </RadioGroup>
            </div>
          )}

          <div className={styles.memberList}>
            {proposal.members.map((member) => (
              <div key={member.proposed_name} className={styles.memberCard}>
                <div className={styles.memberHeader}>
                  <div>
                    <Text className={styles.memberName}>{member.proposed_name}</Text>
                    <Text className={styles.memberRole}> — {member.role.title}</Text>
                  </div>
                  <Button
                    appearance="subtle"
                    size="small"
                    disabled={proposalLoading}
                    onClick={() => void handleRemoveMember(member)}
                  >
                    Remove
                  </Button>
                </div>
                {member.justification && (
                  <Text className={styles.memberSummary}>{member.justification}</Text>
                )}
                <Text className={styles.memberSummary}>{member.role.summary}</Text>
                <Button
                  appearance="transparent"
                  size="small"
                  className={styles.charterToggle}
                  onClick={() => toggleCharter(member.proposed_name)}
                >
                  {expandedCharters.has(member.proposed_name) ? 'Hide charter' : 'View charter'}
                </Button>
                {expandedCharters.has(member.proposed_name) && (
                  <pre className={styles.charterFull}>{member.charter_markdown}</pre>
                )}
              </div>
            ))}
          </div>

          {proposalError && (
            <MessageBar intent="error">
              <MessageBarBody>{proposalError}</MessageBarBody>
            </MessageBar>
          )}

          <div className={styles.navRow}>
            <Button appearance="secondary" onClick={goBack}>Back</Button>
            <Button appearance="secondary" onClick={() => void handleCancel()}>Cancel</Button>
            <Button
              appearance="primary"
              disabled={proposal.members.length === 0 || proposalLoading}
              onClick={() => setStep('confirm')}
            >
              Confirm
            </Button>
          </div>
        </div>
      )}

      {/* Step 3: Confirm */}
      {step === 'confirm' && proposal && (
        <div className={styles.card}>
          <Title3>Cast team</Title3>
          <Text>
            You are about to create a team with {proposal.members.length} member{proposal.members.length !== 1 ? 's' : ''}.
            {proposal.existing_team_present && intent === 'recast' && ' The existing team will be replaced.'}
            {proposal.existing_team_present && intent === 'augment' && ' New members will be added to the existing team.'}
          </Text>

          {confirmError && (
            <MessageBar intent="error">
              <MessageBarBody>{confirmError}</MessageBarBody>
            </MessageBar>
          )}

          <div className={styles.navRow}>
            <Button appearance="secondary" onClick={goBack}>Back</Button>
            <Button appearance="secondary" onClick={() => void handleCancel()}>Cancel</Button>
            <Button
              appearance="primary"
              disabled={confirming}
              onClick={() => void handleConfirm()}
            >
              {confirming ? 'Casting' : 'Cast team'}
            </Button>
            {confirming && <Spinner size="extra-tiny" aria-hidden="true" />}
          </div>
        </div>
      )}
    </div>
  );
}