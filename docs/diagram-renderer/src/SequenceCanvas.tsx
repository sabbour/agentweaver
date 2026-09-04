import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { badgeTones, fontFamily, neutral, radius } from './theme';
import { iconRegistry } from './icons';
import type {
  SequenceFragment,
  SequenceMessage,
  SequenceParticipant,
  SequenceSpec,
  SequenceStep,
} from './types';

const CARD_W = 196;
const CARD_H = 82;
const COL_GAP = 54;
const MARGIN_X = 54;
const TOP = 34;
const LIFELINE_TOP = TOP + CARD_H;
const STEP_GAP = 54;
const SELF_GAP = 70;
const NOTE_GAP = 68;
const FRAGMENT_PAD = 38;
const BOTTOM_CARD_GAP = 28;

interface PositionedStep {
  step: SequenceStep;
  y: number;
  height: number;
  children?: PositionedSection[];
}

interface PositionedSection {
  label?: string;
  y: number;
  height: number;
  steps: PositionedStep[];
}

interface ActivationSpan {
  participant: string;
  y1: number;
  y2: number;
  depth: number;
}

function plainText(value: string): string {
  return value.replace(/<br\s*\/?>|\\n/gi, ' ').replace(/\s+/g, ' ').trim();
}

function messageHeight(message: SequenceMessage, xById: Map<string, number>): number {
  if (message.from === message.to) return SELF_GAP;
  const width = Math.max(120, Math.abs((xById.get(message.to) ?? 0) - (xById.get(message.from) ?? 0)) - 28);
  const lines = Math.max(1, Math.ceil(plainText(message.label).length * 7.2 / width));
  return STEP_GAP + Math.max(0, lines - 1) * 16;
}

function layoutSteps(
  steps: SequenceStep[],
  startY: number,
  xById: Map<string, number>,
  activations: ActivationSpan[],
  active: Map<string, { y: number; depth: number }[]>,
): { positioned: PositionedStep[]; endY: number } {
  const positioned: PositionedStep[] = [];
  let y = startY;
  for (const step of steps) {
    if (step.type === 'activation') {
      const stack = active.get(step.participant) ?? [];
      if (step.action === 'start') {
        stack.push({ y, depth: stack.length });
        active.set(step.participant, stack);
      } else {
        const opened = stack.pop();
        if (opened) activations.push({ participant: step.participant, y1: opened.y, y2: y, depth: opened.depth });
      }
      positioned.push({ step, y, height: 0 });
      continue;
    }
    if (step.type === 'fragment') {
      const children: PositionedSection[] = [];
      let innerY = y + FRAGMENT_PAD;
      for (const section of step.sections) {
        const sectionStart = innerY;
        const laidOut = layoutSteps(section.steps, innerY + (section.label ? 22 : 4), xById, activations, active);
        innerY = laidOut.endY + 10;
        children.push({ label: section.label, y: sectionStart, height: innerY - sectionStart, steps: laidOut.positioned });
      }
      const height = Math.max(86, innerY - y + 8);
      positioned.push({ step, y, height, children });
      y += height + 20;
      continue;
    }
    const height = step.type === 'message' ? messageHeight(step, xById) : NOTE_GAP;
    positioned.push({ step, y, height });
    y += height;
  }
  return { positioned, endY: y };
}

function participantRange(steps: SequenceStep[], indexes: Map<string, number>): [number, number] {
  const values: number[] = [];
  const visit = (items: SequenceStep[]) => {
    for (const step of items) {
      if (step.type === 'message') {
        values.push(indexes.get(step.from) ?? 0, indexes.get(step.to) ?? 0);
      } else if (step.type === 'note') {
        for (const id of step.over) values.push(indexes.get(id) ?? 0);
      } else if (step.type === 'fragment') {
        for (const section of step.sections) visit(section.steps);
      }
    }
  };
  visit(steps);
  return values.length ? [Math.min(...values), Math.max(...values)] : [0, indexes.size - 1];
}

