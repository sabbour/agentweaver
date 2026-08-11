# Validation workflow

Agentweaver keeps every package root's `node_modules` physical and private to its
worktree. The dependency helper accelerates a new worktree by sharing only npm's
content-addressed download cache:

```bash
npm run deps:ensure
```

The cache lives under Git's common directory. Each namespace is keyed by the package
root, `package.json`, `package-lock.json`, exact Node/npm versions, OS, architecture,
libc, sanitized install-shaping npm configuration, install arguments, and lifecycle
environment. Branch names are never part of the key.

Every new or changed worktree still runs `npm ci`, so npm deletes and recreates only
that worktree's dependency tree. A worktree-local marker lets an unchanged subsequent
validation skip reinstalling after checking the hidden lockfile, physical package
paths, and representative `require.resolve` results. Web dependencies must resolve
under that worktree's `apps/web/node_modules`; docs dependencies must resolve under
its `docs/node_modules`. Vite/Vitest/VitePress caches, TypeScript build information,
build output, and test scratch remain worktree-local.

npm owns normal concurrent reads and writes to its download cache. Agentweaver adds one
validation mutex per canonical worktree so two commands cannot replace the same local
`node_modules` concurrently. Lock owners record PID and process start time; stale local
locks are recovered only after that identity proves the original process is gone.
Maintenance operations use a separate global lock.

If a shared namespace is malformed or `npm ci` reports corruption, the helper
quarantines that namespace atomically under the maintenance lock and retries once with
a clean cache. A second failure stops validation. It never regenerates a lockfile.
Workspace roots, local lockfile links, authenticated npm configuration, CI, and
`AGENTWEAVER_DISABLE_SHARED_DEPS=1` use isolated `npm ci` instead. Set
`AGENTWEAVER_DEPS_CACHE_DIR` to choose another cache root.

Explicitly quarantine a project's cache and delete only its current worktree tree:

```bash
npm run deps:invalidate -- --project apps/web
```

Verify npm's cache integrity under the maintenance lock:

```bash
npm run deps:verify -- --project apps/web
```

No live shared/junctioned `node_modules`, hardlinks, copy-on-write clones, or
cross-worktree writable trees are used.

## Validation profiles

Use the layer profile as an advisory preflight while developing. It detects affected
areas, prepares each required dependency root once, and prints command timing:

```bash
npm run validate:layer
```

For a .NET layer, provide a component-specific VSTest filter:

```bash
npm run validate:layer -- --area dotnet \
  --dotnet-filter "FullyQualifiedName~Agentweaver.Tests.Coordinator"
```

.NET validation uses the shared user-level NuGet package cache, locked restore, one
worktree-local build, then `dotnet test --no-build --no-restore`. `bin/` and `obj/`
are never shared. If an assets file points at another checkout, only that project's
current-worktree outputs are deleted before restore.

Each stacked PR gets its path-targeted preflight. Run the full profile against the
exact integrated tree at the stack top:

```bash
npm run validate:full
```

PR 1 must also pass the full suite against its current `dev` merge candidate before
merge. A green stack top does not replace that check. Restack and rerun after a lower
layer merges. Validation logs an identity derived from the exact commit/tree, dirty
digest, profile version, environment, and toolchain; test results are never reused
across Git SHAs.
