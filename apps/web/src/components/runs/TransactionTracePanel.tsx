import { useEffect, useMemo, useState } from 'react';
import { Badge, Text, makeStyles, tokens } from '@fluentui/react-components';
import {
  BotRegular,
  ChevronDownRegular,
  ChevronRightRegular,
  SparkleRegular,
  WrenchRegular,
} from '@fluentui/react-icons';
import type { RunTraceDto } from '../../api/types';
import { apiClient } from '../../api/apiClient';
import { MetricCardHeader, MetricEmptyState } from '../MetricTypography';
import { AgentIdentity } from '../AgentIdentity';
import { formatModelLabel } from '../../utils/agentIdentity';
import { buildTraceTree, collectExpandableKeys, findNode, type SpanType, type TraceNode } from './traceTree';

const useStyles = makeStyles({
  panel: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  header: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  body: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr)',
    gap: tokens.spacingHorizontalL,
    '@media (min-width: 900px)': {
      gridTemplateColumns: 'minmax(0, 1.4fr) minmax(0, 1fr)',
    },
  },
  tree: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    minWidth: 0,
  },
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    width: '100%',
    border: 'none',
    background: 'none',
    borderRadius: tokens.borderRadiusMedium,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    cursor: 'pointer',
    textAlign: 'left',
    minWidth: 0,
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
  },
  rowSelected: {
    backgroundColor: tokens.colorNeutralBackground1Selected,
  },
  chevron: {
    display: 'inline-flex',
    width: '16px',
    flexShrink: 0,
    color: tokens.colorNeutralForeground3,
  },
  chevronSpacer: {
    width: '16px',
    flexShrink: 0,
  },
  typeBadge: {
    flexShrink: 0,
  },
  rowName: {
    fontWeight: tokens.fontWeightSemibold,
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    minWidth: 0,
  },
  spacer: {
    flex: 1,
    minWidth: tokens.spacingHorizontalM,
  },
  duration: {
    flexShrink: 0,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
    fontFamily: tokens.fontFamilyMonospace,
  },
  detail: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalM,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    minWidth: 0,
    height: 'fit-content',
  },
  detailSectionTitle: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase200,
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
    color: tokens.colorNeutralForeground3,
  },
  detailGrid: {
    display: 'grid',
    gridTemplateColumns: 'auto 1fr',
    columnGap: tokens.spacingHorizontalM,
    rowGap: tokens.spacingVerticalXS,
  },
  detailKey: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  detailValue: {
    fontSize: tokens.fontSizeBase200,
    wordBreak: 'break-word',
  },
  detailEmpty: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
});

function typeLabel(type: SpanType): string {
  switch (type) {
    case 'invoke-agent': return 'Invoke Agent';
    case 'llm': return 'LLM';
    case 'tool': return 'Execute Tool';
  }
}

function typeIcon(type: SpanType) {
  switch (type) {
    case 'invoke-agent': return <BotRegular />;
    case 'llm': return <SparkleRegular />;
    case 'tool': return <WrenchRegular />;
  }
}

function typeBadgeColor(type: SpanType): 'brand' | 'success' | 'informative' {
  switch (type) {
    case 'invoke-agent': return 'brand';
    case 'llm': return 'success';
    case 'tool': return 'informative';
  }
}

function nodeName(node: TraceNode): string {
  const { span, type } = node;
  if (type === 'tool') return span.toolName ?? span.name;
  if (type === 'llm') return formatModelLabel(span.model) || span.name;
  return span.agentName ?? span.name;
}

function formatDurationMs(durationMs: number): string {
  const ms = Math.max(0, durationMs);
  if (ms < 1000) return `${Math.round(ms)} ms`;
  const seconds = ms / 1000;
  if (seconds < 60) return `${seconds.toFixed(seconds < 10 ? 1 : 0)} s`;
  return `${(seconds / 60).toFixed(1)} min`;
}

function formatNumber(value: number | null | undefined): string {
  return value == null ? '—' : value.toLocaleString();
}

function TraceRow({
  node,
  depth,
  expanded,
  selectedKey,
  roleByAgent,
  onToggle,
  onSelect,
  styles,
}: {
  node: TraceNode;
  depth: number;
  expanded: Set<string>;
  selectedKey: string | null;
  roleByAgent?: Record<string, string>;
  onToggle: (key: string) => void;
  onSelect: (node: TraceNode) => void;
  styles: ReturnType<typeof useStyles>;
}) {
  const hasChildren = node.children.length > 0;
  const isExpanded = expanded.has(node.key);
  const isSelected = selectedKey === node.key;
  const name = nodeName(node);

  return (
    <>
      <button
        type="button"
        className={`${styles.row} ${isSelected ? styles.rowSelected : ''}`}
        style={{ paddingLeft: `${depth * 20 + 8}px` }}
        onClick={() => onSelect(node)}
        aria-expanded={hasChildren ? isExpanded : undefined}
      >
        {hasChildren ? (
          <span
            className={styles.chevron}
            role="presentation"
            onClick={(event) => { event.stopPropagation(); onToggle(node.key); }}
          >
            {isExpanded ? <ChevronDownRegular /> : <ChevronRightRegular />}
          </span>
        ) : (
          <span className={styles.chevronSpacer} />
        )}
        <Badge
          className={styles.typeBadge}
          appearance="tint"
          color={typeBadgeColor(node.type)}
          size="small"
          icon={typeIcon(node.type)}
        >
          {typeLabel(node.type)}
        </Badge>
        {node.type === 'invoke-agent' ? (
          <span className={styles.rowName}>
            <AgentIdentity label={name} roleByAgent={roleByAgent} />
          </span>
        ) : (
          <Text className={styles.rowName} title={name}>{name}</Text>
        )}
        <span className={styles.spacer} />
        <Text className={styles.duration}>{formatDurationMs(node.span.durationMs)}</Text>
      </button>
      {hasChildren && isExpanded && node.children.map((child) => (
        <TraceRow
          key={child.key}
          node={child}
          depth={depth + 1}
          expanded={expanded}
          selectedKey={selectedKey}
          roleByAgent={roleByAgent}
          onToggle={onToggle}
          onSelect={onSelect}
          styles={styles}
        />
      ))}
    </>
  );
}

