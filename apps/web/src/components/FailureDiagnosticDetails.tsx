import { Text } from '@fluentui/react-components';
import type { RunTerminalDiagnostic } from '../api/types';

export function FailureDiagnosticDetails({ diagnostic }: { diagnostic: RunTerminalDiagnostic }) {
  return (
    <>
      <Text>
        Evidence: {diagnostic.completeness ?? 'partial'}
        {diagnostic.attempt != null ? ` · attempt ${diagnostic.attempt}` : ''}.
      </Text>
      {(diagnostic.observed_facts ?? []).map(item => (
        <Text key={`fact-${item.code}`}>Observed: {item.summary}</Text>
      ))}
      {(diagnostic.supported_interpretations ?? []).map(item => (
        <Text key={`interpretation-${item.code}`}>Supported interpretation: {item.summary}</Text>
      ))}
      {(diagnostic.unknowns ?? []).map(item => (
        <Text key={`unknown-${item.code}`}>Unknown: {item.summary}</Text>
      ))}
      {diagnostic.denial_gate && (
        <Text>
          Denial gate: {diagnostic.denial_gate.gate}
          {diagnostic.denial_gate.capability ? ` · capability ${diagnostic.denial_gate.capability}` : ''}.
        </Text>
      )}
      {(diagnostic.next_actions ?? []).map(action => (
        <Text key={`action-${action.kind}`}>Next action: {action.label} Expected effect: {action.expected_effect}</Text>
      ))}
    </>
  );
}
