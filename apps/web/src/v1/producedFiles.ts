import type { WorkspaceFileContent } from '../api/types';

/**
 * Decodes captured bytes for the 0.x file viewer. Content that is not valid UTF-8 or that
 * contains a NUL byte is shown as binary instead of as text.
 */
export function decodeCapturedFile(path: string, bytes: Uint8Array): WorkspaceFileContent {
  if (bytes.includes(0)) return { path, content: null, is_binary: true, language: null };
  try {
    const content = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
    return { path, content, is_binary: false, language: null };
  } catch {
    return { path, content: null, is_binary: true, language: null };
  }
}
