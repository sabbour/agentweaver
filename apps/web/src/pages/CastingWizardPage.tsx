import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import {
  Button,
  Field,
  MessageBar,
  MessageBarBody,
  Radio,
  RadioGroup,
  Select,
  Spinner,
  Text,
  Textarea,
  Title2,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
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
  templateCard: {
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusSmall,
    cursor: 'pointer',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
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
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  templateTitle: {
    fontWeight: tokens.fontWeightSemibold,
    display: 'block',
  },
  templateDesc: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
    display: 'block',
  },
  templateList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
});

type Mode = 'scenario' | 'free_text' | 'analysis';
type Step = 'mode' | 'configure' | 'review' | 'confirm';

const STEPS: Step[] = ['mode', 'configure', 'review', 'confirm'];
const STEP_LABELS: Record<Step, string> = {
  mode: 'Choose mode',
  configure: 'Configure',
  review: 'Review proposal',
  confirm: 'Confirm',
};

export function CastingWizardPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();
  const navigate = useNavigate();

  // Step state
  const [step, setStep] = useState<Step>('mode');

  // Mode selection
  const [mode, setMode] = useState<Mode>('scenario');

  // Configure state
  const [templates, setTemplates] = useState<TeamTemplateDto[]>([]);
  const [templatesLoading, setTemplatesLoading] = useState(false);
  const [selectedTemplateId, setSelectedTemplateId] = useState('');
  const [goal, setGoal] = useState('');
  const [universe, setUniverse] = useState('');

  // Proposal state
  const [proposal, setProposal] = useState<CastProposalDto | null>(null);
  const [proposalLoading, setProposalLoading] = useState(false);
  const [proposalError, setProposalError] = useState<string | null>(null);

  // Charter expand state
  const [expandedCharters, setExpandedCharters] = useState<Set<string>>(new Set());
  const toggleCharter = (name: string) => {
    setExpandedCharters((prev) => {
      const next = new Set(prev);
      if (next.has(name)) next.delete(name); else next.add(name);
      return next;
    });
  };

  // Review / intent state
  const [intent, setIntent] = useState<'augment' | 'recast'>('augment');

  // Confirm state
  const [confirming, setConfirming] = useState(false);
  const [confirmError, setConfirmError] = useState<string | null>(null);

  // Load templates on mount — used in scenario mode configure step
  useEffect(() => {
    let cancelled = false;
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

  const handleCreateProposal = async () => {
    setProposalLoading(true);
    setProposalError(null);
    try {
      const req: CreateProposalRequest = { mode };
      if (mode === 'scenario') req.template_id = selectedTemplateId;
      if (mode === 'free_text') req.goal = goal;
      if (universe.trim()) req.universe = universe.trim();
      const p = await apiClient.createProposal(projectId, req);
      setProposal(p);
      setStep('review');
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

  const handleCancel = async () => {
    if (proposal) {
      try {
        await apiClient.rejectProposal(projectId, proposal.proposal_id);
      } catch {
        // best-effort
      }
    }
    navigate(`/projects/${projectId}/team`);
  };

  const canProceedFromConfigure =
    (mode === 'scenario' && selectedTemplateId !== '') ||
    (mode === 'free_text' && goal.trim() !== '') ||
    mode === 'analysis';

  return (
    <div className={styles.root}>
      <div className={styles.breadcrumb}>
        <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
        <span>/</span>
        <Link to={`/projects/${projectId}/team`} className={styles.breadcrumbLink}>Team</Link>
        <span>/</span>
        <span>Cast</span>
      </div>

      <Title2>Cast team</Title2>

      <div className={styles.stepIndicator}>
        {STEPS.map((s, i) => (
          <span key={s} className={s === step ? styles.stepActive : undefined}>
            {i + 1}. {STEP_LABELS[s]}
          </span>
        ))}
      </div>

      {/* Step 1: Choose mode */}
      {step === 'mode' && (
        <div className={styles.card}>
          <Title3>Choose casting mode</Title3>
          <RadioGroup
            value={mode}
            onChange={(_, data) => setMode(data.value as Mode)}
          >
            <Radio value="scenario" label="Team template — choose from a curated set of team templates" />
            <Radio value="free_text" label="Describe a goal — describe what you want to build" />
            <Radio value="analysis" label="Analyze project — let the system analyze the project and suggest roles" />
          </RadioGroup>
          <div className={styles.navRow}>
            <Button appearance="secondary" onClick={() => void handleCancel()}>Cancel</Button>
            <Button appearance="primary" onClick={() => setStep('configure')}>Next</Button>
          </div>
        </div>
      )}

      {/* Step 2: Configure */}
      {step === 'configure' && (
        <div className={styles.card}>
          <Title3>Configure</Title3>

          {mode === 'scenario' && (
            <>
              {templatesLoading && <Spinner label="Loading templates" />}
              {!templatesLoading && templates.length > 0 && (
                <div className={styles.templateList}>
                  {templates.map((g) => (
                    <div
                      key={g.id}
                      className={selectedTemplateId === g.id ? styles.templateCardSelected : styles.templateCard}
                      onClick={() => setSelectedTemplateId(g.id)}
                      role="button"
                      tabIndex={0}
                      onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') setSelectedTemplateId(g.id); }}
                      aria-pressed={selectedTemplateId === g.id}
                    >
                      <Text className={styles.templateTitle}>{g.title}</Text>
                      <Text className={styles.templateDesc}>{g.description}</Text>
                    </div>
                  ))}
                </div>
              )}
              {!templatesLoading && templates.length === 0 && (
                <Text>No team templates available.</Text>
              )}
            </>
          )}

          {mode === 'free_text' && (
            <Field label="Goal description" required>
              <Textarea
                value={goal}
                onChange={(_, v) => setGoal(v.value)}
                placeholder="Describe what you want to build or accomplish..."
                rows={4}
              />
            </Field>
          )}

          {mode === 'analysis' && (
            <Text>
              The system will analyze your project and suggest an appropriate team composition.
            </Text>
          )}

          <Field label="Universe (optional)">
            <Select
              value={universe}
              onChange={(_, d) => setUniverse(d.value === '__random__' ? '' : d.value)}
            >
              <option value="__random__">Random</option>
              <option value="The Matrix">The Matrix</option>
              <option value="Star Wars">Star Wars</option>
              <option value="Inception">Inception</option>
              <option value="Firefly">Firefly</option>
              <option value="The Office">The Office</option>
              <option value="Breaking Bad">Breaking Bad</option>
              <option value="Dune">Dune</option>
              <option value="Alien">Alien</option>
              <option value="Blade Runner">Blade Runner</option>
              <option value="The Lord of the Rings">The Lord of the Rings</option>
              <option value="Star Trek">Star Trek</option>
              <option value="Harry Potter">Harry Potter</option>
              <option value="The Avengers">The Avengers</option>
              <option value="Battlestar Galactica">Battlestar Galactica</option>
            </Select>
          </Field>

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
              disabled={!canProceedFromConfigure || proposalLoading}
              onClick={() => void handleCreateProposal()}
            >
              {proposalLoading ? 'Generating' : 'Next'}
            </Button>
            {proposalLoading && <Spinner size="extra-tiny" aria-hidden="true" />}
          </div>
        </div>
      )}

      {/* Step 3: Review proposal */}
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

      {/* Step 4: Confirm */}
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
