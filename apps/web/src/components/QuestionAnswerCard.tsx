import { useState } from 'react';
import { Badge, Button, Text, Textarea, makeStyles, tokens } from '@fluentui/react-components';
import {
  QuestionCircleFilled,
  CheckmarkCircleFilled,
  ClockRegular,
  SendRegular,
} from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';

const useStyles = makeStyles({
  // Mirrors the HITL tool-approval card treatment (brand-stroked, shadowed) so a blocked
  // question reads as an equally prominent operator action.
  card: {
    display: 'flex',
    flexDirection: 'column',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorBrandStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    marginTop: tokens.spacingVerticalXS,
    marginBottom: tokens.spacingVerticalXS,
    overflow: 'hidden',
    boxShadow: tokens.shadow4,
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    backgroundColor: tokens.colorBrandBackground2,
    borderBottom: `1px solid ${tokens.colorBrandStroke2}`,
  },
  body: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
  },
  question: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground1,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
  },
  actions: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
    flexWrap: 'wrap',
  },
  requestId: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
  },
  // Collapsed answered state — muted, consistent with the resolved approval inline view.
  answered: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
    marginTop: tokens.spacingVerticalXS,
    marginBottom: tokens.spacingVerticalXS,
  },
  answeredHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
  },
  answeredAnswer: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
  },
  successIcon: { color: tokens.colorStatusSuccessForeground1, flexShrink: 0 },
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
      <div className={styles.answered} role="status">
        <div className={styles.answeredHeader}>
          {timedOut
            ? <ClockRegular aria-hidden="true" style={{ color: tokens.colorStatusWarningForeground1 }} />
            : <CheckmarkCircleFilled className={styles.successIcon} aria-hidden="true" />}
          <Text size={200} weight="semibold" style={{ color: tokens.colorNeutralForeground2 }}>
            {sourceLabel ? `${sourceLabel} · ` : ''}{timedOut ? 'Question timed out' : 'Question answered'}
          </Text>
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
    <div className={styles.card} role="alert">
      <div className={styles.header}>
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
      <div className={styles.body}>
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
        <div className={styles.actions}>
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
