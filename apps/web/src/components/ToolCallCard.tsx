import { memo } from 'react';
import {
  Accordion,
  AccordionHeader,
  AccordionItem,
  AccordionPanel,
  Badge,
  Spinner,
  Text,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import {
  CheckmarkCircleFilled,
  ErrorCircleFilled,
  WarningFilled,
  WrenchRegular,
} from '@fluentui/react-icons';
import type { ToolCallItem } from '../timeline/types';
import type { StreamStatus } from '../api/sse';

/** Characters displayed per content block before "show more" (Y-1). */
const BLOCK_MAX = 50_000;

const useStyles = makeStyles({
  card: {
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    overflow: 'hidden',
    marginTop: '2px',
    marginBottom: '2px',
  },
  cardError: {
    border: `1px solid ${tokens.colorPaletteRedForeground2}`,
    backgroundColor: tokens.colorPaletteRedBackground1,
  },
  cardSandbox: {
    border: `1px solid ${tokens.colorPaletteYellowForeground2}`,
    backgroundColor: tokens.colorPaletteYellowBackground1,
  },
  headerRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  statusIcon: {
    flexShrink: 0,
  },
  successIcon: {
    color: tokens.colorPaletteGreenForeground1,
  },
  errorIcon: {
    color: tokens.colorPaletteRedForeground1,
  },
  sandboxIcon: {
    color: tokens.colorPaletteYellowForeground1,
  },
  titleText: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
  },
  block: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    backgroundColor: tokens.colorNeutralBackground3,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-all',
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  blockLabel: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
    marginBottom: tokens.spacingVerticalXS,
    display: 'block',
  },
  truncatedNote: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
    marginTop: tokens.spacingVerticalXS,
    display: 'block',
  },
  errorBlock: {
    backgroundColor: tokens.colorPaletteRedBackground1,
  },
  sandboxBlock: {
    backgroundColor: tokens.colorPaletteYellowBackground1,
  },
  completedOpacity: {
    opacity: 0.6,
  },
  badge: {
    marginLeft: tokens.spacingHorizontalXS,
    flexShrink: 0,
  },
});

interface ToolCallCardProps {
  item: ToolCallItem;
  streamStatus: StreamStatus;
}

function getAriaLabel(item: ToolCallItem): string {
  if (!item.settled) return `Tool call: ${item.humanTitle} — pending`;
  if (item.error) {
    return item.error.isSandboxViolation
      ? `Tool call: ${item.humanTitle} — sandbox violation`
      : `Tool call: ${item.humanTitle} — failed`;
  }
  return `Tool call: ${item.humanTitle} — succeeded`;
}

function truncate(text: string) {
  const truncated = text.length > BLOCK_MAX;
  return { display: truncated ? text.slice(0, BLOCK_MAX) : text, truncated, total: text.length };
}

export const ToolCallCard = memo(function ToolCallCard({ item, streamStatus }: ToolCallCardProps) {
  const styles = useStyles();

  const isSandbox = item.error?.isSandboxViolation ?? false;
  const isError = item.error && !isSandbox;
  const isSuccess = item.settled && !item.error;

  const cardClass = mergeClasses(
    styles.card,
    isSandbox ? styles.cardSandbox : undefined,
    isError ? styles.cardError : undefined,
    isSuccess ? styles.completedOpacity : undefined,
  );

  // Sandbox violations expand by default (security-relevant, §7.2)
  const defaultOpen = isSandbox ? ['tool'] : [];

  function StatusIcon() {
    if (!item.settled) {
      return streamStatus === 'error' ? (
        <WarningFilled
          className={mergeClasses(styles.statusIcon, styles.sandboxIcon)}
          aria-label="Result not received"
        />
      ) : (
        <Spinner size="extra-tiny" aria-label="Pending" />
      );
    }
    if (isSandbox) {
      return (
        <WarningFilled
          className={mergeClasses(styles.statusIcon, styles.sandboxIcon)}
          aria-hidden="true"
        />
      );
    }
    if (item.error) {
      return (
        <ErrorCircleFilled
          className={mergeClasses(styles.statusIcon, styles.errorIcon)}
          aria-hidden="true"
        />
      );
    }
    return (
      <CheckmarkCircleFilled
        className={mergeClasses(styles.statusIcon, styles.successIcon)}
        aria-hidden="true"
      />
    );
  }

  const argsJson = JSON.stringify(item.args, null, 2);
  const { display: argsDisplay, truncated: argsTrunc, total: argsTotal } = truncate(argsJson);

  return (
    <div className={cardClass}>
      <Accordion collapsible defaultOpenItems={defaultOpen}>
        <AccordionItem value="tool">
          <AccordionHeader
            aria-label={getAriaLabel(item)}
            expandIconPosition="end"
            size="small"
          >
            <div className={styles.headerRow}>
              {/* SECURITY (Y-3): humanTitle and toolName rendered as text — no HTML */}
              <WrenchRegular aria-hidden="true" />
              <StatusIcon />
              <Text className={styles.titleText}>{item.humanTitle}</Text>
              {isSandbox && (
                <Badge className={styles.badge} color="warning" shape="rounded" size="small">
                  sandbox
                </Badge>
              )}
              {isError && (
                <Badge className={styles.badge} color="danger" shape="rounded" size="small">
                  error
                </Badge>
              )}
            </div>
          </AccordionHeader>
          <AccordionPanel>
            {/* SECURITY (Y-3): args/results rendered as text nodes — no dangerouslySetInnerHTML */}
            <div className={styles.block}>
              <Text as="span" className={styles.blockLabel}>arguments</Text>
              <Text as="pre" style={{ margin: 0, fontFamily: 'inherit', fontSize: 'inherit' }}>
                {argsDisplay}
              </Text>
              {argsTrunc && (
                <Text as="span" className={styles.truncatedNote}>
                  [Truncated — {argsTotal.toLocaleString()} chars total]
                </Text>
              )}
            </div>

            {item.result && (() => {
              const { display, truncated, total } = truncate(item.result.content);
              return (
                <div className={styles.block}>
                  <Text as="span" className={styles.blockLabel}>result</Text>
                  <Text as="pre" style={{ margin: 0, fontFamily: 'inherit', fontSize: 'inherit' }}>
                    {display}
                  </Text>
                  {truncated && (
                    <Text as="span" className={styles.truncatedNote}>
                      [Truncated — {total.toLocaleString()} chars total]
                    </Text>
                  )}
                </div>
              );
            })()}

            {item.error && (() => {
              const { display, truncated, total } = truncate(item.error.errorMessage);
              return (
                <div
                  className={mergeClasses(
                    styles.block,
                    isSandbox ? styles.sandboxBlock : styles.errorBlock,
                  )}
                >
                  <Text as="span" className={styles.blockLabel}>error</Text>
                  <Text as="pre" style={{ margin: 0, fontFamily: 'inherit', fontSize: 'inherit' }}>
                    {display}
                  </Text>
                  {truncated && (
                    <Text as="span" className={styles.truncatedNote}>
                      [Truncated — {total.toLocaleString()} chars total]
                    </Text>
                  )}
                </div>
              );
            })()}
          </AccordionPanel>
        </AccordionItem>
      </Accordion>
    </div>
  );
});
