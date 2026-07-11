/**
 * OutputCard — the assistant response container.
 *
 * Wraps streamed or complete assistant output in the copilot-styled card:
 *   - Optional indeterminate ProgressBar at the top while streaming
 *   - Body: the message content (markdown, agentic steps, tool calls, etc.)
 *   - Optional feedback row (thumbs up / thumbs down)
 *
 * Designed to be used INSIDE a MessageList, or standalone in a run view
 * alongside AgentStepList / ToolCallRow from components/ui/agentic/.
 *
 * Example:
 *   <OutputCard isStreaming>
 *     <Text>Thinking…</Text>
 *   </OutputCard>
 *
 *   <OutputCard
 *     showFeedback
 *     onFeedback={(v) => console.log(v)}
 *   >
 *     <AgentStepList steps={steps} onApprove={…} onDeny={…} />
 *   </OutputCard>
 */

import type { ReactNode } from 'react';
import { Button, ProgressBar, Text, Tooltip, mergeClasses } from '@fluentui/react-components';
import { ThumbDislikeRegular, ThumbLikeRegular } from '@fluentui/react-icons';
import { useCopilotStyles } from './copilotStyles';

export type FeedbackValue = 'positive' | 'negative';

export interface OutputCardProps {
  children: ReactNode;
  /** Shows an indeterminate progress bar at the top while streaming. */
  isStreaming?: boolean;
  /** Renders feedback thumbs in the footer when true. */
  showFeedback?: boolean;
  /** Called when the user clicks a feedback button. */
  onFeedback?: (value: FeedbackValue) => void;
  /** Current feedback selection (controlled). */
  feedbackValue?: FeedbackValue;
  /** Node rendered in the footer before the feedback buttons (e.g. copy action). */
  footerActions?: ReactNode;
  className?: string;
}

export function OutputCard({
  children,
  isStreaming = false,
  showFeedback = false,
  onFeedback,
  feedbackValue,
  footerActions,
  className,
}: OutputCardProps) {
  const styles = useCopilotStyles();
  const showFooter = showFeedback || Boolean(footerActions);

  return (
    <div
      className={mergeClasses(styles.outputCard, className)}
      aria-busy={isStreaming || undefined}
    >
      {isStreaming && (
        <ProgressBar
          className={styles.outputCardProgress}
          aria-label="Generating response"
        />
      )}
      <div className={styles.outputCardBody}>{children}</div>
      {showFooter && (
        <div className={styles.outputCardFooter}>
          {footerActions && (
            <span className={styles.outputCardFooterSpacer}>{footerActions}</span>
          )}
          {!footerActions && <span className={styles.outputCardFooterSpacer} />}
          {showFeedback && (
            <>
              <Text className={styles.outputCardFeedbackLabel}>Was this helpful?</Text>
              <Tooltip content="Helpful" relationship="label">
                <Button
                  appearance="subtle"
                  size="small"
                  icon={<ThumbLikeRegular />}
                  aria-label="Helpful"
                  aria-pressed={feedbackValue === 'positive'}
                  onClick={() => onFeedback?.('positive')}
                />
              </Tooltip>
              <Tooltip content="Not helpful" relationship="label">
                <Button
                  appearance="subtle"
                  size="small"
                  icon={<ThumbDislikeRegular />}
                  aria-label="Not helpful"
                  aria-pressed={feedbackValue === 'negative'}
                  onClick={() => onFeedback?.('negative')}
                />
              </Tooltip>
            </>
          )}
        </div>
      )}
    </div>
  );
}
