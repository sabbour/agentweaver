import { useEffect, useState } from 'react';
import {
  Button,
  Field,
  Input,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { getRunDiff, reviewRun, ApiError } from '../api/client';
import type { RunResponse } from '../api/client';
import { RunStatusBadge } from '../components/RunStatusBadge';
import { DiffViewer } from '../components/DiffViewer';

interface ReviewPageProps {
  run: RunResponse;
  onComplete: (run: RunResponse) => void;
}

/**
 * T065: ReviewPage — fetches diff, renders DiffViewer, presents approve/decline buttons.
 * POST /runs/{runId}/review on action; displays resulting RunStatusBadge and merge outcome.
 * No emojis (NFR-002).
 */
export function ReviewPage({ run, onComplete }: ReviewPageProps) {
  const [diff, setDiff] = useState<string>('');
  const [diffError, setDiffError] = useState<string | null>(null);
  const [reviewer, setReviewer] = useState('');
  const [comment, setComment] = useState('');
  const [loading, setLoading] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const [result, setResult] = useState<RunResponse | null>(null);

  useEffect(() => {
    getRunDiff(run.id)
      .then(setDiff)
      .catch((err) => {
        if (err instanceof ApiError && err.status === 409) {
          setDiffError('Diff not available for this run state.');
        } else {
          setDiffError('Failed to load diff.');
        }
      });
  }, [run.id]);

  const handleReview = async (decision: 'approve' | 'decline') => {
    if (!reviewer.trim()) {
      setActionError('Reviewer name is required.');
      return;
    }

    setLoading(true);
    setActionError(null);

    try {
      const updated = await reviewRun(run.id, {
        decision,
        reviewer: reviewer.trim(),
        comment: comment.trim() || undefined,
      });
      setResult(updated);
      onComplete(updated);
    } catch (err) {
      if (err instanceof ApiError) {
        setActionError(`API error ${err.status}: ${err.body}`);
      } else {
        setActionError('An unexpected error occurred.');
      }
    } finally {
      setLoading(false);
    }
  };

  if (result) {
    return (
      <div style={{ padding: '24px' }}>
        <Text as="h1" size={700} weight="semibold" block>
          Review Complete
        </Text>
        <div style={{ display: 'flex', gap: '8px', marginTop: '12px', alignItems: 'center' }}>
          <Text>Result:</Text>
          <RunStatusBadge status={result.status} />
        </div>
        {result.failureReason && (
          <Text block style={{ color: tokens.colorPaletteRedForeground1, marginTop: '8px' }}>
            {result.failureReason}
          </Text>
        )}
      </div>
    );
  }

  return (
    <div style={{ padding: '24px', maxWidth: '900px' }}>
      <Text as="h1" size={700} weight="semibold" block>
        Review Run {run.id}
      </Text>
      <div style={{ display: 'flex', gap: '8px', marginBottom: '16px', alignItems: 'center' }}>
        <RunStatusBadge status={run.status} />
        <Text style={{ color: tokens.colorNeutralForeground3 }}>
          Branch: {run.originatingBranch}
        </Text>
      </div>

      <Text as="h2" size={500} weight="semibold" block style={{ marginBottom: '8px' }}>
        Diff
      </Text>
      {diffError ? (
        <Text style={{ color: tokens.colorPaletteRedForeground1 }}>{diffError}</Text>
      ) : (
        <DiffViewer diff={diff} />
      )}

      <div style={{ marginTop: '24px', display: 'flex', flexDirection: 'column', gap: '12px', maxWidth: '400px' }}>
        <Field label="Reviewer" required>
          <Input
            value={reviewer}
            onChange={(_, d) => setReviewer(d.value)}
            placeholder="Your name"
          />
        </Field>

        <Field label="Comment (optional)">
          <Textarea
            value={comment}
            onChange={(_, d) => setComment(d.value)}
            rows={3}
          />
        </Field>

        {actionError && (
          <Text style={{ color: tokens.colorPaletteRedForeground1 }}>{actionError}</Text>
        )}

        <div style={{ display: 'flex', gap: '8px' }}>
          <Button
            appearance="primary"
            onClick={() => handleReview('approve')}
            disabled={loading}
            icon={loading ? <Spinner size="tiny" /> : undefined}
          >
            {loading ? 'Processing...' : 'Approve'}
          </Button>
          <Button
            appearance="secondary"
            onClick={() => handleReview('decline')}
            disabled={loading}
          >
            Decline
          </Button>
        </div>
      </div>
    </div>
  );
}
