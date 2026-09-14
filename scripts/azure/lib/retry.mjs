// retry.mjs -- Bounded retry for *idempotent* Azure CLI operations.
//
// WHY THIS IS NOT IN exec.mjs (read before moving it there)
// --------------------------------------------------------
// `exec.run()`/`exec.capture()` deliberately never retry: killing a local CLI
// process cannot establish whether the remote Azure operation completed, so a
// blind retry at that layer could duplicate a mutating operation. That
// invariant is intentional and must stay.
//
// Retry is therefore a *caller* decision, made only where the caller knows the
// operation is idempotent:
//
//   - `az acr import --force`  -- re-importing the same source to the same tag
//                                 converges on the same digest.
//   - read-only queries (`show`, `show-manifests`) -- no remote mutation at all.
//
// Each call site documents its own idempotency justification. Do not wrap a
// non-idempotent operation with this helper.

/**
 * Transient transport/service failures worth retrying.
 *
 * These are regexes, not substrings, and that matters: a naive
 * `includes("enotfound")` also matches "Resou[rceNotFound]" -- turning a
 * deterministic "this image does not exist" into three retries and a much
 * slower, more confusing failure. Numeric status codes are likewise only
 * recognised in status-like context, because a bare "503" appears readily
 * inside a sha256 digest.
 */
const TRANSIENT_PATTERNS = Object.freeze([
  /\bconnection (?:aborted|reset)\b/,
  /\bconnectionreseterror\b/,
  /\b(?:econnreset|econnrefused|etimedout|esockettimedout|enotfound|eai_again|epipe)\b/,
  /\b10054\b/,
  /\btemporary failure in name resolution\b/,
  /\btimed out\b/,
  /\btimeout\b/,
  /\btoo many requests\b/,
  /\bthrottl/,
  /\bservice unavailable\b/,
  /\bbad gateway\b/,
  /\bgateway timeout\b/,
  /\binternal server error\b/,
  /\bserver failed to authenticate\b/,
  /\b(?:http|status(?:\s+code)?|code|error)\D{0,3}\b(?:429|500|502|503|504)\b/,
  /\(\s*(?:429|500|502|503|504)\s*\)/,
  /\boperation returned an invalid status\b/,
]);

/**
 * True when `error` looks like a transient transport/service failure rather
 * than a deterministic rejection (bad name, missing repo, auth denied).
 *
 * A local timeout counts as transient *for idempotent operations only*: the
 * remote state is unknown, but re-running an idempotent operation converges
 * regardless of whether the first attempt landed.
 */
export function isTransientExecError(error) {
  if (!error) return false;
  if (error.name === "ExecTimeoutError") return true;
  const haystack = [error.message, error.stderr, error.name]
    .filter(Boolean)
    .join(" ")
    .toLowerCase();
  if (!haystack) return false;
  return TRANSIENT_PATTERNS.some((pattern) => pattern.test(haystack));
}

function defaultSleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/**
 * Runs `operation(attempt)` up to `attempts` times, retrying only when
 * `isTransient` says the failure is worth another try.
 *
 * `operation` receives the 1-based attempt number so an idempotent-but-
 * conflicting operation can add `--force` on a retry.
 *
 * Backoff is exponential with full jitter, capped at `maxDelayMs`, so a
 * four-way concurrent preflight does not resynchronize onto the same retry
 * instant after a shared service blip.
 */
export async function withRetry(
  operation,
  {
    attempts = 3,
    baseDelayMs = 2_000,
    maxDelayMs = 30_000,
    label = "operation",
    isTransient = isTransientExecError,
    sleep = defaultSleep,
    onRetry,
    random = Math.random,
  } = {},
) {
  if (!Number.isInteger(attempts) || attempts < 1) {
    throw new TypeError(`attempts must be a positive integer; received '${attempts}'.`);
  }

  let lastError;
  for (let attempt = 1; attempt <= attempts; attempt++) {
    try {
      return await operation(attempt);
    } catch (error) {
      lastError = error;
      const canRetry = attempt < attempts && isTransient(error);
      if (!canRetry) throw error;

      const ceiling = Math.min(baseDelayMs * 2 ** (attempt - 1), maxDelayMs);
      const delay = Math.round(ceiling * (0.5 + random() * 0.5));
      onRetry?.({ attempt, attempts, delay, error, label });
      await sleep(delay);
    }
  }

  throw lastError;
}
