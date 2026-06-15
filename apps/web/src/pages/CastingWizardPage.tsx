import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import {
  Button,
  MessageBar,
  MessageBarBody,
  Radio,
  RadioGroup,
  Spinner,
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
  ArrowShuffleRegular,
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

const UNIVERSE_POOLS: { name: string; count: number }[] = [
  { name: 'The Matrix', count: 13 },
  { name: 'Star Wars', count: 13 },
  { name: 'Inception', count: 9 },
  { name: 'Firefly', count: 12 },
  { name: 'The Office', count: 16 },
  { name: 'Breaking Bad', count: 12 },
  { name: 'Dune', count: 12 },
  { name: 'Alien', count: 13 },
  { name: 'Blade Runner', count: 10 },
  { name: 'The Lord of the Rings', count: 13 },
  { name: 'Star Trek', count: 13 },
  { name: 'Harry Potter', count: 12 },
  { name: 'The Avengers', count: 12 },
  { name: 'Battlestar Galactica', count: 12 },
];

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
  panelActive: {
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorBrandBackground2,
    border: `2px solid ${tokens.colorBrandStroke1}`,
    borderRadius: tokens.borderRadiusMedium,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    cursor: 'default',
  },
  panelInactive: {
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    cursor: 'pointer',
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground2,
    },
  },
  panelHeading: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase400,
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
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
  panelRationale: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
    paddingTop: tokens.spacingVerticalS,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  panelsContainer: {
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
  universeSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  universeSectionLabel: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase400,
  },
  universeGrid: {
    display: 'grid',
    gridTemplateColumns: '1fr 1fr',
    gap: tokens.spacingHorizontalM,
  },
  universeCard: {
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusSmall,
    cursor: 'pointer',
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground3,
    },
  },
  universeCardSelected: {
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorBrandBackground2,
    border: `2px solid ${tokens.colorBrandStroke1}`,
    borderRadius: tokens.borderRadiusSmall,
    cursor: 'pointer',
  },
  universeCardName: {
    fontWeight: tokens.fontWeightSemibold,
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
  },
  universeCardCount: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
    display: 'block',
  },
});

