import {
  AzureDataGrid,
  AzureEmptyState,
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
  StatusIconText,
  Text,
  } from '../copilot-fluent-system';
import { makeStyles,
  mergeClasses,
  tokens,
} from '../copilot-fluent-system';
import type { ProposedBacklogItem } from '../api/types';
import type { AzfColumn } from '../copilot-fluent-system';
const useStyles = makeStyles({
  grid: {
    maxHeight: '400px',
    overflowY: 'auto',
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
  loadingRow: {
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
  const columns: AzfColumn<ProposedBacklogItem>[] = [
    {
      columnId: 'item',
      header: 'Backlog item',
      renderCell: (item) => (
        <div className="azf-stack azf-gap-xs">
          <Text className={styles.itemTitle}>{item.title}</Text>
          {item.description && <Text className={styles.itemDescription}>{item.description}</Text>}
        </div>
      ),
    },
    {
      columnId: 'state',
      header: 'State',
      width: '140px',
      renderCell: (item) => item.already_exists
        ? <StatusIconText status="info">Already exists</StatusIconText>
        : <StatusIconText status="success">New</StatusIconText>,
    },
  ];

  return (
    <Dialog open={isOpen} onOpenChange={(_, d) => { if (!d.open) onClose(); }}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Preview proposed backlog items</DialogTitle>
          <DialogContent>
            {isLoading ? (
              <div className={mergeClasses('azf-row azf-gap-s', styles.loadingRow)}>
                <Spinner size="extra-tiny" aria-hidden="true" />
                <Text>Analyzing spec file...</Text>
              </div>
            ) : error ? (
              <MessageBar intent="error">
                <MessageBarBody>{error}</MessageBarBody>
              </MessageBar>
            ) : (
              <>
                {wasCapped && (
                  <Text className={styles.capNotice}>
                    Showing first {proposedItems.length} of {totalFound} items found.
                  </Text>
                )}
                <AzureDataGrid
                  className={styles.grid}
                  items={proposedItems}
                  columns={columns}
                  getRowId={(item, index) => `${item.title}-${index}`}
                  ariaLabel="Proposed backlog items"
                  emptyState={<AzureEmptyState compact title="No actionable items found in this file." />}
                />
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
