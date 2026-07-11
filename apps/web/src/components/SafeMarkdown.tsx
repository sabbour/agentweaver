import ReactMarkdown from 'react-markdown';
import rehypeSanitize, { defaultSchema } from 'rehype-sanitize';
import remarkGfm from 'remark-gfm';
import { memo } from 'react';
import type { ComponentProps } from 'react';

// SECURITY: sanitize with the default schema (no raw HTML passthrough).
// rehype-raw is intentionally NOT included — raw HTML in agent text is neutralised.
// This schema strips <script>, event-handler attributes, and any tag not on the allowlist.
const SANITIZE_SCHEMA = defaultSchema;

// SECURITY: custom link renderer forces safe external-link attributes.
// This prevents target="_blank" without rel="noopener noreferrer" (reverse tabnabbing).
function SafeLink({ href, children }: ComponentProps<'a'>) {
  return (
    <a href={href} target="_blank" rel="noopener noreferrer">
      {children}
    </a>
  );
}

export interface SafeMarkdownProps {
  children: string;
}

/**
 * Shared GFM + sanitized markdown renderer. Single source of truth for how agent /
 * timeline text is rendered so tables, task-lists and links stay consistent and safe
 * across surfaces (AgentMessageBubble, the run Timeline messages, …).
 */
export const SafeMarkdown = memo(function SafeMarkdown({ children }: SafeMarkdownProps) {
  return (
    <ReactMarkdown
      remarkPlugins={[remarkGfm]}
      rehypePlugins={[[rehypeSanitize, SANITIZE_SCHEMA]]}
      components={{ a: SafeLink }}
    >
      {children}
    </ReactMarkdown>
  );
});
