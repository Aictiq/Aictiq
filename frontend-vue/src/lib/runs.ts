import type { Run, RunLogPage, RunLogLine, RunStatus } from '@/api/runs'

/**
 * Run presentation rules, kept out of the components because they decide what a
 * stakeholder is told and are worth asserting directly — the same reasoning as
 * `lib/claims.ts` and `lib/runners.ts`.
 */

/** A run is live until it reaches an outcome; the item stays claimed for exactly one. */
export function isLiveRun(status: RunStatus | string | undefined): boolean {
  return status === 'queued' || status === 'assigned' || status === 'running'
}

export const runStatuses: RunStatus[] = [
  'queued',
  'assigned',
  'running',
  'succeeded',
  'failed',
  'cancelled',
  'timedOut',
]

export const runStatusLabel: Record<RunStatus, string> = {
  queued: 'Queued',
  assigned: 'Assigned',
  running: 'Running',
  succeeded: 'Succeeded',
  failed: 'Failed',
  cancelled: 'Cancelled',
  timedOut: 'Timed out',
}

/** Design tokens only: a literal colour here would be wrong in one of the two themes. */
export const runStatusTone: Record<RunStatus, string> = {
  queued: 'border-border bg-muted text-muted-foreground',
  assigned: 'border-primary/35 bg-primary/10 text-primary',
  running: 'border-primary/35 bg-primary/10 text-primary',
  succeeded: 'border-success/35 bg-success/10 text-success',
  failed: 'border-destructive/35 bg-destructive/10 text-destructive',
  cancelled: 'border-border bg-muted text-muted-foreground',
  timedOut: 'border-warning/40 bg-warning/10 text-warning',
}

/**
 * Realtime and runner outcomes spell the timeout `timed_out`; the REST DTO spells it
 * `timedOut`. One door for both, so a push never fails a lookup the API would answer.
 */
export function normalizeRunStatus(raw: string | undefined | null): RunStatus | null {
  if (!raw) return null
  const name = raw === 'timed_out' ? 'timedOut' : raw
  return (runStatuses as string[]).includes(name) ? (name as RunStatus) : null
}

/** `PROJ-12` names project `PROJ` — the same rule the API applies to item-key routes. */
export function projectKeyOf(itemKey: string): string {
  const cut = itemKey.lastIndexOf('-')
  return cut > 0 ? itemKey.slice(0, cut) : itemKey
}

