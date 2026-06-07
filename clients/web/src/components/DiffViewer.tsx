import { tokens } from '@fluentui/react-components';

interface DiffViewerProps {
  diff: string;
}

/**
 * T066: DiffViewer — renders a unified diff with syntax-highlighted lines.
 * Added lines: distinct green background. Removed lines: distinct red background.
 * No emojis (NFR-002). Accessible color contrast compliant.
 */
export function DiffViewer({ diff }: DiffViewerProps) {
  if (!diff || diff.trim().length === 0) {
    return (
      <div
        style={{
          padding: '12px',
          color: tokens.colorNeutralForeground3,
          fontFamily: 'monospace',
          fontSize: '13px',
        }}
      >
        No changes in this run.
      </div>
    );
  }

  const lines = diff.split('\n');

  return (
    <div
      style={{
        fontFamily: 'monospace',
        fontSize: '13px',
        lineHeight: '1.5',
        overflowX: 'auto',
        border: `1px solid ${tokens.colorNeutralStroke1}`,
        borderRadius: tokens.borderRadiusMedium,
      }}
    >
      {lines.map((line, index) => (
        <div
          key={index}
          style={{
            padding: '1px 8px',
            backgroundColor: getLineBackground(line),
            color: getLineColor(line),
            whiteSpace: 'pre',
          }}
        >
          {line || ' '}
        </div>
      ))}
    </div>
  );
}

function getLineBackground(line: string): string {
  if (line.startsWith('+') && !line.startsWith('+++')) {
    return '#e6ffed'; // accessible green — WCAG AA contrast on dark text
  }
  if (line.startsWith('-') && !line.startsWith('---')) {
    return '#ffeef0'; // accessible red — WCAG AA contrast on dark text
  }
  if (line.startsWith('@@')) {
    return '#f1f8ff'; // accessible blue for hunk headers
  }
  return 'transparent';
}

function getLineColor(line: string): string {
  if (line.startsWith('+') && !line.startsWith('+++')) {
    return '#22863a';
  }
  if (line.startsWith('-') && !line.startsWith('---')) {
    return '#b31d28';
  }
  if (line.startsWith('@@')) {
    return '#1b7cd6';
  }
  return tokens.colorNeutralForeground1;
}