function ParticipantCard({ participant, x, y }: { participant: SequenceParticipant; x: number; y: number }) {
  const tone = badgeTones[participant.badge.tone];
  const Icon = iconRegistry[participant.icon];
  return (
    <div
      style={{
        position: 'absolute',
        left: x - CARD_W / 2,
        top: y,
        width: CARD_W,
        height: CARD_H,
        boxSizing: 'border-box',
        display: 'flex',
        alignItems: 'center',
        gap: 10,
        padding: '13px 12px 12px 15px',
        background: neutral.background1,
        border: `1px solid ${neutral.stroke2}`,
        borderLeft: `5px solid ${tone.fg}`,
        borderRadius: radius.card,
        boxShadow: '0 2px 5px rgba(28,24,20,0.06), 0 8px 20px rgba(28,24,20,0.06)',
        fontFamily,
        zIndex: 4,
      }}
    >
      <span style={{ display: 'flex', flexShrink: 0, color: tone.fg }}><Icon fontSize={24} /></span>
      <span style={{ minWidth: 0, flex: 1 }}>
        <span style={{
          display: 'block',
          color: neutral.foreground1,
          fontSize: participant.label.length > 22 ? 13 : 15,
          lineHeight: 1.18,
          fontWeight: 650,
          overflowWrap: participant.label.includes(' ') ? 'normal' : 'anywhere',
        }}>
          {participant.label}
        </span>
        {participant.subLabel && (
          <span style={{ display: 'block', color: neutral.foreground3, fontSize: 11, marginTop: 2 }}>
            {participant.subLabel}
          </span>
        )}
        <span style={{
          display: 'inline-block',
          marginTop: 6,
          padding: '2px 7px',
          borderRadius: radius.badge,
          color: tone.fg,
          background: tone.bg,
          fontSize: 10,
          fontWeight: 700,
        }}>{participant.badge.text}</span>
      </span>
    </div>
  );
}

function Label({ x, y, width, children, align = 'center' }: { x: number; y: number; width: number; children: ReactNode; align?: 'center' | 'left' }) {
  return (
    <foreignObject x={x} y={y} width={width} height={44}>
      <div style={{
        width: '100%',
        boxSizing: 'border-box',
        padding: '2px 6px',
        textAlign: align,
        color: neutral.foreground2,
        fontFamily,
        fontSize: 13,
        fontWeight: 600,
        lineHeight: 1.2,
      }}>
        <span style={{
          display: 'inline',
          padding: '2px 5px',
          background: neutral.background3,
          boxDecorationBreak: 'clone',
          WebkitBoxDecorationBreak: 'clone',
        }}>{children}</span>
      </div>
    </foreignObject>
  );
}

function collectMessageNumbers(steps: SequenceStep[], numbers: Map<SequenceMessage, number>, next = { value: 1 }) {
  for (const step of steps) {
    if (step.type === 'message') numbers.set(step, next.value++);
    else if (step.type === 'fragment') for (const section of step.sections) collectMessageNumbers(section.steps, numbers, next);
  }
}

