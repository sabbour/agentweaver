import { useState } from 'react';
import type { ReactNode } from 'react';
import {
  Popover,
  PopoverSurface,
  PopoverTrigger,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import { SparkleRegular } from '@fluentui/react-icons';
import { costChipLabel, formatAic } from './CostChip';
import { formatUsd, hasUsdRate, nanoAiuToCredits } from '../lib/credits';

const useStyles = makeStyles({
  trigger: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    appearance: 'none',
    border: 'none',
    cursor: 'pointer',
    padding: `${tokens.spacingVerticalXXS} ${tokens.spacingHorizontalXS}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: 'transparent',
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightMedium,
    fontFamily: tokens.fontFamilyBase,
    whiteSpace: 'nowrap',
    lineHeight: tokens.lineHeightBase200,
    ':hover': { backgroundColor: tokens.colorNeutralBackground3, color: tokens.colorNeutralForeground1 },
  },
  triggerPlain: {
    padding: 0,
    borderRadius: 0,
    color: 'inherit',
    fontSize: 'inherit',
    fontWeight: 'inherit',
    lineHeight: 'inherit',
    ':hover': { backgroundColor: 'transparent', color: 'inherit', textDecorationLine: 'underline' },
  },
  glyphPlain: {
    fontSize: 'inherit',
    color: 'inherit',
  },
  glyph: {
    display: 'inline-flex',
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground3,
  },
  surface: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    minWidth: '220px',
  },
  row: {
    display: 'flex',
    alignItems: 'baseline',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
  },
  rowLabel: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  rowValue: {
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  usd: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
  },
  muted: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  link: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    textDecorationLine: 'underline',
    ':hover': { color: tokens.colorNeutralForeground1 },
  },
});

export interface AiCreditsProps {
  /** Raw backend usage in nano-AIU. Preferred source for the AIC value + USD estimate. */
  totalNanoAiu?: number | null;
  /** Token fallback shown when there is no AIC value (mirrors CostChip). */
  totalTokens?: number | null;
  /** Extra popover content rendered below the standard rows (e.g. a token breakdown). */
  detail?: ReactNode;
  /** Render "0 AIC" instead of nothing when there is no usage yet (used by the composer). */
  showZero?: boolean;
  /** Inherit surrounding typography (font size/weight/color) instead of the compact badge style.
   *  Use inside headline metric tiles so the value keeps its display size. */
  plain?: boolean;
  ariaLabel?: string;
  'data-testid'?: string;
}

/**
 * Shared, hoverable AI-credits affordance used across Agentweaver. Shows the compact "{N} AIC"
 * value with a sparkle glyph; hover or click opens a Popover with the Session credit total, the
 * USD estimate (only when a real rate is configured — see lib/credits.ts), and any extra detail.
 * Reuses formatAic/costChipLabel so it stays consistent with CostChip.
 */
export function AiCredits({
  totalNanoAiu,
  totalTokens,
  detail,
  showZero = false,
  plain = false,
  ariaLabel,
  'data-testid': testId,
}: AiCreditsProps) {
  const styles = useStyles();
  const [open, setOpen] = useState(false);

  const label = costChipLabel(totalNanoAiu, totalTokens);
  if (!label && !showZero) return null;

  const hasAic = totalNanoAiu != null && totalNanoAiu > 0;
  const displayLabel = label ?? '0 AIC';
  const credits = nanoAiuToCredits(totalNanoAiu);
  const sessionValue = hasAic || showZero
    ? `${formatAic(totalNanoAiu ?? 0)} AIC`
    : label ?? '—';
  const showUsd = hasUsdRate() && (hasAic || showZero);
  const hasTokens = totalTokens != null && totalTokens > 0;

  return (
    <Popover
      open={open}
      onOpenChange={(_, data) => setOpen(data.open)}
      openOnHover
      mouseLeaveDelay={150}
      withArrow
      positioning="above"
    >
      <PopoverTrigger disableButtonEnhancement>
        <button
          type="button"
          className={mergeClasses(styles.trigger, plain && styles.triggerPlain)}
          onClick={() => setOpen((value) => !value)}
          data-testid={testId}
          aria-label={ariaLabel ?? `AI credits — ${sessionValue}`}
          title={sessionValue}
        >
          <SparkleRegular className={mergeClasses(styles.glyph, plain && styles.glyphPlain)} aria-hidden="true" />
          <span>{displayLabel}</span>
        </button>
      </PopoverTrigger>
      <PopoverSurface className={styles.surface}>
        <div className={styles.row}>
          <span className={styles.rowLabel}>Session</span>
          <span className={styles.rowValue}>{sessionValue}</span>
        </div>
        {showUsd && <div className={styles.usd}>{`\u2248 ${formatUsd(credits)}`}</div>}
        {showUsd && <div className={styles.muted}>{`1 AIC = ${formatUsd(1)}`}</div>}
        {hasTokens && (
          <div className={styles.muted}>{`${totalTokens!.toLocaleString()} tokens`}</div>
        )}
        {detail}
        <a
          className={styles.link}
          href="https://github.github.com/gh-aw/specs/ai-credits-specification/"
          target="_blank"
          rel="noopener noreferrer"
        >
          Pricing details
        </a>
      </PopoverSurface>
    </Popover>
  );
}
