import {
  Badge,
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import type { ProposedBacklogItem } from '../api/types';

const useStyles = makeStyles({
  itemList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    maxHeight: '400px',
    overflowY: 'auto',
    paddingRight: tokens.spacingHorizontalXS,
  },
  item: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    padding: tokens.spacingVerticalS,
    backgroundColor: tokens.colorNeutralBackground2,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  itemHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  itemTitle: {
    flex: 1,
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  itemDescription: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  capNotice: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    fontStyle: 'italic',
  },
  empty: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground3,
    fontStyle: 'italic',
    padding: `${tokens.spacingVerticalM} 0`,
  },
  loadingRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalM} 0`,
  },
});

export interface DecomposePreviewDialogProps {
  isOpen: boolean;
  onClose: () => void;
  onConfirm: () => Promise<void>;
  proposedItems: ProposedBacklogItem[];
  wasCapped: boolean;
  totalFound: number;
  isLoading: boolean;
  error?: string | null;
}

export function DecomposePreviewDialog({
  isOpen,
  onClose,
  onConfirm,
  proposedItems = [],
  wasCapped,
  totalFound,
  isLoading,
  error,
}: DecomposePreviewDialogProps) {
  const styles = useStyles();

  return (
    <Dialog open={isOpen} onOpenChange={(_, d) => { if (!d.open) onClose(); }}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Preview proposed backlog items</DialogTitle>
          <DialogContent>
            {isLoading ? (
              <div className={styles.loadingRow}>
                <Spinner size="extra-tiny" aria-hidden="true" />
                <Text>Analyzing spec file...</Text>
              </div>
            ) : error ? (
              <MessageBar intent="error">
                <MessageBarBody>{error}</MessageBarBody>
              </MessageBar>
            ) : proposedItems.length === 0 ? (
              <Text className={styles.empty}>No actionable items found in this file.</Text>
            ) : (
              <>
                {wasCapped && (
                  <Text className={styles.capNotice}>
                    Showing first {proposedItems.length} of {totalFound} items found.
                  </Text>
                )}
                <div className={styles.itemList}>
                  {proposedItems.map((item, i) => (
                    <div key={i} className={styles.item}>
                      <div className={styles.itemHeader}>
                        <Text className={styles.itemTitle}>{item.title}</Text>
                        {item.already_exists && (
                          <Badge appearance="tint" color="informative" size="small">
                            Already exists
                          </Badge>
                        )}
                      </div>
                      {item.description && (
                        <Text className={styles.itemDescription}>{item.description}</Text>
                      )}
                    </div>
                  ))}
                </div>
              </>
            )}
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onClose} disabled={isLoading}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={isLoading || !!error || proposedItems.length === 0}
              onClick={() => void onConfirm()}
            >
              {isLoading ? 'Loading...' : 'Create tasks'}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
