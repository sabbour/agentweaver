import {
  Button,
  Portal,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import {
  ChevronLeftRegular,
  ChevronRightRegular,
  DismissRegular,
} from '@fluentui/react-icons';
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { CSSProperties, RefObject } from 'react';

const VIEWPORT_MARGIN = 16;
const TARGET_GAP = 12;

export interface FirstRunTourTargets {
  projects: RefObject<HTMLElement | null>;
  sessions: RefObject<HTMLElement | null>;
  startTask: RefObject<HTMLElement | null>;
}

export interface FirstRunTourProps {
  open: boolean;
  targets: FirstRunTourTargets;
  returnFocusTarget?: RefObject<HTMLElement | null>;
  onDismiss: () => void;
}

interface TourStep {
  id: 'projects' | 'sessions' | 'startTask';
  title: string;
  body: string[];
}

interface TargetBox {
  top: number;
  left: number;
  width: number;
  height: number;
  right: number;
  bottom: number;
}

const STEPS: TourStep[] = [
  {
    id: 'projects',
    title: 'Create a project',
    body: [
      'Create a local project or connect a GitHub repository.',
      'Each project contains its team, work, and review history.',
    ],
  },
  {
    id: 'sessions',
    title: 'Use Sessions',
    body: [
      'Sessions keep your conversations and agent work in one place.',
      'Open Sessions to continue a conversation or review recent agent work.',
    ],
  },
  {
    id: 'startTask',
    title: 'Start a task',
    body: [
      'Select a project first.',
      'Then describe the result that you want Agentweaver to produce.',
    ],
  },
];

const useStyles = makeStyles({
  layer: {
    position: 'fixed',
    inset: 0,
    zIndex: 100000,
    pointerEvents: 'none',
  },
  targetRing: {
    position: 'fixed',
    borderRadius: tokens.borderRadiusLarge,
    outline: `2px solid ${tokens.colorStrokeFocus2}`,
    outlineOffset: '4px',
    pointerEvents: 'none',
    transitionProperty: 'top, left, width, height',
    transitionDuration: '150ms',
    transitionTimingFunction: tokens.curveDecelerateMid,
    '@media (prefers-reduced-motion: reduce)': {
      transitionDuration: '0.01ms',
    },
  },
  panel: {
    position: 'fixed',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    width: 'min(336px, calc(100vw - 32px))',
    maxWidth: 'calc(100vw - 32px)',
    maxHeight: 'calc(100vh - 32px)',
    overflowY: 'auto',
    padding: tokens.spacingVerticalL,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow16,
    color: tokens.colorNeutralForeground1,
    pointerEvents: 'auto',
  },
  header: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
  },
  heading: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  stepLabel: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  title: {
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
    fontWeight: tokens.fontWeightSemibold,
  },
  body: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    color: tokens.colorNeutralForeground2,
  },
  actions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  skip: {
    marginRight: 'auto',
  },
});

function targetBox(element: HTMLElement | null): TargetBox | null {
  if (!element) return null;
  const rect = element.getBoundingClientRect();
  return {
    top: rect.top,
    left: rect.left,
    width: rect.width,
    height: rect.height,
    right: rect.right,
    bottom: rect.bottom,
  };
}

function clamp(value: number, minimum: number, maximum: number): number {
  return Math.min(Math.max(value, minimum), Math.max(minimum, maximum));
}

function boxesMatch(left: TargetBox | null, right: TargetBox | null): boolean {
  if (left === right) return true;
  if (!left || !right) return false;
  return left.top === right.top
    && left.left === right.left
    && left.width === right.width
    && left.height === right.height;
}

function positionsMatch(left: CSSProperties, right: CSSProperties): boolean {
  return left.top === right.top
    && left.left === right.left
    && left.right === right.right
    && left.bottom === right.bottom;
}

