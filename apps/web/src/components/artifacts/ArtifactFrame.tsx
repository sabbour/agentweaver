import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import type { ReactNode, Ref } from 'react';

const useStyles = makeStyles({
  frame: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
    borderRadius: tokens.borderRadiusXLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    overflow: 'hidden',
    boxShadow: '0 18px 48px rgba(39, 35, 32, 0.10)',
  },
  /* Full-bleed variant: the frame stretches to fill its container (used by the
     landing artifact takeover) and the body claims the remaining height so the
     result, not empty chrome, owns the viewport. */
  frameFill: {
    flex: 1,
    height: '100%',
    minHeight: 0,
  },
  bar: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalL}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  meta: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
  },
  label: {
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
    lineHeight: '18px',
  },
  caption: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    lineHeight: '16px',
  },
  body: {
    minWidth: 0,
    /* The artifact owns its own art direction inside this scroll frame. */
    maxHeight: '620px',
    overflow: 'auto',
    backgroundColor: tokens.colorNeutralBackground1,
    '@media (max-width: 720px)': {
      maxHeight: 'none',
    },
  },
  /* In the fill variant the body has no fixed cap, so it flexes to the frame's
     height and scrolls internally (the auto-scroll animator drives it). */
  bodyFill: {
    flex: 1,
    minHeight: 0,
    maxHeight: 'none',
    '@media (max-width: 720px)': {
      maxHeight: 'none',
    },
  },
});

export interface ArtifactFrameProps {
  /** Short label for what was produced, e.g. "Pull request preview". */
  label: string;
  /** One-line description of the artifact. */
  caption: string;
  children: ReactNode;
  /** When true, the frame + body stretch to fill their container's height. */
  fill?: boolean;
  /** Forwarded to the scrollable body so callers can drive simulated scrolling. */
  scrollRef?: Ref<HTMLDivElement>;
}

/**
 * Wraps every Stage-5 artifact with a warm-monochrome chrome that labels what the
 * run produced. The artifact body scrolls inside the frame; artifacts bring their
 * own colours and typography.
 */
export function ArtifactFrame({ label, caption, children, fill, scrollRef }: ArtifactFrameProps) {
  const styles = useStyles();
  return (
    <figure className={mergeClasses(styles.frame, fill && styles.frameFill)}>
      <figcaption className={styles.bar}>
        <span className={styles.meta}>
          <span className={styles.label}>{label}</span>
          <span className={styles.caption}>{caption}</span>
        </span>
      </figcaption>
      <div className={mergeClasses(styles.body, fill && styles.bodyFill)} ref={scrollRef}>
        {children}
      </div>
    </figure>
  );
}
