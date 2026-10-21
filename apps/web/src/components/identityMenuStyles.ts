import { makeStyles, tokens } from '@fluentui/react-components';

/** 0.x persona trigger and menu styles, shared by the 0.x GitHub identity badge and the v1 Broker identity menu. */
export const useIdentityMenuStyles = makeStyles({
  trigger: {
    cursor: 'pointer',
    minWidth: 0,
    width: '100%',
    justifyContent: 'flex-start',
  },
  triggerCollapsed: {
    // In the collapsed rail only the avatar shows, so center it.
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
    // Used when there is no avatar image, such as the v1 Broker menu whose
    // token carries no picture.
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
    // Long names are cut with an ellipsis so the rail width stays fixed.
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
    // Push the status and version to the right edge, across from Sign out.
    marginLeft: 'auto',
  },
});
