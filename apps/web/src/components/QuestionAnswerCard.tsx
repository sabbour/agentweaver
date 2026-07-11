import {
  apiClient } from '../api/apiClient';
import { Badge,
  Button,
  StatusIconText,
  Text,
  Textarea } from '../copilot-fluent-system';
import { makeStyles,
  mergeClasses,
  tokens,
} from '../copilot-fluent-system';
import { CheckmarkCircleFilled, ClockRegular, QuestionCircleFilled, SendRegular } from '../copilot-fluent-system';
import { useState } from 'react';
const useStyles = makeStyles({
  // Mirrors the HITL tool-approval card treatment (brand-stroked, shadowed) so a blocked
  // question reads as an equally prominent operator action.
  card: {
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorBrandStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    marginTop: tokens.spacingVerticalXS,
    marginBottom: tokens.spacingVerticalXS,
    overflow: 'hidden',
    boxShadow: tokens.shadow4,
  },
  header: {
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    backgroundColor: tokens.colorBrandBackground2,
    borderBottom: `1px solid ${tokens.colorBrandStroke2}`,
  },
  body: {
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
  },
  question: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground1,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
  },
  actions: {
    flexWrap: 'wrap',
  },
  requestId: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
  },
  // Collapsed answered state — muted, consistent with the resolved approval inline view.
  answered: {
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
    marginTop: tokens.spacingVerticalXS,
    marginBottom: tokens.spacingVerticalXS,
  },
  answeredAnswer: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
  },
});

export interface QuestionAnswerCardProps {
  // The run that ASKED the question — answers POST against this id. For a bubbled coordinator
  // child question this is the childRunId, NOT the coordinator run id.
  runId: string;
  requestId: string;
  question: string;
  // Present once resolved (from agent.question_answered or an optimistic local submit).
  answer?: string;
  timedOut?: boolean;
  // Optional provenance label, e.g. "Subtask 2" for a child question on the coordinator stream.
  sourceLabel?: string;
}

export function QuestionAnswerCard({ runId, requestId, question, answer, timedOut, sourceLabel }: QuestionAnswerCardProps) {
  const styles = useStyles();
  const [value, setValue] = useState('');
  const [busy, setBusy] = useState(false);
  const [localAnswer, setLocalAnswer] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const resolvedAnswer = answer ?? localAnswer ?? undefined;
  const isAnswered = resolvedAnswer !== undefined;

  const submit = async () => {
    const trimmed = value.trim();
    if (!trimmed || busy || isAnswered) return;
    setBusy(true);
    setError(null);
    try {
      await apiClient.answerQuestion(runId, requestId, trimmed);
      setLocalAnswer(trimmed);
    } catch {
      setError('Could not submit the answer. Try again.');
    } finally {
      setBusy(false);
    }
  };

  const displayId = requestId.length > 8 ? requestId.slice(0, 8) : requestId;

  if (isAnswered) {
    return (
      <div className={mergeClasses('azf-surface azf-surface--subtle azf-stack azf-gap-xs', styles.answered)} role="status">
        <div className="azf-row azf-gap-xs">
          <StatusIconText
            status={timedOut ? 'warning' : 'success'}
            icon={timedOut ? <ClockRegular aria-hidden="true" /> : <CheckmarkCircleFilled aria-hidden="true" />}
          >
            {sourceLabel ? `${sourceLabel} · ` : ''}{timedOut ? 'Question timed out' : 'Question answered'}
          </StatusIconText>
        </div>
        <Text className={styles.answeredAnswer}>{question}</Text>
        {resolvedAnswer && (
          <Text className={styles.answeredAnswer} style={{ color: tokens.colorNeutralForeground1 }}>
            {timedOut ? 'Auto-resolved: ' : 'Answer: '}{resolvedAnswer}
          </Text>
        )}
      </div>
    );
  }

  return (
    <div className={mergeClasses('azf-surface azf-stack', styles.card)} role="alert">
      <div className={mergeClasses('azf-row azf-gap-s', styles.header)}>
        <QuestionCircleFilled
          style={{ fontSize: '18px', color: tokens.colorBrandForeground1 }}
          aria-hidden="true"
        />
        <Text weight="semibold" size={300} style={{ color: tokens.colorBrandForeground1 }}>
          Answer required
        </Text>
        {sourceLabel && (
          <Badge appearance="tint" color="brand" shape="rounded">{sourceLabel}</Badge>
        )}
      </div>
      <div className={mergeClasses('azf-stack azf-gap-s', styles.body)}>
        <Text className={styles.question}>{question}</Text>
        <Textarea
          value={value}
          onChange={(_, d) => setValue(d.value)}
          placeholder="Type your answer…"
          aria-label="Answer to the agent's question"
          resize="vertical"
          disabled={busy}
        />
        {error && (
          <Text size={200} style={{ color: tokens.colorStatusDangerForeground1 }}>{error}</Text>
        )}
        <div className={mergeClasses('azf-row azf-gap-s azf-wrap', styles.actions)}>
          <Button
            appearance="primary"
            size="small"
            icon={<SendRegular />}
            disabled={busy || value.trim().length === 0}
            onClick={() => void submit()}
          >
            Submit answer
          </Button>
          <Text className={styles.requestId}>ID: {displayId}</Text>
        </div>
      </div>
    </div>
  );
}