type Step = 'cast' | 'review' | 'confirm';
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
    setTemplatesLoading(true);
    apiClient.getTemplates()
      .then((data) => { if (!cancelled) setTemplates(data); })
      .catch(() => { if (!cancelled) setTemplates([]); })
      .finally(() => { if (!cancelled) setTemplatesLoading(false); });
    return () => { cancelled = true; };
  }, []);

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
      const req: CreateProposalRequest = { mode: 'free_text', goal };
      if (universe) req.universe = universe;
      const p = await apiClient.createProposal(projectId, req);
      setFormulateProposal(p);
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
      const p = await apiClient.createProposal(projectId, req);
      setAnalyzeProposal(p);
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
          <div className={styles.panelsContainer}>
            {/* Formulate with AI */}
            <div
              className={activePanel === 'formulate' ? styles.panelActive : styles.panelInactive}
              onClick={() => setActivePanel('formulate')}
            >
              <div className={styles.panelHeading}><SparkleRegular />Formulate with AI</div>
              <Text className={styles.panelDesc}>
                Sketch the team in plain language; AI picks a universe, team size, and required roles.
              </Text>
              <Textarea
                value={goal}
                onChange={(_, v) => setGoal(v.value)}
                placeholder="e.g. a small team of 3 to ship a SaaS MVP fast..."
                rows={3}
              />
              {formulateError && (
                <MessageBar intent="error">
                  <MessageBarBody>{formulateError}</MessageBarBody>
                </MessageBar>
              )}
              <div className={styles.panelActionRow}>
                {formulateLoading && <Spinner size="extra-tiny" aria-hidden="true" />}
                <Button
                  appearance="primary"
                  size="small"
                  disabled={goal.trim() === '' || formulateLoading}
                  onClick={(e) => { e.stopPropagation(); void handleFormulate(); }}
                >
                  {formulateLoading ? 'Formulating' : 'Formulate \u2192'}
                </Button>
              </div>
              {formulateProposal && (
                <Text className={styles.panelRationale}>
                  <strong>Why this team:</strong>{' '}
                  {formulateProposal.warnings.length > 0
                    ? formulateProposal.warnings[0]
                    : 'Team formulated.'}
                </Text>
              )}
            </div>

            {/* Start from template */}
            <div
              className={activePanel === 'template' ? styles.panelActive : styles.panelInactive}
              onClick={() => setActivePanel('template')}
            >
              <div className={styles.panelHeading}><DocumentBulletListRegular />Start from template</div>
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
                      onClick={(e) => {
                        e.stopPropagation();
                        setActivePanel('template');
                        setSelectedTemplateId(t.id);
                      }}
                      role="button"
                      tabIndex={0}
                      onKeyDown={(e) => {
                        if (e.key === 'Enter' || e.key === ' ') {
                          setActivePanel('template');
                          setSelectedTemplateId(t.id);
                        }
                      }}
                      aria-pressed={selectedTemplateId === t.id}
                    >
                      <Text className={styles.templateTitle}>{t.title}</Text>
                    </div>
                  ))}
                </div>
              )}
            </div>

            {/* Analyze project */}
            <div
              className={activePanel === 'analyze' ? styles.panelActive : styles.panelInactive}
              onClick={() => setActivePanel('analyze')}
            >
              <div className={styles.panelHeading}><SearchRegular />Analyze project</div>
              <Text className={styles.panelDesc}>
                The system will analyze your project and suggest roles.
              </Text>
              {analyzeError && (
                <MessageBar intent="error">
                  <MessageBarBody>{analyzeError}</MessageBarBody>
                </MessageBar>
              )}
              <div className={styles.panelActionRow}>
                {analyzeLoading && <Spinner size="extra-tiny" aria-hidden="true" />}
                <Button
                  appearance="primary"
                  size="small"
                  disabled={analyzeLoading}
                  onClick={(e) => { e.stopPropagation(); void handleAnalyze(); }}
                >
                  {analyzeLoading ? 'Analyzing' : 'Analyze \u2192'}
                </Button>
              </div>
              {analyzeProposal && (
                <Text className={styles.panelRationale}>
                  Analysis complete.{' '}
                  {analyzeProposal.members.length} role{analyzeProposal.members.length !== 1 ? 's' : ''} suggested.
                </Text>
              )}
            </div>
          </div>

          {/* Universe section */}
          <div className={styles.universeSection}>
            <Text className={styles.universeSectionLabel}>Universe</Text>
            <div className={styles.universeGrid}>
              <div
                className={styles.universeCard}
                onClick={() => setUniverse('')}
                role="button"
                tabIndex={0}
                onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') setUniverse(''); }}
                aria-pressed={universe === ''}
              >
                <div className={styles.universeCardName}><ArrowShuffleRegular />Random</div>
                <Text className={styles.universeCardCount}>Any universe</Text>
              </div>
              {UNIVERSE_POOLS.map((u) => (
                <div
                  key={u.name}
                  className={universe === u.name ? styles.universeCardSelected : styles.universeCard}
                  onClick={() => setUniverse(u.name)}
                  role="button"
                  tabIndex={0}
                  onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') setUniverse(u.name); }}
                  aria-pressed={universe === u.name}
                >
                  <Text className={styles.universeCardName}>{u.name}</Text>
                  <Text className={styles.universeCardCount}>{u.count} characters available</Text>
                </div>
              ))}
            </div>
          </div>

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
                {castLoading ? 'Casting' : 'Cast Team \u2192'}
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
              Next
            </Button>
          </div>
        </div>
      )}

      {/* Step 3: Confirm */}
      {step === 'confirm' && proposal && (
        <div className={styles.card}>
          <Title3>Create team</Title3>
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
              {confirming ? 'Creating' : 'Create team'}
            </Button>
            {confirming && <Spinner size="extra-tiny" aria-hidden="true" />}
          </div>
        </div>
      )}
    </div>
  );
}