/** "42 s", "4 min", "1 h 12 m" — the elapsed time of a run that has started. */
export function runDuration(
  run: { startedAt: string | null; finishedAt?: string | null },
  now: Date = new Date(),
): string | null {
  if (!run.startedAt) return null
  const start = new Date(run.startedAt).getTime()
  if (Number.isNaN(start)) return null
  const end = run.finishedAt ? new Date(run.finishedAt).getTime() : now.getTime()
  const seconds = Math.max(0, Math.round((end - start) / 1000))
  if (seconds < 60) return `${seconds} s`
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes} min`
  return `${Math.floor(minutes / 60)} h ${minutes % 60} m`
}

export function formatCost(costUsd: number | null): string | null {
  return costUsd === null ? null : `$${costUsd.toFixed(2)}`
}

/** "12.3k" — token counts are for a glance, not an invoice. */
export function formatTokens(count: number | null): string | null {
  if (count === null) return null
  if (count < 1000) return String(count)
  if (count < 1_000_000) return `${(count / 1000).toFixed(1)}k`
  return `${(count / 1_000_000).toFixed(1)}m`
}

export interface StartRunContext {
  /** The organization's `canOperateFactory` for *me*. */
  canOperateFactory: boolean
  /** My effective project role, or null when the project itself is invisible. */
  projectRole: string | null
  projectArchived: boolean
  claimedBy: string | null
  claimedByName?: string | null
  hasLiveRun: boolean
}

/**
 * Whether the item page draws the Hand to agent button, and why it is asleep if it is drawn
 * asleep. Hidden from stakeholders and project guests — the API refuses them anyway —
 * and disabled, with the reason named, while a claim or a live run holds the item.
 */
export function startRunButton(context: StartRunContext): {
  visible: boolean
  disabledReason: string | null
} {
  if (!context.canOperateFactory || !context.projectRole || context.projectRole === 'guest') {
    return { visible: false, disabledReason: null }
  }
  if (context.hasLiveRun) return { visible: true, disabledReason: 'An agent is already working on this item.' }
  if (context.claimedBy)
    return { visible: true, disabledReason: `Claimed by ${context.claimedByName ?? 'someone'}.` }
  if (context.projectArchived) return { visible: true, disabledReason: 'The project is archived.' }
  return { visible: true, disabledReason: null }
}

/**
 * Who may ask a run to stop: the person who dispatched it, or an Admin of the run's
 * project (an organization Admin is always one) — the API's rule. Drawing the button is
 * only the guess; the 403 is the answer.
 */
export function canCancelRun(
  run: Pick<Run, 'status' | 'requestedBy' | 'cancelRequested'>,
  viewer: { userId: string | null | undefined; isProjectAdmin: boolean },
): boolean {
  if (!isLiveRun(run.status) || run.cancelRequested) return false
  // A rule-dispatched run has no requester (`requestedBy` is null) to match against —
  // only a project Admin may cancel one of those.
  return (run.requestedBy !== null && run.requestedBy === viewer.userId) || viewer.isProjectAdmin
}

/**
 * Who dispatched a run, for display: a person's name, or which rule did (naming the rule
 * even after it is deleted, since the run still says which one asked). Null means show the
 * agent's own avatar/name as usual — a person dispatched it and the caller already renders
 * who from elsewhere (the run carries no name for a person, only an id).
 */
export function runRequesterLabel(
  run: Pick<Run, 'requestedBy' | 'ruleId' | 'ruleName'>,
): string | null {
  if (run.requestedBy) return null
  if (!run.ruleId) return null
  return run.ruleName ? `Rule: ${run.ruleName}` : 'Rule (deleted)'
}

/** What the run detail says while nothing has picked the run up yet. */
export function runWaitingMessage(run: Pick<Run, 'status' | 'harness'>): string | null {
  if (run.status !== 'queued') return null
  return `Waiting for a runner that offers ${run.harness}…`
}

// ── The log ─────────────────────────────────────────────────────────────────────────

/** The cap marker's sequence: it sorts after anything a runner can send. */
export const TRUNCATED_SEQ = 2147483647

export interface RunLogState {
  lines: RunLogLine[]
  truncated: boolean
}

export const emptyLogState: RunLogState = { lines: [], truncated: false }

/**
 * Merges one fetched page into the log. Pages can arrive out of order (two tail fetches
 * racing), replays collide on their sequence number, and the truncation marker is not a
 * line — it becomes the notice.
 */
export function applyLogPage(state: RunLogState, page: RunLogPage): RunLogState {
  if (page.items.length === 0 && !page.truncated && state.truncated) return state
  const bySeq = new Map<number, RunLogLine>()
  for (const line of state.lines) bySeq.set(line.seq, line)
  for (const line of page.items) {
    if (line.seq >= TRUNCATED_SEQ) continue
    bySeq.set(line.seq, line)
  }
  return {
    lines: [...bySeq.values()].sort((a, b) => a.seq - b.seq),
    truncated: state.truncated || page.truncated,
  }
}

/** The cursor the next tail fetch passes as `after`: everything up to here is on screen. */
export function lastLogSeq(state: RunLogState): number {
  const last = state.lines.at(-1)
  return last ? last.seq : -1
}

/** The log endpoint pages; a full page means there may be more. */
export function hasMoreLogLines(page: RunLogPage, pageSize: number): boolean {
  return page.items.length >= pageSize
}

// ── The choice a dialog remembers ───────────────────────────────────────────────────

const RUN_CHOICE_PREFIX = 'aictiq.run.'

export interface RunChoice {
  playbookId: string | null
  agentId: string | null
}

/** The last playbook and agent used on this project. Storage is a convenience, never a permission. */
export function readRunChoice(projectKey: string): RunChoice | null {
  try {
    const raw = localStorage.getItem(RUN_CHOICE_PREFIX + projectKey)
    if (!raw) return null
    const parsed = JSON.parse(raw) as Partial<RunChoice>
    return { playbookId: parsed.playbookId ?? null, agentId: parsed.agentId ?? null }
  } catch {
    return null
  }
}

export function writeRunChoice(projectKey: string, choice: RunChoice): void {
  try {
    localStorage.setItem(RUN_CHOICE_PREFIX + projectKey, JSON.stringify(choice))
  } catch {
    // A private window loses the memory, not the dispatch.
  }
}

// ── An agent's record ───────────────────────────────────────────────────────────────

const terminal = new Set(['succeeded', 'failed', 'cancelled', 'timedOut'])

/**
 * An agent's success rate over a window: succeeded out of every run that reached an
 * outcome, so queued and running work does not drag the number down.
 */
export function runSuccessRate(
  runs: Pick<Run, 'status' | 'queuedAt'>[],
  since: Date,
  now: Date = new Date(),
): { rate: number | null; total: number } {
  const window = runs.filter(
    (run) => terminal.has(run.status) && new Date(run.queuedAt).getTime() >= since.getTime(),
  )
  if (now.getTime() < since.getTime() || window.length === 0) return { rate: null, total: 0 }
  const succeeded = window.filter((run) => run.status === 'succeeded').length
  return { rate: Math.round((succeeded / window.length) * 100), total: window.length }
}
