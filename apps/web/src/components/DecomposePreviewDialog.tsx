import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DialogTrigger,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { CheckmarkCircleRegular, InfoRegular } from '@fluentui/react-icons';
import { EmptyState } from './ui';
import type { ProposedBacklogItem } from '../api/types';

const useStyles = makeStyles({
  grid: {
    maxHeight: '400px',
    overflowY: 'auto',
  },
  itemStack: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
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
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalM} 0`,
  },
  statusRow: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
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
          <DialogTitle
              action={
                <DialogTrigger disableButtonEnhancement>
                  <Button appearance="subtle" aria-label="Close" icon={<DismissRegular />} />
                </DialogTrigger>
              }
            >Preview proposed backlog items</DialogTitle>
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
            ) : (
              <>
                {wasCapped && (
                  <Text className={styles.capNotice}>
                    Showing first {proposedItems.length} of {totalFound} items found.
                  </Text>
                )}
                {proposedItems.length === 0 ? (
                  <EmptyState title="No actionable items found in this file." />
                ) : (
                  <div className={styles.grid}>
                    <Table aria-label="Proposed backlog items">
                      <TableHeader>
                        <TableRow>
                          <TableHeaderCell>Backlog item</TableHeaderCell>
                          <TableHeaderCell style={{ width: '140px' }}>State</TableHeaderCell>
                        </TableRow>
                      </TableHeader>
                      <TableBody>
                        {proposedItems.map((item, index) => (
                          <TableRow key={`${item.title}-${index}`}>
                            <TableCell>
                              <div className={styles.itemStack}>
                                <Text className={styles.itemTitle}>{item.title}</Text>
                                {item.description && (
                                  <Text className={styles.itemDescription}>{item.description}</Text>
                                )}
                              </div>
                            </TableCell>
                            <TableCell>
                              {item.already_exists ? (
                                <span className={styles.statusRow}>
                                  <InfoRegular fontSize={14} aria-hidden="true" />
                                  <Text size={200}>Already exists</Text>
                                </span>
                              ) : (
                                <span className={styles.statusRow}>
                                  <CheckmarkCircleRegular fontSize={14} aria-hidden="true" />
                                  <Text size={200}>New</Text>
                                </span>
                              )}
                            </TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  </div>
                )}
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
