# Validation workflow

Agentweaver worktrees can reuse immutable, content-addressed dependency trees for the
root tools, web app, and docs site:

```bash
npm run deps:ensure
```

The cache lives under Git's common directory, so sibling worktrees share it without
pointing source or workspace links at another branch. Its key includes `package.json`,
`package-lock.json`, Node and npm versions, operating system, and architecture. The
key also includes npm settings that change installation output. The first caller runs
`npm ci`; later worktrees materialize a private tree from the completed cache, using
copy-on-write filesystem cloning when supported and a normal file copy otherwise.
No elevated symlink privilege or cross-worktree junction is required. Population uses
an exclusive lock and an atomic completion marker. Dead or expired locks are recovered
automatically.

This complements npm's user-level cache: npm already shares downloaded tarballs, but
still extracts and links every package for each isolated `npm ci`. The repository cache
reuses that completed extraction only when the full environment key matches.

Projects containing npm workspaces or local lockfile links always use an isolated
`npm ci`. CI also uses isolated installs. If the shared cache, lock, or link cannot be
used, the command reports the reason and falls back to isolated installation. Set
`AGENTWEAVER_DISABLE_SHARED_DEPS=1` to force that behavior, or
`AGENTWEAVER_DEPS_CACHE_DIR` to choose another cache root.

Invalidate future materializations after diagnosing a damaged dependency tree:

```bash
npm run deps:invalidate -- --project apps/web
```

Invalidation creates a new cache generation; already-running worktrees keep their
private materialization. Vite, Vitest, and VitePress write transform caches inside each
worktree rather than into the shared cache.

## Validation profiles

Use the layer profile while developing. It detects changed areas, installs each required
dependency set once, and prints timing for every command:

```bash
npm run validate:layer
```

For a .NET layer, provide a component-specific VSTest filter:

```bash
npm run validate:layer -- --area dotnet \
  --dotnet-filter "FullyQualifiedName~Agentweaver.Tests.Coordinator"
```

Use `npm run validate:full` only at the top of a stacked change before integration.
CI remains the authority for the full .NET suite. NuGet's user package cache is already
shared safely; `bin/` and `obj/` stay worktree-local because MSBuild outputs are not
reused across branches.
