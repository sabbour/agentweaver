import {
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  Title3,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import type { TokenUsageSummary } from '../api/types';
import { formatAic } from './CostChip';

export interface TokenUsagePanelProps {
  usage: TokenUsageSummary;
  title?: string;
}

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  panel: {
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  summaryRow: {
    display: 'flex',
    gap: tokens.spacingHorizontalXXL,
    flexWrap: 'wrap',
    marginBottom: tokens.spacingVerticalM,
  },
  statBlock: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  statLabel: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
  },
  statValue: {
    fontSize: tokens.fontSizeBase500,
    fontWeight: tokens.fontWeightSemibold,
  },
  headerCell: {
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground2,
  },
  modelCell: {
    width: '40%',
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  numericCell: {
    width: '20%',
    textAlign: 'right',
    fontVariantNumeric: 'tabular-nums',
  },
  tableWrapper: {
    overflowX: 'auto',
  },
  table: {
    minWidth: '560px',
    tableLayout: 'fixed',
  },
});

export function TokenUsagePanel({ usage, title = 'Token usage' }: TokenUsagePanelProps) {
  const styles = useStyles();

  return (
    <div className={styles.root}>
      {title && <Title3>{title}</Title3>}
      <div className={styles.panel}>
        <div className={styles.summaryRow}>
          <div className={styles.statBlock}>
            <Text className={styles.statLabel}>Total tokens</Text>
            <Text className={styles.statValue}>{usage.total_tokens.toLocaleString()}</Text>
          </div>
          <div className={styles.statBlock}>
            <Text className={styles.statLabel}>Input tokens</Text>
            <Text className={styles.statValue}>{usage.input_tokens.toLocaleString()}</Text>
          </div>
          <div className={styles.statBlock}>
            <Text className={styles.statLabel}>Output tokens</Text>
            <Text className={styles.statValue}>{usage.output_tokens.toLocaleString()}</Text>
          </div>
          <div className={styles.statBlock}>
            <Text className={styles.statLabel}>Total AICs</Text>
            <Text className={styles.statValue}>{formatAic(usage.total_nano_aiu)}</Text>
          </div>
        </div>

        {usage.by_model.length > 0 && (
          <div className={styles.tableWrapper}>
            <Table aria-label="Per-model token usage" size="small" className={styles.table}>
              <TableHeader>
                <TableRow>
                  <TableHeaderCell className={mergeClasses(styles.headerCell, styles.modelCell)}>Model</TableHeaderCell>
                  <TableHeaderCell className={mergeClasses(styles.headerCell, styles.numericCell)}>Input tokens</TableHeaderCell>
                  <TableHeaderCell className={mergeClasses(styles.headerCell, styles.numericCell)}>Output tokens</TableHeaderCell>
                  <TableHeaderCell className={mergeClasses(styles.headerCell, styles.numericCell)}>AICs</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {usage.by_model.map((row) => (
                  <TableRow key={row.model_id}>
                    <TableCell className={styles.modelCell} title={row.model_id}>{row.model_id}</TableCell>
                    <TableCell className={styles.numericCell}>{row.input_tokens.toLocaleString()}</TableCell>
                    <TableCell className={styles.numericCell}>{row.output_tokens.toLocaleString()}</TableCell>
                    <TableCell className={styles.numericCell}>{formatAic(row.total_nano_aiu)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        )}
      </div>
    </div>
  );
}
