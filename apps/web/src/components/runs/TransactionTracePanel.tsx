import { useEffect, useMemo, useState } from 'react';
import {
  Badge,
  MessageBar,
  MessageBarBody,
  Spinner,
  Tab,
  TabList,
  Text,
  makeStyles,
  mergeClasses,
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
import { apiClient } from '../../api/apiClient';
import type { PersistedRunEvent, RunTraceDto, RunTraceSpanDto } from '../../api/types';
import { formatModelLabel } from '../../utils/agentIdentity';
import { AgentIdentity } from '../AgentIdentity';
import { CostChip } from '../CostChip';
import { formatAic } from '../costChipFormat';
import { Body, EmptyState, TitleText } from '../ui';
import {
  aggregateNanoAiu,
  buildToolCallIndex,
  buildTraceTree,
  collectExpandableKeys,
  findNode,
  getTraceTimeline,
  normalizeType,
  totalNanoAiu,
} from './traceTree';
import type { SpanType, ToolCallDetail, TraceNode, TraceTimeline } from './traceTree';
import type { ReactNode } from 'react';

type TraceTab = 'timeline' | 'attributes' | 'events';
type BadgeColor = 'subtle' | 'success' | 'warning' | 'danger';

const useStyles = makeStyles({
  panel: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    padding: tokens.spacingVerticalL,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    minWidth: 0,
  },
  header: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  headerTitle: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  summary: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))',
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  summaryItem: {
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
    ':last-child': { borderRight: 'none' },
  },
  summaryLabel: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  summaryValue: {
    fontWeight: tokens.fontWeightSemibold,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  mono: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
  },
  tabs: {
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  timelineLayout: {
    display: 'grid',
    gap: tokens.spacingHorizontalL,
    minWidth: 0,
    '@media (min-width: 1100px)': {
      gridTemplateColumns: 'minmax(0, 1fr) minmax(300px, 360px)',
    },
  },
  timeline: {
    minWidth: 0,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    overflow: 'hidden',
  },
  axis: {
    display: 'grid',
    gridTemplateColumns: 'minmax(190px, 42%) minmax(160px, 1fr) 72px',
    columnGap: tokens.spacingHorizontalS,
    alignItems: 'end',
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalS}`,
    backgroundColor: tokens.colorNeutralBackground2,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
  },
  axisTitle: {
    fontWeight: tokens.fontWeightSemibold,
    letterSpacing: '0.04em',
  },
  axisTicks: {
    display: 'flex',
    justifyContent: 'space-between',
    fontFamily: tokens.fontFamilyMonospace,
    minWidth: 0,
  },
  axisEnd: {
    textAlign: 'right',
    fontWeight: tokens.fontWeightSemibold,
    letterSpacing: '0.04em',
  },
  treeRows: {
    display: 'flex',
    flexDirection: 'column',
  },
  row: {
    width: '100%',
    display: 'grid',
    gridTemplateColumns: 'minmax(190px, 42%) minmax(160px, 1fr) 72px',
    columnGap: tokens.spacingHorizontalS,
    alignItems: 'center',
    padding: `0 ${tokens.spacingHorizontalS}`,
    minHeight: '52px',
    border: 'none',
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
    backgroundColor: 'transparent',
    cursor: 'pointer',
    color: tokens.colorNeutralForeground1,
    textAlign: 'left',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '-2px' },
  },
  rowSelected: {
    backgroundColor: tokens.colorNeutralBackground1Selected,
    boxShadow: `inset 3px 0 0 ${tokens.colorBrandStroke1}`,
  },
  name: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    minWidth: 0,
  },
  chevron: {
    width: '16px',
    height: '16px',
    flexShrink: 0,
    display: 'inline-flex',
    alignItems: 'center',
    color: tokens.colorNeutralForeground3,
  },
  chevronSpacer: { width: '16px', height: '16px', flexShrink: 0 },
  nameCopy: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    minWidth: 0,
  },
  nameText: {
    fontWeight: tokens.fontWeightSemibold,
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  nameMeta: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  typeBadge: { flexShrink: 0 },
  timelineTrack: {
    height: '32px',
    position: 'relative',
    minWidth: 0,
    backgroundImage: `repeating-linear-gradient(90deg, transparent 0, transparent calc(25% - 1px), ${tokens.colorNeutralStroke2} calc(25% - 1px), ${tokens.colorNeutralStroke2} 25%)`,
  },
  bar: {
    position: 'absolute',
    top: '10px',
    height: '12px',
    minWidth: '4px',
    borderRadius: tokens.borderRadiusSmall,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    backgroundColor: tokens.colorNeutralBackground4,
  },
  barAgent: {
    backgroundColor: tokens.colorBrandBackground2,
  },
  barLlm: {
    backgroundColor: tokens.colorStatusSuccessBackground2,
  },
  barTool: {
    backgroundColor: tokens.colorStatusWarningBackground2,
  },
  barFailed: {
    backgroundColor: tokens.colorStatusDangerBackground2,
  },
  barSelected: {
    outline: `2px solid ${tokens.colorStrokeFocus2}`,
    outlineOffset: '1px',
  },
  duration: {
    justifySelf: 'end',
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    fontFamily: tokens.fontFamilyMonospace,
    whiteSpace: 'nowrap',
  },
  inspector: {
    minWidth: 0,
    height: 'fit-content',
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: tokens.spacingVerticalM,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  inspectorHeader: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  inspectorName: {
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
    overflowWrap: 'anywhere',
  },
  sectionTitle: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    display: 'block',
    marginBottom: tokens.spacingVerticalXS,
  },
  detailGrid: {
    display: 'grid',
    gridTemplateColumns: 'minmax(92px, auto) minmax(0, 1fr)',
    columnGap: tokens.spacingHorizontalM,
    rowGap: tokens.spacingVerticalXS,
  },
  detailKey: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  detailValue: {
    fontSize: tokens.fontSizeBase200,
    overflowWrap: 'anywhere',
  },
  status: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
  },
  statusSuccess: { color: tokens.colorStatusSuccessForeground1, fontSize: '16px' },
  statusFailed: { color: tokens.colorStatusDangerForeground1, fontSize: '16px' },
  codeBlock: {
    margin: 0,
    padding: tokens.spacingVerticalS,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    whiteSpace: 'pre-wrap',
    overflowWrap: 'anywhere',
    maxHeight: '280px',
    overflow: 'auto',
  },
  codeBlockError: {
    backgroundColor: tokens.colorStatusDangerBackground1,
    color: tokens.colorStatusDangerForeground1,
    border: `1px solid ${tokens.colorStatusDangerBorder1}`,
  },
  attributes: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr)',
    gap: tokens.spacingVerticalM,
    '@media (min-width: 900px)': { gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr)' },
  },
  attributeGroup: {
    padding: tokens.spacingVerticalM,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    minWidth: 0,
  },
  attributeList: {
    display: 'grid',
    gridTemplateColumns: 'minmax(120px, 40%) minmax(0, 1fr)',
    columnGap: tokens.spacingHorizontalM,
    rowGap: tokens.spacingVerticalS,
  },
  attributeName: {
    color: tokens.colorNeutralForeground3,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
  },
  attributeValue: {
    margin: 0,
    fontSize: tokens.fontSizeBase200,
    overflowWrap: 'anywhere',
  },
  events: {
    display: 'flex',
    flexDirection: 'column',
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  event: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto',
    columnGap: tokens.spacingHorizontalM,
    alignItems: 'center',
    padding: `${tokens.spacingVerticalM} 0`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  eventTitle: {
    fontWeight: tokens.fontWeightSemibold,
    fontFamily: tokens.fontFamilyMonospace,
    overflowWrap: 'anywhere',
  },
  eventContext: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    marginTop: tokens.spacingVerticalXXS,
    overflowWrap: 'anywhere',
  },
  eventPayload: {
    gridColumn: '1 / -1',
    marginTop: tokens.spacingVerticalS,
  },
  eventPayloadSummary: {
    color: tokens.colorBrandForegroundLink,
    cursor: 'pointer',
    fontSize: tokens.fontSizeBase200,
  },
  unavailable: {
    padding: tokens.spacingVerticalM,
    color: tokens.colorNeutralForeground3,
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

function typeBadgeColor(type: SpanType): Exclude<BadgeColor, 'danger'> {
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

function formatDateTime(timestamp: string): string {
  const value = new Date(timestamp);
  return Number.isNaN(value.getTime()) ? '—' : value.toLocaleString();
}

function timelinePercent(value: number, total: number): number {
  if (total <= 0) return 0;
  return Math.max(0, Math.min(100, value / total * 100));
}

function timelinePlacement(span: RunTraceSpanDto, timeline: TraceTimeline | null) {
  const startedAtMs = new Date(span.timestamp).getTime();
  if (!timeline || !Number.isFinite(startedAtMs)) return { left: 0, width: 1.5 };
  const left = timelinePercent(startedAtMs - timeline.startedAtMs, timeline.durationMs);
  const width = timeline.durationMs > 0
    ? Math.max(1.5, timelinePercent(Math.max(0, span.durationMs), timeline.durationMs))
    : 1.5;
  return { left: Math.min(left, 98.5), width: Math.min(width, 100 - left) };
}

function rawTokenTotals(spans: RunTraceSpanDto[]) {
  const llmSpans = spans.filter((span) => normalizeType(span) === 'llm');
  const tokenSpans = llmSpans.length > 0
    ? llmSpans
    : spans.filter((span) => span.inputTokens != null || span.outputTokens != null);
  const inputPresent = tokenSpans.some((span) => span.inputTokens != null);
  const outputPresent = tokenSpans.some((span) => span.outputTokens != null);
  if (!inputPresent && !outputPresent) return null;
  return {
    input: tokenSpans.reduce((total, span) => total + (span.inputTokens ?? 0), 0),
    output: tokenSpans.reduce((total, span) => total + (span.outputTokens ?? 0), 0),
    inputPresent,
    outputPresent,
  };
}

function formatTokenTotals(totals: ReturnType<typeof rawTokenTotals>): string {
  if (!totals) return '—';
  const values: string[] = [];
  if (totals.inputPresent) values.push(`${totals.input.toLocaleString()} input`);
  if (totals.outputPresent) values.push(`${totals.output.toLocaleString()} output`);
  return values.join(' · ');
}

function traceAgent(spans: RunTraceSpanDto[]): string | null {
  return [...spans]
    .sort((left, right) => new Date(left.timestamp).getTime() - new Date(right.timestamp).getTime())
    .find((span) => normalizeType(span) === 'invoke-agent' && span.agentName?.trim())?.agentName ?? null;
}

function traceSucceeded(spans: RunTraceSpanDto[]): boolean {
  return spans.every((span) => span.success);
}

function DetailRow({ label, value, styles }: { label: string; value: ReactNode; styles: ReturnType<typeof useStyles> }) {
  return (
    <>
      <Text className={styles.detailKey}>{label}</Text>
      <Text className={styles.detailValue}>{value}</Text>
    </>
  );
}

function SpanStatus({ span, styles }: { span: RunTraceSpanDto; styles: ReturnType<typeof useStyles> }) {
  return (
    <span className={styles.status}>
      {span.success
        ? <CheckmarkCircleRegular className={styles.statusSuccess} aria-hidden="true" />
        : <ErrorCircleRegular className={styles.statusFailed} aria-hidden="true" />
      }
      <Text>{span.success ? 'Success' : (span.resultCode?.trim() || 'Failed')}</Text>
    </span>
  );
}

function TraceRow({
  node,
  depth,
  expanded,
  selectedKey,
  roleByAgent,
  timeline,
  onToggle,
  onSelect,
  styles,
}: {
  node: TraceNode;
  depth: number;
  expanded: Set<string>;
  selectedKey: string | null;
  roleByAgent?: Record<string, string>;
  timeline: TraceTimeline | null;
  onToggle: (key: string) => void;
  onSelect: (node: TraceNode) => void;
  styles: ReturnType<typeof useStyles>;
}) {
  const hasChildren = node.children.length > 0;
  const isExpanded = expanded.has(node.key);
  const isSelected = selectedKey === node.key;
  const name = nodeName(node);
  const placement = timelinePlacement(node.span, timeline);
  const barStyle = {
    left: `${placement.left}%`,
    width: `${placement.width}%`,
  };
  const barKind = node.type === 'invoke-agent'
    ? styles.barAgent
    : node.type === 'llm'
      ? styles.barLlm
      : styles.barTool;

  return (
    <>
      <button
        type="button"
        className={mergeClasses(styles.row, isSelected && styles.rowSelected)}
        onClick={() => onSelect(node)}
        aria-expanded={hasChildren ? isExpanded : undefined}
        aria-pressed={isSelected}
        data-testid="trace-span"
        data-span-key={node.key}
        data-span-type={node.type}
        data-selected={isSelected ? 'true' : 'false'}
      >
        <span className={styles.name} style={{ paddingLeft: `${depth * 20}px` }}>
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
          <span className={styles.nameCopy}>
            {node.type === 'invoke-agent' ? (
              <span className={styles.nameText}><AgentIdentity label={name} roleByAgent={roleByAgent} /></span>
            ) : (
              <Text className={styles.nameText} title={name}>{name}</Text>
            )}
            <Text className={styles.nameMeta}>
              {node.span.success ? 'Success' : (node.span.resultCode?.trim() || 'Failed')}
              {node.synthetic ? ' · derived model call' : ''}
            </Text>
          </span>
        </span>
        <span className={styles.timelineTrack}>
          <span
            className={mergeClasses(styles.bar, barKind, !node.span.success && styles.barFailed, isSelected && styles.barSelected)}
            style={barStyle}
            data-testid="trace-duration-bar"
            data-span-key={node.key}
            data-span-offset={placement.left.toFixed(2)}
            data-span-width={placement.width.toFixed(2)}
          />
        </span>
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
          timeline={timeline}
          onToggle={onToggle}
          onSelect={onSelect}
          styles={styles}
        />
      ))}
    </>
  );
}

function TraceInspector({
  node,
  roleByAgent,
  toolCallIndex,
  styles,
}: {
  node: TraceNode | null;
  roleByAgent?: Record<string, string>;
  toolCallIndex: Map<string, ToolCallDetail>;
  styles: ReturnType<typeof useStyles>;
}) {
  if (!node) {
    return (
      <aside className={styles.inspector} aria-label="Span inspector">
        <EmptyState title="Select a span to inspect its trace context." />
      </aside>
    );
  }

  const { span, type } = node;
  const toolDetail = type === 'tool' && span.toolCallId ? toolCallIndex.get(span.toolCallId) : undefined;
  const nodeCost = aggregateNanoAiu(node);
  const costLabel = type === 'invoke-agent' ? 'AIC (invocation)' : type === 'llm' ? 'AIC (model call)' : 'AIC';
  return (
    <aside className={styles.inspector} aria-label="Span inspector">
      <div className={styles.inspectorHeader}>
        <Badge appearance="tint" color={typeBadgeColor(type)} size="small" icon={typeIcon(type)}>
          {typeLabel(type)}
        </Badge>
        <Text className={styles.inspectorName}>{nodeName(node)}</Text>
      </div>
      <div>
        <Text className={styles.sectionTitle}>Trace context</Text>
        <div className={styles.detailGrid}>
          <DetailRow label="Event time" value={formatDateTime(span.timestamp)} styles={styles} />
          <DetailRow label="Duration" value={formatDurationMs(span.durationMs)} styles={styles} />
          <DetailRow label="Status" value={<SpanStatus span={span} styles={styles} />} styles={styles} />
          {span.resultCode && <DetailRow label="Result code" value={span.resultCode} styles={styles} />}
          {span.operationName && <DetailRow label="Operation" value={span.operationName} styles={styles} />}
          <DetailRow label={costLabel} value={nodeCost > 0 ? `${formatAic(nodeCost)} AIC` : '—'} styles={styles} />
        </div>
      </div>
      {type === 'invoke-agent' && (
        <div>
          <Text className={styles.sectionTitle}>Agent</Text>
          <AgentIdentity label={span.agentName ?? span.name} roleByAgent={roleByAgent} />
        </div>
      )}
      <div>
        <Text className={styles.sectionTitle}>{type === 'tool' ? 'Tool call' : 'Model usage'}</Text>
        <div className={styles.detailGrid}>
          {type === 'tool' ? (
            <>
              <DetailRow label="Tool" value={span.toolName ?? span.name} styles={styles} />
              {span.toolCallId && <DetailRow label="Call ID" value={<code>{span.toolCallId}</code>} styles={styles} />}
            </>
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
            <Text className={styles.sectionTitle}>Arguments</Text>
            {toolDetail?.arguments ? (
              <pre className={styles.codeBlock}>{JSON.stringify(toolDetail.arguments, null, 2)}</pre>
            ) : (
              <Text className={styles.detailValue}>No arguments recorded for this call.</Text>
            )}
          </div>
          <div>
            <Text className={styles.sectionTitle}>Output</Text>
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
    </aside>
  );
}

function AttributeGroup({
  title,
  values,
  styles,
}: {
  title: string;
  values: Array<[string, ReactNode]>;
  styles: ReturnType<typeof useStyles>;
}) {
  return (
    <section className={styles.attributeGroup}>
      <Text className={styles.sectionTitle}>{title}</Text>
      <dl className={styles.attributeList}>
        {values.map(([key, value]) => (
          <span key={key} style={{ display: 'contents' }}>
            <dt className={styles.attributeName}>{key}</dt>
            <dd className={styles.attributeValue}>{value}</dd>
          </span>
        ))}
      </dl>
    </section>
  );
}

function TraceAttributes({ node, styles }: { node: TraceNode | null; styles: ReturnType<typeof useStyles> }) {
  if (!node) {
    return <EmptyState title="Select a span in the Timeline to view its attributes." />;
  }

  const { span, type } = node;
  const traceValues: Array<[string, ReactNode]> = [
    ['span.id', <code key="id">{span.id}</code>],
    ['parent.id', span.parentId ? <code key="parent">{span.parentId}</code> : 'Root span'],
    ['span.type', type],
    ['timestamp', formatDateTime(span.timestamp)],
    ['duration', formatDurationMs(span.durationMs)],
    ['status', <SpanStatus key="status" span={span} styles={styles} />],
  ];
  if (span.resultCode) traceValues.push(['result.code', span.resultCode]);
  if (span.operationName) traceValues.push(['operation.name', span.operationName]);

  const semanticValues: Array<[string, ReactNode]> = [];
  if (span.agentName) semanticValues.push(['agent.name', span.agentName]);
  if (span.toolName) semanticValues.push(['tool.name', span.toolName]);
  if (span.toolCallId) semanticValues.push(['tool.call.id', <code key="call">{span.toolCallId}</code>]);
  if (span.model) semanticValues.push(['model', formatModelLabel(span.model)]);
  if (span.inputTokens != null) semanticValues.push(['usage.input_tokens', formatNumber(span.inputTokens)]);
  if (span.outputTokens != null) semanticValues.push(['usage.output_tokens', formatNumber(span.outputTokens)]);
  if (span.totalNanoAiu != null) semanticValues.push(['agentweaver.aiu.nano', formatNumber(span.totalNanoAiu)]);

  return (
    <div className={styles.attributes}>
      <AttributeGroup title="Trace attributes" values={traceValues} styles={styles} />
      <AttributeGroup
        title="Generative AI attributes"
        values={semanticValues.length > 0 ? semanticValues : [['availability', 'No additional semantic attributes were recorded.']]}
        styles={styles}
      />
    </div>
  );
}

function getEventString(payload: Record<string, unknown>, key: string): string | null {
  const value = payload[key];
  return typeof value === 'string' && value.trim() ? value : null;
}

function eventContext(event: PersistedRunEvent): string {
  const callId = getEventString(event.payload, 'callId');
  const toolName = getEventString(event.payload, 'toolName');
  const parts = [`Sequence ${event.sequence}`];
  const timestamp = getEventString(event.payload, 'timestamp_utc')
    ?? getEventString(event.payload, 'timestampUtc')
    ?? getEventString(event.payload, 'timestamp');
  if (timestamp) parts.push(formatDateTime(timestamp));
  if (toolName) parts.push(`Tool ${toolName}`);
  if (callId) parts.push(`Call ${callId}`);
  return parts.join(' · ');
}

function eventBadge(eventType: string): { label: string; color: BadgeColor } {
  if (/error|fail/i.test(eventType)) return { label: 'Error', color: 'danger' };
  if (/tool\.call/i.test(eventType)) return { label: 'Call', color: 'warning' };
  if (/tool\.result/i.test(eventType)) return { label: 'Result', color: 'success' };
  return { label: 'Recorded', color: 'subtle' };
}

function eventPayload(event: PersistedRunEvent): string {
  try {
    return JSON.stringify(event.payload, null, 2);
  } catch {
    return 'Payload could not be formatted.';
  }
}

function TraceEvents({
  events,
  availability,
  styles,
}: {
  events: PersistedRunEvent[];
  availability: 'loading' | 'loaded' | 'unavailable';
  styles: ReturnType<typeof useStyles>;
}) {
  if (availability === 'loading') return <Spinner label="Loading persisted events" />;
  if (availability === 'unavailable') {
    return <Text className={styles.unavailable}>Persisted run events are not available for this trace.</Text>;
  }
  if (events.length === 0) return <EmptyState title="No persisted events are available for this trace." />;

  return (
    <div className={styles.events} aria-label="Persisted trace events">
      {events.map((event) => {
        const badge = eventBadge(event.type);
        const payloadKeys = Object.keys(event.payload);
        return (
          <article className={styles.event} key={`${event.sequence}-${event.type}`}>
            <div>
              <Text className={styles.eventTitle}>{event.type}</Text>
              <Text className={styles.eventContext}>{eventContext(event)}</Text>
            </div>
            <Badge appearance="tint" color={badge.color} size="small">{badge.label}</Badge>
            {payloadKeys.length > 0 && (
              <details className={styles.eventPayload}>
                <summary className={styles.eventPayloadSummary}>
                  {payloadKeys.length} recorded payload {payloadKeys.length === 1 ? 'field' : 'fields'}
                </summary>
                <pre className={styles.codeBlock}>{eventPayload(event)}</pre>
              </details>
            )}
          </article>
        );
      })}
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
  const [events, setEvents] = useState<PersistedRunEvent[]>([]);
  const [eventsAvailability, setEventsAvailability] = useState<'loading' | 'loaded' | 'unavailable'>('loading');
  const [loading, setLoading] = useState(true);
  const [toolCallIndex, setToolCallIndex] = useState<Map<string, ToolCallDetail>>(new Map());
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [activeTab, setActiveTab] = useState<TraceTab>('timeline');

  useEffect(() => {
    let cancelled = false;
    const loadTrace = async () => {
      setLoading(true);
      setTrace({ runId, spans: [] });
      setEvents([]);
      setEventsAvailability('loading');
      setToolCallIndex(new Map());
      setSelectedKey(null);
      setActiveTab('timeline');
      try {
        const next = await apiClient.getRunTraces(runId);
        if (!cancelled) {
          const nextTree = buildTraceTree(next.spans);
          setTrace(next);
          setExpanded(collectExpandableKeys(nextTree, new Set<string>()));
          setSelectedKey(nextTree[0]?.key ?? null);
        }
      } catch {
        if (!cancelled) {
          setTrace({ runId, spans: [] });
          setExpanded(new Set());
          setSelectedKey(null);
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
      try {
        const nextEvents = await apiClient.getRunEvents(runId);
        if (!cancelled) {
          setEvents(nextEvents);
          setToolCallIndex(buildToolCallIndex(nextEvents));
          setEventsAvailability('loaded');
        }
      } catch {
        if (!cancelled) {
          setToolCallIndex(new Map());
          setEventsAvailability('unavailable');
        }
      }
    };
    void loadTrace();
    return () => { cancelled = true; };
  }, [runId]);

  const tree = useMemo(() => buildTraceTree(trace.spans), [trace.spans]);
  const timeline = useMemo(() => getTraceTimeline(trace.spans), [trace.spans]);
  const runTotalNanoAiu = useMemo(() => totalNanoAiu(tree), [tree]);
  const tokens = useMemo(() => rawTokenTotals(trace.spans), [trace.spans]);
  const agent = useMemo(() => traceAgent(trace.spans), [trace.spans]);

  const selectedNode = findNode(tree, selectedKey);

  function toggle(key: string) {
    setExpanded((current) => {
      const next = new Set(current);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }

  const axisTicks = timeline
    ? [0, 0.25, 0.5, 0.75, 1].map((fraction) => formatDurationMs(timeline.durationMs * fraction))
    : ['0 ms', '—', '—', '—', '—'];

  return (
    <section className={styles.panel} data-testid="transaction-trace-panel">
      <header className={styles.header}>
        <div className={styles.headerTitle}>
          <TitleText as="h2">{title}</TitleText>
          <Badge appearance="outline" size="small">Distributed trace</Badge>
          {runTotalNanoAiu > 0 && (
            <CostChip
              totalNanoAiu={runTotalNanoAiu}
              ariaLabel={`Total AI credit cost for this run ${formatAic(runTotalNanoAiu)} AIC`}
            />
          )}
        </div>
        <Body tone="muted">{subtitle}</Body>
      </header>

      {!loading && trace.queryError && (
        <MessageBar intent="warning">
          <MessageBarBody>{trace.queryError}</MessageBarBody>
        </MessageBar>
      )}

      {loading ? (
        <Spinner label="Loading transaction trace" />
      ) : tree.length === 0 ? (
        <EmptyState title="No trace data available for this run yet." />
      ) : (
        <>
          <dl className={styles.summary} aria-label="Trace summary">
            <div className={styles.summaryItem}>
              <dt className={styles.summaryLabel}>Agent</dt>
              <dd className={styles.summaryValue}>{agent ? <AgentIdentity label={agent} roleByAgent={roleByAgent} /> : '—'}</dd>
            </div>
            <div className={styles.summaryItem}>
              <dt className={styles.summaryLabel}>Run ID</dt>
              <dd className={mergeClasses(styles.summaryValue, styles.mono)} title={runId}>{runId}</dd>
            </div>
            <div className={styles.summaryItem}>
              <dt className={styles.summaryLabel}>Trace duration</dt>
              <dd className={styles.summaryValue}>{timeline ? formatDurationMs(timeline.durationMs) : '—'}</dd>
            </div>
            {tokens && (
              <div className={styles.summaryItem}>
                <dt className={styles.summaryLabel}>Tokens</dt>
                <dd className={styles.summaryValue}>{formatTokenTotals(tokens)}</dd>
              </div>
            )}
            <div className={styles.summaryItem}>
              <dt className={styles.summaryLabel}>Trace status</dt>
              <dd className={styles.summaryValue}>
                <Badge
                  appearance="tint"
                  color={traceSucceeded(trace.spans) ? 'success' : 'danger'}
                  icon={traceSucceeded(trace.spans) ? <CheckmarkCircleRegular /> : <ErrorCircleRegular />}
                >
                  {traceSucceeded(trace.spans) ? 'Success' : 'Failed'}
                </Badge>
              </dd>
            </div>
          </dl>

          <TabList
            className={styles.tabs}
            selectedValue={activeTab}
            onTabSelect={(_, data) => setActiveTab(data.value as TraceTab)}
            aria-label="Trace detail views"
          >
            <Tab value="timeline">Timeline</Tab>
            <Tab value="attributes">Attributes</Tab>
            <Tab value="events">Events</Tab>
          </TabList>

          {activeTab === 'timeline' && (
            <div className={styles.timelineLayout}>
              <div className={styles.timeline} data-testid="trace-timeline">
                <div className={styles.axis}>
                  <Text className={styles.axisTitle}>SPAN</Text>
                  <span className={styles.axisTicks}>
                    {axisTicks.map((tick, index) => <span key={`${tick}-${index}`}>{tick}</span>)}
                  </span>
                  <Text className={styles.axisEnd}>DURATION</Text>
                </div>
                <div className={styles.treeRows} data-testid="trace-tree">
                  {tree.map((node) => (
                    <TraceRow
                      key={node.key}
                      node={node}
                      depth={0}
                      expanded={expanded}
                      selectedKey={selectedKey}
                      roleByAgent={roleByAgent}
                      timeline={timeline}
                      onToggle={toggle}
                      onSelect={(selected) => setSelectedKey(selected.key)}
                      styles={styles}
                    />
                  ))}
                </div>
              </div>
              <TraceInspector node={selectedNode} roleByAgent={roleByAgent} toolCallIndex={toolCallIndex} styles={styles} />
            </div>
          )}

          {activeTab === 'attributes' && <TraceAttributes node={selectedNode} styles={styles} />}
          {activeTab === 'events' && <TraceEvents events={events} availability={eventsAvailability} styles={styles} />}
        </>
      )}
    </section>
  );
}
