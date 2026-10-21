import type { WorkspaceFileContent } from '../api/types';

/**
 * Decodes captured bytes for the 0.x file viewer. Content that is not valid UTF-8 or that
 * contains a NUL byte is shown as binary instead of as text.
 */
export function decodeCapturedFile(path: string, bytes: Uint8Array): WorkspaceFileContent {
  // Text files almost never contain a NUL byte, so treat any NUL as a sign of binary content.
  if (bytes.includes(0)) return { path, content: null, is_binary: true, language: null };
  try {
    // fatal: true makes the decoder throw on invalid UTF-8 instead of inserting replacement
    // characters, so the viewer never shows text that differs from the captured bytes.
    const content = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
    return { path, content, is_binary: false, language: null };
  } catch {
    // Not valid UTF-8, so show it as binary.
    return { path, content: null, is_binary: true, language: null };
  }
}