export function FirstRunTour({
  open,
  targets,
  returnFocusTarget,
  onDismiss,
}: FirstRunTourProps) {
  const styles = useStyles();
  const [stepIndex, setStepIndex] = useState(0);
  const [box, setBox] = useState<TargetBox | null>(null);
  const [panelStyle, setPanelStyle] = useState<CSSProperties>({});
  const [layoutReady, setLayoutReady] = useState(false);
  const panelRef = useRef<HTMLDivElement>(null);
  const headingRef = useRef<HTMLDivElement>(null);
  const previousFocusRef = useRef<HTMLElement | null>(null);
  const onDismissRef = useRef(onDismiss);
  const returnFocusTargetRef = useRef(returnFocusTarget);
  const step = STEPS[stepIndex];
  const target = targets[step.id];
  const dismiss = useCallback(() => {
    setStepIndex(0);
    setLayoutReady(false);
    const previousFocus = previousFocusRef.current;
    const focusTarget = previousFocus
      && previousFocus !== document.body
      && previousFocus.isConnected
      ? previousFocus
      : returnFocusTargetRef.current?.current;
    onDismissRef.current();
    queueMicrotask(() => focusTarget?.focus());
  }, []);

  useEffect(() => {
    onDismissRef.current = onDismiss;
    returnFocusTargetRef.current = returnFocusTarget;
  }, [onDismiss, returnFocusTarget]);

  const updateLayout = useCallback(() => {
    if (!open) return;
    const nextBox = targetBox(target.current);
    setBox((current) => boxesMatch(current, nextBox) ? current : nextBox);

    const panel = panelRef.current?.getBoundingClientRect();
    const panelWidth = panel?.width ?? 336;
    const panelHeight = panel?.height ?? 240;
    const viewportWidth = window.innerWidth;
    const viewportHeight = window.innerHeight;

    const setPosition = (nextPosition: CSSProperties) => {
      setPanelStyle((current) => positionsMatch(current, nextPosition) ? current : nextPosition);
      setLayoutReady(true);
    };

    if (viewportWidth <= 720 || panelHeight > viewportHeight - (VIEWPORT_MARGIN * 2) || !nextBox) {
      setPosition({
        left: VIEWPORT_MARGIN,
        right: VIEWPORT_MARGIN,
        bottom: VIEWPORT_MARGIN,
      });
      return;
    }

    const maxLeft = viewportWidth - panelWidth - VIEWPORT_MARGIN;
    const maxTop = viewportHeight - panelHeight - VIEWPORT_MARGIN;

    if (step.id === 'startTask') {
      const below = nextBox.bottom + TARGET_GAP;
      const top = below + panelHeight <= viewportHeight - VIEWPORT_MARGIN
        ? below
        : nextBox.top - panelHeight - TARGET_GAP;
      setPosition({
        left: clamp(nextBox.right - panelWidth, VIEWPORT_MARGIN, maxLeft),
        top: clamp(top, VIEWPORT_MARGIN, maxTop),
      });
      return;
    }

    const preferredLeft = nextBox.right + TARGET_GAP;
    const left = preferredLeft + panelWidth <= viewportWidth - VIEWPORT_MARGIN
      ? preferredLeft
      : nextBox.left - panelWidth - TARGET_GAP;
    setPosition({
      left: clamp(left, VIEWPORT_MARGIN, maxLeft),
      top: clamp(nextBox.top - 8, VIEWPORT_MARGIN, maxTop),
    });
  }, [open, step.id, target]);

  useLayoutEffect(() => {
    if (!open) return undefined;
    const frame = window.requestAnimationFrame(updateLayout);
    window.addEventListener('resize', updateLayout);
    window.addEventListener('scroll', updateLayout, true);
    const observer = typeof ResizeObserver === 'undefined'
      ? null
      : new ResizeObserver(updateLayout);
    if (target.current) observer?.observe(target.current);
    if (panelRef.current) observer?.observe(panelRef.current);
    return () => {
      window.cancelAnimationFrame(frame);
      window.removeEventListener('resize', updateLayout);
      window.removeEventListener('scroll', updateLayout, true);
      observer?.disconnect();
    };
  }, [open, target, updateLayout]);

  useEffect(() => {
    if (!open) return undefined;
    previousFocusRef.current = document.activeElement instanceof HTMLElement
      ? document.activeElement
      : null;
    headingRef.current?.focus();
    return undefined;
  }, [open]);

  useEffect(() => {
    if (!open) return;
    headingRef.current?.focus();
  }, [open, stepIndex]);

  useEffect(() => {
    if (!open) return undefined;
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') dismiss();
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [dismiss, open]);

  if (!open) return null;

  const isFirst = stepIndex === 0;
  const isLast = stepIndex === STEPS.length - 1;

  return (
    <Portal>
      <div className={styles.layer}>
        {box && (
          <div
            className={styles.targetRing}
            aria-hidden="true"
            style={{
              top: box.top,
              left: box.left,
              width: box.width,
              height: box.height,
            }}
          />
        )}
        <div
          ref={panelRef}
          className={styles.panel}
          style={{ ...panelStyle, visibility: layoutReady ? 'visible' : 'hidden' }}
          role="dialog"
          aria-modal="false"
          aria-labelledby={`first-run-tour-title-${step.id}`}
          aria-describedby={`first-run-tour-body-${step.id}`}
        >
          <div className={styles.header}>
            <div ref={headingRef} className={styles.heading} tabIndex={-1}>
              <Text className={styles.stepLabel}>Step {stepIndex + 1} of {STEPS.length}</Text>
              <Text
                as="h2"
                id={`first-run-tour-title-${step.id}`}
                className={styles.title}
              >
                {step.title}
              </Text>
            </div>
            <Button
              appearance="subtle"
              size="small"
              icon={<DismissRegular />}
              aria-label="Skip product tour"
              onClick={dismiss}
            />
          </div>
          <div id={`first-run-tour-body-${step.id}`} className={styles.body}>
            {step.body.map((paragraph) => <Text key={paragraph}>{paragraph}</Text>)}
          </div>
          <div className={styles.actions}>
            <Button appearance="subtle" className={styles.skip} onClick={dismiss}>
              Skip tour
            </Button>
            {!isFirst && (
              <Button
                appearance="secondary"
                icon={<ChevronLeftRegular />}
                onClick={() => {
                  setLayoutReady(false);
                  setStepIndex((current) => current - 1);
                }}
              >
                Back
              </Button>
            )}
            <Button
              appearance="primary"
              icon={isLast ? undefined : <ChevronRightRegular />}
              iconPosition="after"
              onClick={() => {
                if (isLast) {
                  dismiss();
                  return;
                }
                setLayoutReady(false);
                setStepIndex((current) => current + 1);
              }}
            >
              {isLast ? 'Finish' : 'Next'}
            </Button>
          </div>
        </div>
      </div>
    </Portal>
  );
}
