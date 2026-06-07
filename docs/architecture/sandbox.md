# Sandbox

The agent can read and write files only inside its assigned git worktree. The sandbox is enforced in the backend, not in the model prompt, so the same rules apply no matter which client starts the run.

## Path validation model

`SandboxPathValidator` applies five layers of validation before a read or write succeeds:

1. Reject absolute paths.
2. Reject any `..` path segment before the path is combined with the sandbox root.
3. Normalize the candidate path with `Path.GetFullPath` against the sandbox root.
4. Check that the normalized path still has the sandbox root as its lexical prefix.
5. Walk existing ancestors and reject any reparse point, including symlinks and junctions.

That lexical validation prevents obvious escapes. The file tools then open the file handle and verify the real path after open, which closes the gap for time-of-check and time-of-use races and reparse-point redirection.

## Open then verify

The runtime does not trust a lexical path check on its own. After it opens a file, it resolves the final path from the handle and checks that path against the sandbox root again.

- On Windows, it uses `GetFinalPathNameByHandle`.
- On Linux, it resolves `/proc/self/fd/<fd>`.

A handle that resolves outside the worktree is rejected before any bytes are read or written.

## What the agent can do

Inside the worktree, the agent can:

- Read existing text files
- Create directories as needed for a write
- Create or overwrite text files
- Retry within the sandbox when a path is missing or rejected

## What the agent cannot do

The agent cannot:

- Use absolute paths
- Traverse out of the worktree with `..`
- Follow a symlink or junction outside the worktree
- Read or write files in another repository or elsewhere on disk
- Escape the sandbox by swapping a path target after validation

## Failure model

The sandboxed file tools never throw policy failures into the agent loop. They return structured failures instead, which the runtime converts into `tool.rejected` for policy denials and `tool.error` for ordinary execution failures such as missing files or access errors.