function SequenceSvg({
  spec,
  positioned,
  xById,
  width,
  height,
  activations,
}: {
  spec: SequenceSpec;
  positioned: PositionedStep[];
  xById: Map<string, number>;
  width: number;
  height: number;
  activations: ActivationSpan[];
}) {
  const indexById = new Map(spec.participants.map((p, i) => [p.id, i]));
  const numbers = new Map<SequenceMessage, number>();
  if (spec.autonumber) collectMessageNumbers(spec.steps, numbers);
  const lineColor = neutral.foreground4;
  const arrowId = 'sequence-arrow';
  const openArrowId = 'sequence-open-arrow';

  const renderSteps = (items: PositionedStep[]): ReactNode[] => items.flatMap((item, itemIndex) => {
    const key = `${item.y}-${itemIndex}`;
    const step = item.step;
    if (step.type === 'activation') return [];
    if (step.type === 'note') {
      const xs = step.over.map((id) => xById.get(id) ?? MARGIN_X);
      const left = Math.min(...xs) - 82;
      const right = Math.max(...xs) + 82;
      return [<g key={key}>
        <rect x={left} y={item.y + 8} width={right - left} height={42} rx={8} fill={badgeTones.marigold.bg} stroke={badgeTones.marigold.fg} strokeOpacity={0.42} />
        <foreignObject x={left + 9} y={item.y + 14} width={right - left - 18} height={30}>
          <div style={{ fontFamily, fontSize: 12, lineHeight: 1.25, color: neutral.foreground1, textAlign: 'center' }}>{plainText(step.label)}</div>
        </foreignObject>
      </g>];
    }
    if (step.type === 'fragment') {
      const [first, last] = participantRange(step.sections.flatMap((s) => s.steps), indexById);
      const left = (xById.get(spec.participants[first]?.id) ?? MARGIN_X) - CARD_W / 2 - 12;
      const right = (xById.get(spec.participants[last]?.id) ?? width - MARGIN_X) + CARD_W / 2 + 12;
      const children = item.children ?? [];
      const fragmentElements: ReactNode[] = [
        <rect key={`${key}-box`} x={left} y={item.y} width={right - left} height={item.height} rx={10} fill={neutral.background2} fillOpacity={0.76} stroke={neutral.foreground4} strokeWidth={1.2} />,
        <path key={`${key}-tab`} d={`M ${left} ${item.y + 28} L ${left + 72} ${item.y + 28} L ${left + 84} ${item.y + 16} L ${left + 84} ${item.y} `} fill="none" stroke={neutral.foreground4} />,
        <text key={`${key}-op`} x={left + 11} y={item.y + 19} fill={neutral.foreground1} fontFamily={fontFamily} fontSize={12} fontWeight={700}>{step.operator}</text>,
      ];
      if (step.label) fragmentElements.push(
        <text key={`${key}-label`} x={left + 96} y={item.y + 19} fill={neutral.foreground2} fontFamily={fontFamily} fontSize={12} fontWeight={650}>[{step.label}]</text>,
      );
      children.forEach((section, sectionIndex) => {
        if (sectionIndex > 0) fragmentElements.push(
          <line key={`${key}-divider-${sectionIndex}`} x1={left} y1={section.y} x2={right} y2={section.y} stroke={neutral.foreground4} strokeDasharray="5 4" />,
        );
        if (section.label) fragmentElements.push(
          <text key={`${key}-section-${sectionIndex}`} x={left + 14} y={section.y + 16} fill={neutral.foreground2} fontFamily={fontFamily} fontSize={12} fontWeight={650}>[{section.label}]</text>,
        );
        fragmentElements.push(...renderSteps(section.steps));
      });
      return [<g key={key}>{fragmentElements}</g>];
    }

    const x1 = xById.get(step.from) ?? 0;
    const x2 = xById.get(step.to) ?? 0;
    const y = item.y + 34;
    const dashed = step.line === 'dashed';
    const marker = step.arrow === 'open' ? `url(#${openArrowId})` : step.arrow === 'cross' ? undefined : `url(#${arrowId})`;
    const number = numbers.get(step);
    const text = `${number ? `${number}. ` : ''}${plainText(step.label)}`;
    if (x1 === x2) {
      const loopW = 62;
      return [<g key={key}>
        <path d={`M ${x1} ${y} H ${x1 + loopW} V ${y + 25} H ${x1 + 6}`} fill="none" stroke={lineColor} strokeWidth={1.5} strokeDasharray={dashed ? '6 4' : undefined} markerEnd={marker} />
        {step.arrow === 'cross' && <text x={x1 + 3} y={y + 31} fill={lineColor} fontFamily={fontFamily} fontWeight={700}>×</text>}
        <Label x={x1 + 12} y={item.y + 2} width={Math.min(230, width - x1 - 20)} align="left">{text}</Label>
      </g>];
    }
    const direction = Math.sign(x2 - x1);
    const start = x1 + direction * 7;
    const end = x2 - direction * 9;
    const labelLeft = Math.min(x1, x2) + 12;
    const labelWidth = Math.max(110, Math.abs(x2 - x1) - 24);
    return [<g key={key}>
      <line x1={start} y1={y} x2={end} y2={y} stroke={lineColor} strokeWidth={1.5} strokeDasharray={dashed ? '6 4' : undefined} markerEnd={marker} />
      {step.arrow === 'cross' && <text x={x2 - direction * 7} y={y + 5} fill={lineColor} fontFamily={fontFamily} fontWeight={700} textAnchor="middle">×</text>}
      <Label x={labelLeft} y={item.y + 3} width={labelWidth}>{text}</Label>
    </g>];
  });

  return (
    <svg width={width} height={height} aria-label={spec.alt} role="img">
      <defs>
        <marker id={arrowId} viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
          <path d="M 0 0 L 10 5 L 0 10 z" fill={lineColor} />
        </marker>
        <marker id={openArrowId} viewBox="0 0 10 10" refX="9" refY="5" markerWidth="8" markerHeight="8" orient="auto-start-reverse">
          <path d="M 1 1 L 9 5 L 1 9" fill="none" stroke={lineColor} strokeWidth="1.5" />
        </marker>
        <pattern id="sequence-grid" width="28" height="28" patternUnits="userSpaceOnUse">
          <circle cx="1" cy="1" r="1" fill={neutral.stroke2} />
        </pattern>
      </defs>
      <rect width={width} height={height} fill={neutral.background3} />
      <rect width={width} height={height} fill="url(#sequence-grid)" />
      {spec.participants.map((p) => {
        const x = xById.get(p.id)!;
        return <line key={p.id} x1={x} y1={LIFELINE_TOP - 1} x2={x} y2={height - CARD_H - BOTTOM_CARD_GAP} stroke={neutral.foreground4} strokeWidth={1.2} strokeDasharray="5 5" opacity={0.76} />;
      })}
      {activations.map((a, i) => {
        const x = (xById.get(a.participant) ?? 0) + a.depth * 5;
        return <rect key={`${a.participant}-${i}`} x={x - 5} y={a.y1} width={10} height={Math.max(12, a.y2 - a.y1)} rx={3} fill={neutral.background1} stroke={badgeTones.teal.fg} strokeWidth={1.2} />;
      })}
      {renderSteps(positioned)}
    </svg>
  );
}