function DetailRow({ label, value, styles }: { label: string; value: string; styles: ReturnType<typeof useStyles> }) {
  return (
    <>
      <Text className={styles.detailKey}>{label}</Text>
      <Text className={styles.detailValue}>{value}</Text>
    </>
  );
}

function TraceDetail({
  node,
  roleByAgent,
  styles,
}: {
  node: TraceNode;
  roleByAgent?: Record<string, string>;
  styles: ReturnType<typeof useStyles>;
}) {
  const { span, type } = node;
  return (
    <div className={styles.detail}>
      <MetricCardHeader
        title={typeLabel(type)}
        subtitle={nodeName(node)}
        aside={<Badge appearance="tint" color={typeBadgeColor(type)} size="small" icon={typeIcon(type)}>{typeLabel(type)}</Badge>}
      />
      <div>
        <Text className={styles.detailSectionTitle}>Generative AI properties</Text>
        <div className={styles.detailGrid}>
          <DetailRow label="Event time" value={new Date(span.timestamp).toLocaleString()} styles={styles} />
          <DetailRow label="Duration" value={formatDurationMs(span.durationMs)} styles={styles} />
          <DetailRow label="Status" value={span.success ? 'Success' : (span.resultCode?.trim() || 'Failed')} styles={styles} />
          {span.operationName && <DetailRow label="Operation" value={span.operationName} styles={styles} />}
        </div>
      </div>
      {type === 'invoke-agent' && (
        <div>
          <Text className={styles.detailSectionTitle}>Agent</Text>
          <AgentIdentity label={span.agentName ?? span.name} roleByAgent={roleByAgent} />
        </div>
      )}
      <div>
        <Text className={styles.detailSectionTitle}>Resource properties</Text>
        <div className={styles.detailGrid}>
          {type === 'tool' ? (
            <DetailRow label="Tool" value={span.toolName ?? span.name} styles={styles} />
          ) : (
            <>
              <DetailRow label="Model" value={formatModelLabel(span.model)} styles={styles} />
              <DetailRow label="Input tokens" value={formatNumber(span.inputTokens)} styles={styles} />
              <DetailRow label="Output tokens" value={formatNumber(span.outputTokens)} styles={styles} />
            </>
          )}
        </div>
      </div>
    </div>
  );
}

export function TransactionTracePanel({
  runId,
  title = 'Transaction trace',
  subtitle = 'End-to-end agent, LLM and tool spans from AppInsights distributed traces.',
  roleByAgent,
}: {
  runId: string;
  title?: string;
  subtitle?: string;
  roleByAgent?: Record<string, string>;
}) {
  const styles = useStyles();
  const [trace, setTrace] = useState<RunTraceDto>({ runId, spans: [] });
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());

  useEffect(() => {
    let cancelled = false;
    setTrace({ runId, spans: [] });
    setSelectedKey(null);
    void apiClient.getRunTraces(runId)
      .then((next) => { if (!cancelled) setTrace(next); })
      .catch(() => { if (!cancelled) setTrace({ runId, spans: [] }); });
    return () => { cancelled = true; };
  }, [runId]);

  const tree = useMemo(() => buildTraceTree(trace.spans), [trace.spans]);

  useEffect(() => {
    // Expand every node with children by default so the full hierarchy is visible.
    setExpanded(collectExpandableKeys(tree, new Set<string>()));
  }, [tree]);

  const selectedNode = findNode(tree, selectedKey);

  function toggle(key: string) {
    setExpanded((current) => {
      const next = new Set(current);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }

  return (
    <div className={styles.panel}>
      <MetricCardHeader
        className={styles.header}
        title={title}
        subtitle={subtitle}
        aside={<Badge appearance="outline" size="small">AppInsights</Badge>}
      />
      {tree.length === 0 ? (
        <MetricEmptyState>No AppInsights trace data available for this run yet.</MetricEmptyState>
      ) : (
        <div className={styles.body}>
          <div className={styles.tree}>
            {tree.map((node) => (
              <TraceRow
                key={node.key}
                node={node}
                depth={0}
                expanded={expanded}
                selectedKey={selectedKey}
                roleByAgent={roleByAgent}
                onToggle={toggle}
                onSelect={(selected) => setSelectedKey(selected.key)}
                styles={styles}
              />
            ))}
          </div>
          {selectedNode ? (
            <TraceDetail node={selectedNode} roleByAgent={roleByAgent} styles={styles} />
          ) : (
            <div className={styles.detail}>
              <Text className={styles.detailEmpty}>Select a span to view its Generative AI properties.</Text>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
