import { makeStyles, tokens } from '@fluentui/react-components';
import type { ReactNode } from 'react';

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
  bar: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalL}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  badge: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    padding: '2px 9px',
    borderRadius: tokens.borderRadiusCircular,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightSemibold,
    letterSpacing: '0.01em',
    whiteSpace: 'nowrap',
    flexShrink: 0,
  },
  dot: {
    width: '6px',
    height: '6px',
    borderRadius: '50%',
    backgroundColor: tokens.colorNeutralForeground3,
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
});

export interface ArtifactFrameProps {
  /** Short label for what was produced, e.g. "Pull request preview". */
  label: string;
  /** One-line description of the artifact. */
  caption: string;
  children: ReactNode;
}

/**
 * Wraps every Stage-5 artifact with a warm-monochrome chrome that carries the
 * mandatory visible "Illustrative output" disclosure. The artifact body scrolls
 * inside the frame; artifacts bring their own colours and typography.
 */
export function ArtifactFrame({ label, caption, children }: ArtifactFrameProps) {
  const styles = useStyles();
  return (
    <figure className={styles.frame}>
      <figcaption className={styles.bar}>
        <span className={styles.badge}>
          <span className={styles.dot} aria-hidden="true" />
          Illustrative output
        </span>
        <span className={styles.meta}>
          <span className={styles.label}>{label}</span>
          <span className={styles.caption}>{caption}</span>
        </span>
      </figcaption>
      <div className={styles.body}>{children}</div>
    </figure>
  );
}
