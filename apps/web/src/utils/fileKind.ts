// Shared markdown-file detection so viewers/browsers agree on what counts as markdown.
// Matches ArtifactBrowser's fileIconForName extension set (`.md` / `.markdown`), case-insensitive,
// so a `.markdown` file (or `.MD`) gets the same treatment everywhere (icon, default preview mode).
export function isMarkdownFile(path: string | null | undefined): boolean {
  if (!path) return false;
  const lower = path.toLowerCase();
  return lower.endsWith('.md') || lower.endsWith('.markdown');
}
