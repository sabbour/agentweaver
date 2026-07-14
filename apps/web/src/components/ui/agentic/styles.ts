import { makeStyles, tokens } from '@fluentui/react-components';

export const useAgenticStyles = makeStyles({
  // ArtifactChip
  artifactChip: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    height: '32px',
    padding: `0 ${tokens.spacingHorizontalS}`,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    cursor: 'pointer',
    fontFamily: tokens.fontFamilyBase,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground1,
    transition: 'background-color 150ms ease-out, border-color 150ms ease-out',
    '@media (prefers-reduced-motion: reduce)': {
      transition: 'none',
    },
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
      border: `1px solid ${tokens.colorNeutralStroke1Hover}`,
    },
    ':active': {
      backgroundColor: tokens.colorNeutralBackground1Pressed,
    },
  },
  artifactChipIcon: {
    display: 'flex',
    alignItems: 'center',
    fontSize: '16px',
    color: tokens.colorNeutralForeground3,
    flexShrink: 0,
  },
  artifactChipTitle: {
    fontWeight: tokens.fontWeightSemibold,
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    maxWidth: '160px',
  },
  artifactChipType: {
    color: tokens.colorNeutralForeground3,
    whiteSpace: 'nowrap',
  },

  // ApprovalGate — inline warning block
  approvalGate: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingVerticalM,
    border: `1px solid ${tokens.colorStatusWarningBorder1}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorStatusWarningBackground2,
  },
  approvalRiskText: {
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    color: tokens.colorNeutralForeground1,
  },
  approvalDisclaimer: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  approvalActions: {
    display: 'flex',
    flexDirection: 'row',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },

  // AgentStep — timeline item
  stepList: {
    display: 'flex',
    flexDirection: 'column',
    gap: '0',
    listStyle: 'none',
    margin: '0',
    padding: '0',
  },
  stepItem: {
    display: 'flex',
    flexDirection: 'column',
    position: 'relative',
    paddingLeft: '32px',
    // Vertical connector line
    '::before': {
      content: '""',
      position: 'absolute',
      left: '11px',
      top: '28px',
      bottom: '0',
      width: '1px',
      backgroundColor: tokens.colorNeutralStroke2,
    },
    ':last-child::before': {
      display: 'none',
    },
  },
  stepHeader: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalS} 0`,
    cursor: 'pointer',
    userSelect: 'none',
  },
  stepIconSlot: {
    position: 'absolute',
    left: '0',
    top: tokens.spacingVerticalS,
    width: '24px',
    height: '24px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    flexShrink: 0,
    zIndex: 1,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  stepTitle: {
    flex: 1,
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: tokens.lineHeightBase300,
    color: tokens.colorNeutralForeground1,
  },
  stepTitleWrap: {
    flex: 1,
    minWidth: 0,
    display: 'flex',
    alignItems: 'baseline',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalXS,
  },
  stepBadge: {
    display: 'inline-flex',
    alignItems: 'center',
    padding: `0 ${tokens.spacingHorizontalXS}`,
    height: '18px',
    borderRadius: tokens.borderRadiusSmall,
    backgroundColor: tokens.colorNeutralBackground3,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
    lineHeight: tokens.lineHeightBase100,
    whiteSpace: 'nowrap',
  },
  stepStatusLabel: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
    whiteSpace: 'nowrap',
    alignSelf: 'center',
  },
  stepChevron: {
    fontSize: '12px',
    color: tokens.colorNeutralForeground3,
    alignSelf: 'center',
    transition: 'transform 150ms ease-out',
    '@media (prefers-reduced-motion: reduce)': {
      transition: 'none',
    },
  },
  stepChevronOpen: {
    transform: 'rotate(90deg)',
  },
  stepPanel: {
    paddingBottom: tokens.spacingVerticalM,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  stepBody: {
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    color: tokens.colorNeutralForeground2,
  },
  stepArtifacts: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalXS,
  },

  /**
   * Container for nested child steps inside an expanded parent panel.
   * A left border acts as the vertical connector line for the sub-tree;
   * the 16px left padding provides indentation so child icons have room.
   */
  stepChildrenList: {
    listStyle: 'none',
    margin: `${tokens.spacingVerticalXS} 0 0 0`,
    padding: '0',
    paddingLeft: '12px',
    borderLeft: `1.5px solid ${tokens.colorNeutralStroke2}`,
  },

  // Status icon colors
  statusComplete: {
    color: '#16a149',
  },
  statusWarning: {
    color: '#8a4b01',
  },
  statusDanger: {
    color: '#a62147',
  },
  statusRunning: {
    color: tokens.colorNeutralForeground3,
  },
  statusPending: {
    color: tokens.colorNeutralForeground4,
  },

  // Running pulse dot
  runningDot: {
    width: '10px',
    height: '10px',
    borderRadius: '50%',
    backgroundColor: tokens.colorNeutralForeground3,
    animationName: {
      '0%, 100%': { opacity: '0.4', transform: 'scale(0.9)' },
      '50%': { opacity: '1', transform: 'scale(1.1)' },
    },
    animationDuration: '1.2s',
    animationIterationCount: 'infinite',
    animationTimingFunction: 'ease-in-out',
    '@media (prefers-reduced-motion: reduce)': {
      animationName: 'none',
    },
  },

  // ToolCallRow
  toolCallRow: {    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  toolCallHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    cursor: 'pointer',
  },
  toolCallName: {
    fontFamily: 'ui-monospace, "Cascadia Code", Consolas, monospace',
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  toolCallStatusIcon: {
    fontSize: '14px',
    flexShrink: 0,
  },
  toolCallSummary: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    paddingLeft: tokens.spacingHorizontalXL,
  },
  toolCallArtifacts: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalXS,
    paddingLeft: tokens.spacingHorizontalXL,
  },

  // AgentActivitySession — "Run activity" panel
  activitySession: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  activityHeader: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
  },
  activityHeaderTitles: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  activityTitle: {
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: tokens.lineHeightBase400,
    color: tokens.colorNeutralForeground1,
  },
  activitySubline: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  activitySummary: {
    display: 'inline-flex',
    alignItems: 'center',
    alignSelf: 'flex-start',
    gap: tokens.spacingHorizontalXS,
    border: 'none',
    backgroundColor: 'transparent',
    padding: `${tokens.spacingVerticalXXS} 0`,
    cursor: 'pointer',
    color: tokens.colorNeutralForeground2,
    fontFamily: tokens.fontFamilyBase,
  },
  activitySummaryText: {
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground2,
  },
  activitySummaryDivider: {
    color: tokens.colorNeutralForeground4,
  },
  activitySummaryAction: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
});
