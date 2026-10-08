/**
 * FeedbackButtons — mirrors @1js/fai-react-feedback-buttons FeedbackButtons.
 *
 * Props mirrored:
 *   - selected?: "positive" | "negative" (controlled)
 *   - disabled?: boolean
 *   - onFeedback?: (value: "positive" | "negative") => void
 *
 * Slots mirrored: root, positiveFeedbackButton, negativeFeedbackButton
 *   (positiveFeedbackTooltip, negativeFeedbackTooltip — via Fluent Tooltip)
 */
import { mergeClasses, Tooltip } from "@fluentui/react-components";
import { ThumbLikeRegular, ThumbDislikeRegular, ThumbLikeFilled, ThumbDislikeFilled } from "@fluentui/react-icons";
import { useFeedbackButtonStyles } from "./copilotStyles";

export type FeedbackValue = "positive" | "negative";

export interface FeedbackButtonsProps {
  /** Controlled selected state — mirrors FeedbackButtonsProps.selected */
  selected?: FeedbackValue;
  disabled?: boolean;
  onFeedback?: (value: FeedbackValue) => void;
  className?: string;
}

export function FeedbackButtons({
  selected,
  disabled = false,
  onFeedback,
  className,
}: FeedbackButtonsProps) {
  const styles = useFeedbackButtonStyles();

  return (
    <div
      className={mergeClasses(
        styles.root,
        disabled ? styles.disabled : undefined,
        className
      )}
      role="group"
      aria-label="Message feedback"
    >
      {/* slot: positiveFeedbackButton + positiveFeedbackTooltip */}
      <Tooltip content="Helpful" relationship="label" withArrow>
        <button
          type="button"
          className={mergeClasses(
            styles.positiveButton,
            selected === "positive" ? styles.selectedPositive : undefined
          )}
          onClick={() => onFeedback?.("positive")}
          aria-label="Helpful"
          aria-pressed={selected === "positive"}
          disabled={disabled}
        >
          {selected === "positive" ? (
            <ThumbLikeFilled fontSize={14} />
          ) : (
            <ThumbLikeRegular fontSize={14} />
          )}
        </button>
      </Tooltip>

      {/* slot: negativeFeedbackButton + negativeFeedbackTooltip */}
      <Tooltip content="Not helpful" relationship="label" withArrow>
        <button
          type="button"
          className={mergeClasses(
            styles.negativeButton,
            selected === "negative" ? styles.selectedNegative : undefined
          )}
          onClick={() => onFeedback?.("negative")}
          aria-label="Not helpful"
          aria-pressed={selected === "negative"}
          disabled={disabled}
        >
          {selected === "negative" ? (
            <ThumbDislikeFilled fontSize={14} />
          ) : (
            <ThumbDislikeRegular fontSize={14} />
          )}
        </button>
      </Tooltip>
    </div>
  );
}
