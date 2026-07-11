export { AgentweaverCopilotProvider } from './AgentweaverCopilotProvider';
export type { AgentweaverCopilotProviderProps } from './AgentweaverCopilotProvider';

export { Composer, OutputBubble } from './Composer';
export type { ComposerProps, OutputBubbleProps } from './Composer';

// CopilotProof is dev-only — not re-exported from the main index so it stays
// out of the production bundle. Import directly:
//   import { CopilotProof } from '../copilot/CopilotProof';
