import { makeStyles, tokens } from '@fluentui/react-components';

/**
 * Styles for the account button in the side bar and the menu it opens.
 * Shared by the 0.x GitHub identity badge and the v1 identity menu so both look the same.
 * The trigger and label rules use `minWidth: 0` and ellipsis so long names do not
 * widen a collapsed side bar.
 */
export const useIdentityMenuStyles = makeStyles({
  trigger: {
    cursor: 'pointer',
    minWidth: 0,
    width: '100%',
    justifyContent: 'flex-start',
  },
  triggerCollapsed: {
    justifyContent: 'center',
  },
  triggerInner: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    minWidth: 0,
    width: '100%',
  },
  triggerInnerCollapsed: {
    justifyContent: 'center',
  },
  avatar: {
    width: '28px',
    height: '28px',
    borderRadius: '50%',
    objectFit: 'cover',
    flexShrink: 0,
  },
  fallbackAvatar: {
    width: '28px',
    height: '28px',
    borderRadius: '50%',
    backgroundColor: tokens.colorNeutralBackground4,
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    flexShrink: 0,
    color: tokens.colorNeutralForeground2,
  },
  label: {
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    flex: 1,
  },
  chevron: {
    color: tokens.colorNeutralForeground3,
    flexShrink: 0,
  },
  popover: {
    width: '320px',
    maxWidth: 'calc(100vw - 32px)',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingHorizontalM,
  },
  identity: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    minWidth: 0,
  },
  identityDetails: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
  },
  secondary: {
    color: tokens.colorNeutralForeground2,
  },
  truncate: {
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: tokens.spacingVerticalS,
  },
  footer: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalS,
    minWidth: 0,
  },
  signOut: {
    justifyContent: 'flex-start',
  },
  footerMeta: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    minWidth: 0,
    marginLeft: 'auto',
  },
});
