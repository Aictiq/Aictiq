import type { HarnessOutcome } from '../types.js'

const limit = (value: string, length: number) =>
  value.length > length ? `${value.slice(0, Math.max(0, length - 1))}…` : value

const RateLimited =
  /rate[ _-]?limit|\b429\b|too many requests|usage limit|\d+-hour limit|quota exceeded/i
const Transient =
  /overloaded|\b529\b|\b50[234]\b|service unavailable|bad gateway|econnreset|econnrefused|etimedout|enotfound|eai_again|socket hang up|fetch failed|network error|connection (?:reset|refused|closed|error)|stream (?:disconnected|error)/i
const SessionMissing =
  /no conversation found|session(?: id)? (?:not found|does not exist)|no (?:such )?session|thread .*not found|rollout .*not found/i

/**
 * Why a harness failed, read from its exit and its last output lines. The reason decides what
 * the instance may do next: `harness-rate-limited`, `harness-transient` and `harness-crashed`
 * are worth continuing automatically, `session-unavailable` (only on a resume) offers a fresh
 * retry, and `harness-exit-N` or the adapter's own reason stay failed until someone looks.
 *
 * A null exit code is a signal the runner did not send (its own stops are reported before an
 * adapter is asked), so the harness crashed.
 */
export function failureReason(
  exitCode: number | null,
  lastLines: string[],
  resuming: boolean,
  fallback?: string,
): string {
  const text = lastLines.join('\n')
  if (resuming && SessionMissing.test(text)) return 'session-unavailable'
  if (RateLimited.test(text)) return 'harness-rate-limited'
  if (Transient.test(text)) return 'harness-transient'
  if (exitCode === null) return 'harness-crashed'
  return fallback ?? `harness-exit-${exitCode}`
}

export function failedOutcome(
  exitCode: number | null,
  lastLines: string[],
  resuming = false,
  fallback?: string,
): HarnessOutcome {
  return {
    outcome: 'failed',
    failureReason: failureReason(exitCode, lastLines, resuming, fallback),
    summary: limit(lastLines.slice(-20).join('\n'), 4_000) || null,
  }
}

/** The message a resumed harness is given instead of the original prompt. */
export function continuePrompt(reason: string | null): string {
  return [
    'Continue where you stopped.',
    `The previous attempt failed${reason ? ` with ${reason}` : ''} before the work was finished.`,
    'Your earlier changes are still in this checkout. Check what is already done, then finish the remaining steps of the original instructions.',
  ].join(' ')
}
