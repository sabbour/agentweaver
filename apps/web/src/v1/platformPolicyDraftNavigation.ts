export function findPlatformPolicyDiagnosticDraftFile<T extends {
  id: number;
  relativePath: string;
}>(
  relativePath: string | null,
  draftFiles: T[],
): T | null {
  if (relativePath === null) return null;
  const matches = draftFiles.filter((file) => file.relativePath === relativePath);
  return matches.length === 1 ? matches[0] : null;
}
