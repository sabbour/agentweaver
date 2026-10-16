/** Extracts a project identifier from a project-scoped route pathname. */
export function projectIdFromPath(pathname: string): string | undefined {
  const match = /^\/projects\/([^/]+)/.exec(pathname);
  return match?.[1];
}
