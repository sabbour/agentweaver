/**
 * components/ui/copilot — barrel exports
 *
 * Native Copilot-styled chat surface. No @1js imports.
 * Mirrored @1js component anatomy (as design reference only):
 *   Composer     ← @1js/fai-react-chat-input ChatInput + SendButton
 *   CopilotMessage ← @1js/fai-react-copilot-chat CopilotMessage
 *   UserMessage  ← @1js/fai-react-copilot-chat UserMessage
 *   CopilotChat  ← @1js/fai-react-copilot-chat CopilotChat
 *   OutputCard   ← @1js/fai-react-output-card OutputCard
 *   FeedbackButtons ← @1js/fai-react-feedback-buttons FeedbackButtons
 *   Attachment   ← @1js/fai-react-attachments Attachment
 */

// Composer + attachments
export { Composer } from "./Composer";
export type { ComposerProps, ComposerSubmitData, ComposerAppearance } from "./Composer";

export { Attachment, AttachmentList } from "./Attachment";
export type { AttachmentProps, AttachmentListProps } from "./Attachment";

// Messages + chat feed
export { CopilotChat, CopilotMessage, UserMessage } from "./Message";
export type {
  CopilotChatProps,
  CopilotMessageProps,
  CopilotLoadingState,
  UserMessageProps,
} from "./Message";

// Output card
export { OutputCard } from "./OutputCard";
export type { OutputCardProps, OutputCardMode } from "./OutputCard";

// Feedback
export { FeedbackButtons } from "./FeedbackButtons";
export type { FeedbackButtonsProps, FeedbackValue } from "./FeedbackButtons";

// Dev demo
export { CopilotSurface } from "./CopilotSurface";
