import {
  apiClient } from '../../api/apiClient';
import {
  Badge,
  makeStyles,
  mergeClasses,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  BotRegular,
  CheckmarkCircleRegular,
  ChevronDownRegular,
  ChevronRightRegular,
  ErrorCircleRegular,
  SparkleRegular,
  WrenchRegular,
} from '@fluentui/react-icons';
import { formatModelLabel } from '../../utils/agentIdentity';
import { AgentIdentity } from '../AgentIdentity';
import { buildToolCallIndex,
  buildTraceTree,
  collectExpandableKeys,
  findNode } from './traceTree';
import { Body, EmptyState, TitleText } from '../ui';
import { useEffect, useMemo, useState } from 'react';
import type { RunTraceDto } from '../../api/types';
import type { SpanType, ToolCallDetail, TraceNode } from './traceTree';
import type { ReactNode } from 'react';

const useStyles = makeStyles({
  panel: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    padding: tokens.spacingVerticalL,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minWidth: 0,
  },
  panelHeaderWrapper: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  panelHeaderTitle: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    minWidth: 0,
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
    minWidth: 0,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  row: {
    width: '100%',
    border: 'none',
    background: 'none',
    borderRadius: tokens.borderRadiusMedium,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    cursor: 'pointer',
    textAlign: 'left',
    minWidth: 0,
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
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
    minWidth: 0,
    height: 'fit-content',
    backgroundColor: tokens.colorNeutralBackground2,
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    padding: tokens.spacingVerticalM,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  detailPanelHeader: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  detailSectionTitle: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase200,
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
  statusRow: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
  },
  statusIconSuccess: {
    fontSize: '14px',
    flexShrink: 0,
    color: tokens.colorStatusSuccessForeground1,
  },
  statusIconDanger: {
    fontSize: '14px',
    flexShrink: 0,
    color: tokens.colorStatusDangerForeground1,
  },
  codeBlock: {
    margin: 0,
    padding: tokens.spacingVerticalS,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
    maxHeight: '320px',
    overflow: 'auto',
  },
  codeBlockError: {
    backgroundColor: tokens.colorStatusDangerBackground1,
    color: tokens.colorStatusDangerForeground1,
    border: `1px solid ${tokens.colorStatusDangerBorder1}`,
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

function typeBadgeColor(type: SpanType): 'subtle' | 'success' | 'warning' {
  switch (type) {
    case 'invoke-agent': return 'subtle';
    case 'llm': return 'success';
    case 'tool': return 'warning';
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
        className={mergeClasses(styles.row, isSelected && styles.rowSelected)}
        style={{ paddingLeft: `${depth * 20 + 8}px` }}
        onClick={() => onSelect(node)}
        aria-expanded={hasChildren ? isExpanded : undefined}
        aria-pressed={isSelected}
        data-testid="trace-span"
        data-span-key={node.key}
        data-span-type={node.type}
        data-selected={isSelected ? 'true' : 'false'}
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

function DetailRow({ label, value, styles }: { label: string; value: ReactNode; styles: ReturnType<typeof useStyles> }) {
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
  toolCallIndex,
  styles,
}: {
  node: TraceNode;
  roleByAgent?: Record<string, string>;
  toolCallIndex: Map<string, ToolCallDetail>;
  styles: ReturnType<typeof useStyles>;
}) {
  const { span, type } = node;
  const toolDetail = type === 'tool' && span.toolCallId ? toolCallIndex.get(span.toolCallId) : undefined;
  return (
    <div className={styles.detail}>
      <div className={styles.detailPanelHeader}>
        <Badge appearance="tint" color={typeBadgeColor(type)} size="small" icon={typeIcon(type)}>
          {typeLabel(type)}
        </Badge>
        <TitleText as="h3">{nodeName(node)}</TitleText>
      </div>
      <div>
        <Text className={styles.detailSectionTitle}>Generative AI properties</Text>
        <div className={styles.detailGrid}>
          <DetailRow label="Event time" value={new Date(span.timestamp).toLocaleString()} styles={styles} />
          <DetailRow label="Duration" value={formatDurationMs(span.durationMs)} styles={styles} />
          <DetailRow
            label="Status"
            value={(
              <span className={styles.statusRow}>
                {span.success
                  ? <CheckmarkCircleRegular className={styles.statusIconSuccess} aria-hidden="true" />
                  : <ErrorCircleRegular className={styles.statusIconDanger} aria-hidden="true" />
                }
                <Text>{span.success ? 'Success' : (span.resultCode?.trim() || 'Failed')}</Text>
              </span>
            )}
            styles={styles}
          />
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
        <Text className={styles.detailSectionTitle}>Properties</Text>
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
      {type === 'tool' && (
        <>
          <div>
            <Text className={styles.detailSectionTitle}>Arguments</Text>
            {toolDetail?.arguments ? (
              <pre className={styles.codeBlock}>{JSON.stringify(toolDetail.arguments, null, 2)}</pre>
            ) : (
              <Text className={styles.detailValue}>No arguments recorded for this call.</Text>
            )}
          </div>
          <div>
            <Text className={styles.detailSectionTitle}>Output</Text>
            {toolDetail?.errorMessage ? (
              <pre className={mergeClasses(styles.codeBlock, styles.codeBlockError)}>{toolDetail.errorMessage}</pre>
            ) : toolDetail?.content ? (
              <pre className={styles.codeBlock}>{toolDetail.content}</pre>
            ) : (
              <Text className={styles.detailValue}>No output recorded for this call.</Text>
            )}
          </div>
        </>
      )}
    </div>
  );
}

export function TransactionTracePanel({
  runId,
  title = 'Transaction trace',
  subtitle = 'End-to-end agent, LLM, and tool spans from distributed traces.',
  roleByAgent,
}: {
  runId: string;
  title?: string;
  subtitle?: string;
  roleByAgent?: Record<string, string>;
}) {
  const styles = useStyles();
  const [trace, setTrace] = useState<RunTraceDto>({ runId, spans: [] });
  const [toolCallIndex, setToolCallIndex] = useState<Map<string, ToolCallDetail>>(new Map());
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());

  useEffect(() => {
    let cancelled = false;
    const loadTrace = async () => {
      setTrace({ runId, spans: [] });
      setToolCallIndex(new Map());
      setSelectedKey(null);
      try {
        const next = await apiClient.getRunTraces(runId);
        if (!cancelled) setTrace(next);
      } catch {
        if (!cancelled) setTrace({ runId, spans: [] });
      }
      try {
        // The persisted run event log carries tool.call/tool.result/tool.error payloads
        // (arguments + output) that the AppInsights-backed trace span itself lacks (#850).
        const events = await apiClient.getRunEvents(runId);
        if (!cancelled) setToolCallIndex(buildToolCallIndex(events));
      } catch {
        if (!cancelled) setToolCallIndex(new Map());
      }
    };
    void loadTrace();
    return () => { cancelled = true; };
  }, [runId]);

  const tree = useMemo(() => buildTraceTree(trace.spans), [trace.spans]);

  useEffect(() => {
    // Expand every node with children by default so the full hierarchy is visible.
    const syncExpanded = async () => {
      setExpanded(collectExpandableKeys(tree, new Set<string>()));
    };
    void syncExpanded();
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
    <div className={styles.panel} data-testid="transaction-trace-panel">
      <div className={styles.panelHeaderWrapper}>
        <div className={styles.panelHeaderTitle}>
          <TitleText as="h2">{title}</TitleText>
          <Badge appearance="outline" size="small">Distributed traces</Badge>
        </div>
        <Body tone="muted">{subtitle}</Body>
      </div>
      {tree.length === 0 ? (
        <EmptyState title="No trace data available for this run yet." />
      ) : (
        <div className={styles.body}>
          <div className={styles.tree} data-testid="trace-tree">
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
            <TraceDetail node={selectedNode} roleByAgent={roleByAgent} toolCallIndex={toolCallIndex} styles={styles} />
          ) : (
            <div className={styles.detail}>
              <EmptyState title="Select a span to view its details." />
            </div>
          )}
        </div>
      )}
    </div>
  );
}
