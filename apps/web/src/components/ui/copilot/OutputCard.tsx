/**
 * OutputCard — mirrors @1js/fai-react-output-card OutputCard anatomy.
 *
 * Props mirrored:
 *   - isLoading?: boolean  (replaces streaming; triggers flair-done CSS on false transition)
 *   - mode?: "canvas" | "sidecar"  (canvas adds shadow)
 *
 * Slots mirrored: root, progress (opt-in ProgressBar at top of card), body (children).
 *
 * When isLoading transitions from true → false a "done" CSS class is applied
 * briefly to create a subtle border-brightening flair effect (mirrors @1js flair).
 */
import React, { useEffect, useRef, useState } from "react";
import { mergeClasses, ProgressBar } from "@fluentui/react-components";
import { useOutputCardStyles } from "./copilotStyles";

export type OutputCardMode = "canvas" | "sidecar";

export interface OutputCardProps {
  /** Whether the card is in a loading/streaming state — ProgressBar shown when true */
  isLoading?: boolean;
  /** canvas adds a shadow; sidecar (docked console) is flat */
  mode?: OutputCardMode;
  /** Opt-in progress slot: renders ProgressBar at the top of the card */
  showProgress?: boolean;
  children?: React.ReactNode;
  className?: string;
}

export function OutputCard({
  isLoading = false,
  mode = "sidecar",
  showProgress,
  children,
  className,
}: OutputCardProps) {
  const styles = useOutputCardStyles();
  const [doneFlair, setDoneFlair] = useState(false);
  const prevLoading = useRef(isLoading);

  // When isLoading transitions false → brief "done" flair
  useEffect(() => {
    if (prevLoading.current && !isLoading) {
      setDoneFlair(true);
      const id = setTimeout(() => setDoneFlair(false), 600);
      prevLoading.current = isLoading;
      return () => clearTimeout(id);
    }
    prevLoading.current = isLoading;
  }, [isLoading]);

  const renderProgress = showProgress ?? isLoading;

  return (
    <div
      className={mergeClasses(
        styles.root,
        mode === "canvas" ? styles.rootCanvas : undefined,
        doneFlair ? styles.done : undefined,
        className
      )}
    >
      {/* slot: progress — opt-in ProgressBar at top of card */}
      {renderProgress && (
        <div className={styles.progress}>
          <ProgressBar thickness="medium" />
        </div>
      )}

      {/* body — message content */}
      <div className={styles.body}>{children}</div>
    </div>
  );
}
