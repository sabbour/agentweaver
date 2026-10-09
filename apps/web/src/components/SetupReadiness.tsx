import {
  Button,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import {
  CheckmarkCircleFilled,
  ChevronDownRegular,
  ChevronUpRegular,
  CircleRegular,
  DismissRegular,
  LockClosedRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { useId, useState } from 'react';
import type { ReactNode } from 'react';

export type SetupReadinessStatus = 'ready' | 'action-required' | 'optional' | 'unavailable';
export type SetupReadinessRequirement = 'required' | 'optional';

export interface SetupReadinessItem {
  id: string;
  title: string;
  description: string;
  requirement: SetupReadinessRequirement;
  status: SetupReadinessStatus;
}

export interface SetupReadinessViewModel {
  title: string;
  description?: string;
  items: SetupReadinessItem[];
  collapseOptional?: boolean;
  loading?: boolean;
  loadingLabel?: string;
  error?: string | null;
}

export interface SetupReadinessProps {
  model: SetupReadinessViewModel;
  primaryAction?: ReactNode;
  onRetry?: () => void;
  onDismiss?: () => void;
  compact?: boolean;
}

const STATUS_LABELS: Record<SetupReadinessStatus, string> = {
  ready: 'Ready',
  'action-required': 'Action required',
  optional: 'Optional',
  unavailable: 'Unavailable to you',
};

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalL,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    minWidth: 0,
  },
  compact: {
    padding: tokens.spacingVerticalM,
  },
  header: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
  },
  heading: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  title: {
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
    fontWeight: tokens.fontWeightSemibold,
  },
  description: {
    color: tokens.colorNeutralForeground2,
    maxWidth: '68ch',
  },
  list: {
    display: 'flex',
    flexDirection: 'column',
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  row: {
    display: 'grid',
    gridTemplateColumns: '20px minmax(0, 1fr) auto',
    alignItems: 'start',
    gap: tokens.spacingHorizontalM,
    padding: `${tokens.spacingVerticalM} 0`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    '@media (max-width: 520px)': {
      gridTemplateColumns: '20px minmax(0, 1fr)',
    },
  },
  icon: {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    minHeight: '22px',
    color: tokens.colorNeutralForeground3,
  },
  readyIcon: {
    color: tokens.colorStatusSuccessForeground1,
  },
  actionIcon: {
    color: tokens.colorStatusWarningForeground1,
  },
  content: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  rowTitle: {
    fontWeight: tokens.fontWeightSemibold,
  },
  metadata: {
    display: 'flex',
    gap: tokens.spacingHorizontalXS,
    flexWrap: 'wrap',
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  rowDescription: {
    color: tokens.colorNeutralForeground2,
    overflowWrap: 'anywhere',
  },
  status: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    whiteSpace: 'nowrap',
    '@media (max-width: 520px)': {
      gridColumn: '2',
      whiteSpace: 'normal',
    },
  },
  footer: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  primaryAction: {
    marginLeft: 'auto',
  },
});

function StatusIcon({ status }: { status: SetupReadinessStatus }) {
  const styles = useStyles();
  if (status === 'ready') {
    return <CheckmarkCircleFilled className={styles.readyIcon} aria-hidden="true" />;
  }
  if (status === 'action-required') {
    return <WarningRegular className={styles.actionIcon} aria-hidden="true" />;
  }
  if (status === 'unavailable') {
    return <LockClosedRegular aria-hidden="true" />;
  }
  return <CircleRegular aria-hidden="true" />;
}

export function SetupReadiness({
  model,
  primaryAction,
  onRetry,
  onDismiss,
  compact = false,
}: SetupReadinessProps) {
  const styles = useStyles();
  const headingId = useId();
  const listId = useId();
  const [collapsedOptionalExpanded, setCollapsedOptionalExpanded] = useState(false);
  const requiredItems = model.items.filter((item) => item.requirement === 'required');
  const optionalItems = model.items.filter((item) => item.requirement === 'optional');
  const optionalExpanded = model.collapseOptional ? collapsedOptionalExpanded : true;

  const visibleItems = optionalExpanded
    ? [...requiredItems, ...optionalItems]
    : requiredItems;

  return (
    <section
      className={`${styles.root}${compact ? ` ${styles.compact}` : ''}`}
      aria-labelledby={headingId}
      aria-busy={model.loading || undefined}
    >
      <div className={styles.header}>
        <div className={styles.heading}>
          <Text
            as="h2"
            id={headingId}
            className={styles.title}
          >
            {model.title}
          </Text>
          {model.description && <Text className={styles.description}>{model.description}</Text>}
        </div>
        {onDismiss && (
          <Button
            appearance="subtle"
            size="small"
            icon={<DismissRegular />}
            aria-label={`Dismiss ${model.title}`}
            onClick={onDismiss}
          />
        )}
      </div>

      {model.loading ? (
        <Spinner
          size="tiny"
          label={model.loadingLabel ?? 'Loading setup status'}
          labelPosition="after"
        />
      ) : model.error ? (
        <MessageBar intent="error">
          <MessageBarBody>{model.error}</MessageBarBody>
        </MessageBar>
      ) : (
        <div
          id={listId}
          className={styles.list}
          role="list"
          aria-label={`${model.title} checklist`}
          hidden={visibleItems.length === 0}
        >
          {visibleItems.map((item) => (
            <div className={styles.row} role="listitem" key={item.id}>
              <span className={styles.icon}><StatusIcon status={item.status} /></span>
              <div className={styles.content}>
                <Text className={styles.rowTitle}>{item.title}</Text>
                <div className={styles.metadata}>
                  <span>{item.requirement === 'required' ? 'Required' : 'Optional'}</span>
                </div>
                <Text className={styles.rowDescription}>{item.description}</Text>
              </div>
              <Text className={styles.status}>{STATUS_LABELS[item.status]}</Text>
            </div>
          ))}
        </div>
      )}

      <div className={styles.footer}>
        {!model.loading && !model.error && model.collapseOptional && optionalItems.length > 0 && (
          <Button
            appearance="subtle"
            size="small"
            icon={optionalExpanded ? <ChevronUpRegular /> : <ChevronDownRegular />}
            aria-expanded={optionalExpanded}
            aria-controls={listId}
            onClick={() => setCollapsedOptionalExpanded((expanded) => !expanded)}
          >
            {optionalExpanded ? 'Hide optional setup' : 'Show optional setup'}
          </Button>
        )}
        {model.error && onRetry && (
          <Button appearance="secondary" onClick={onRetry}>Reload setup status</Button>
        )}
        {!model.loading && !model.error && primaryAction && (
          <div className={styles.primaryAction}>{primaryAction}</div>
        )}
      </div>
    </section>
  );
}
