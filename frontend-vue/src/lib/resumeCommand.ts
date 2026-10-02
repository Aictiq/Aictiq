import type { Run } from '@/api/runs'

/**
 * The terminal command that reopens a finished run's harness session by hand, in the checkout
 * its runner kept. Kept out of the component because the quoting has to hold for any path a
 * runner reports and is worth asserting directly.
 */

/** How long a runner keeps a finished run's checkout (`KeptWorkspaceMaxAgeMs` in the CLI). */
export const KeptWorkspaceDays = 5

const harnessCommands: Record<string, string> = {
  claude: 'claude --resume',
  codex: 'codex resume',
  opencode: 'opencode --session',
}

/**
 * Single-quoted for POSIX shells and fish alike. Fish reads `\\` and `\'` inside single quotes,
 * so neither a quote nor a backslash is ever left inside them: both are closed out and escaped.
 */
export function quoteForShell(value: string): string {
  return `'${value.replace(/['\\]/g, (char) => `'\\${char}'`)}'`
}

/** PowerShell single quotes: only a doubled quote is special. */
function quoteForPowerShell(value: string): string {
  return `'${value.replaceAll("'", "''")}'`
}

/**
 * The command for a run, or null when it has nothing to resume: still live, no session, no
 * stored checkout (runs from before the runner reported one), an unknown harness, or finished
 * longer ago than the runner keeps checkouts.
 */
export function resumeCommand(run: Run, now: number = Date.now()): string | null {
  const { sessionId, workspacePath, finishedAt } = run
  const harness = harnessCommands[run.harness]
  if (!sessionId || !workspacePath || !finishedAt || !harness) return null
  // Negated so an unparsable time hides the command too.
  if (!(now - Date.parse(finishedAt) < KeptWorkspaceDays * 24 * 60 * 60 * 1000)) return null
  // A Windows runner's checkout (`C:\...`) is opened from PowerShell, which also runs `&&`.
  const quote = /^[A-Za-z]:[\\/]/.test(workspacePath) ? quoteForPowerShell : quoteForShell
  return `cd ${quote(workspacePath)} && ${harness} ${quote(sessionId)}`
}