export function SequenceCanvas({ spec, onReady }: { spec: SequenceSpec; onReady?: () => void }) {
  const layout = useMemo(() => {
    const xById = new Map<string, number>();
    spec.participants.forEach((p, i) => xById.set(p.id, MARGIN_X + CARD_W / 2 + i * (CARD_W + COL_GAP)));
    const width = MARGIN_X * 2 + spec.participants.length * CARD_W + Math.max(0, spec.participants.length - 1) * COL_GAP;
    const activations: ActivationSpan[] = [];
    const active = new Map<string, { y: number; depth: number }[]>();
    const laidOut = layoutSteps(spec.steps, LIFELINE_TOP + 34, xById, activations, active);
    const sequenceEnd = laidOut.endY + 12;
    for (const [participant, stack] of active) {
      for (const opened of stack) activations.push({ participant, y1: opened.y, y2: sequenceEnd, depth: opened.depth });
    }
    const height = sequenceEnd + BOTTOM_CARD_GAP + CARD_H + TOP;
    return { ...laidOut, xById, width, height, activations };
  }, [spec]);
  const [ready, setReady] = useState(false);

  useEffect(() => {
    const id = requestAnimationFrame(() => requestAnimationFrame(() => {
      setReady(true);
      onReady?.();
    }));
    return () => cancelAnimationFrame(id);
  }, [layout, onReady]);

  return (
    <div id="diagram-root" data-diagram-ready={ready ? 'true' : 'false'} style={{
      position: 'relative',
      width: layout.width,
      height: layout.height,
      overflow: 'hidden',
      background: neutral.background3,
      border: `1px solid ${neutral.stroke2}`,
      borderRadius: radius.card,
    }}>
      <SequenceSvg spec={spec} positioned={layout.positioned} xById={layout.xById} width={layout.width} height={layout.height} activations={layout.activations} />
      {spec.participants.map((p) => <ParticipantCard key={`top-${p.id}`} participant={p} x={layout.xById.get(p.id)!} y={TOP} />)}
      {spec.participants.map((p) => <ParticipantCard key={`bottom-${p.id}`} participant={p} x={layout.xById.get(p.id)!} y={layout.height - CARD_H - TOP} />)}
    </div>
  );
}
