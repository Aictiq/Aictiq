import type {
  Run,
  RunChainLink,
  RunLogPage,
  RunLogLine,
  RunScaleUp,
  RunStatus,
  StageVerdict,
} from '@/api/runs'
import { since } from '@/lib/claims'
import { durationLabel } from '@/lib/fleet'

/**
 * Run presentation rules, kept out of the components because they decide what a
 * stakeholder is told and are worth asserting directly - the same reasoning as
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

/** `PROJ-12` names project `PROJ` - the same rule the API applies to item-key routes. */
export function projectKeyOf(itemKey: string): string {
  const cut = itemKey.lastIndexOf('-')
  return cut > 0 ? itemKey.slice(0, cut) : itemKey
}

/** "42 s", "4 min", "1 h 12 m" - the elapsed time of a run that has started. */
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

/** "12.3k" - token counts are for a glance, not an invoice. */
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
 * asleep. Hidden from stakeholders and project guests - the API refuses them anyway -
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
 * project (an organization Admin is always one) - the API's rule. Drawing the button is
 * only the guess; the 403 is the answer.
 */
export function canCancelRun(
  run: Pick<Run, 'status' | 'requestedBy' | 'cancelRequested'>,
  viewer: { userId: string | null | undefined; isProjectAdmin: boolean },
): boolean {
  if (!isLiveRun(run.status) || run.cancelRequested) return false
  // A rule-dispatched run has no requester (`requestedBy` is null) to match against -
  // only a project Admin may cancel one of those.
  return (run.requestedBy !== null && run.requestedBy === viewer.userId) || viewer.isProjectAdmin
}

/**
 * Who dispatched a run, for display: a person's name, or which rule did (naming the rule
 * even after it is deleted, since the run still says which one asked). Null means show the
 * agent's own avatar/name as usual - a person dispatched it and the caller already renders
 * who from elsewhere (the run carries no name for a person, only an id).
 */
export function runRequesterLabel(
  run: Pick<Run, 'requestedBy' | 'ruleId' | 'ruleName'>,
): string | null {
  if (run.requestedBy) return null
  if (!run.ruleId) return null
  return run.ruleName ? `Rule: ${run.ruleName}` : 'Rule (deleted)'
}

/**
 * Whether a finished run offers Retry - a fresh run with the same playbook and agent. Only for
 * an implement run that ended without finishing and is still the item's latest; a run that can
 * be continued offers Continue first, and Retry beside it.
 */
export function canRetryRun(
  run: Pick<Run, 'status' | 'kind' | 'superseded' | 'continuedByRunId'>,
): boolean {
  return (
    (run.status === 'failed' || run.status === 'timedOut') &&
    run.kind !== 'refine' &&
    run.kind !== 'chat' &&
    run.kind !== 'projectChat' &&
    // A stage reviews an implement run's pull request; the server starts it, not a person.
    run.kind !== 'stage' &&
    !run.superseded &&
    !run.continuedByRunId
  )
}

export const stageVerdictLabel: Record<StageVerdict, string> = {
  pass: 'passed',
  changes_requested: 'changes requested',
}

/**
 * The small labels a run of a playbook chain carries beside its playbook: a stage run names
 * its stage ("Stage: Code review") and its verdict once it has one; an implement run the
 * server started to address a stage's findings says "Rework". Null where nothing applies,
 * which is every run of a playbook without stages.
 */
export function runStageLabels(
  run: Pick<Run, 'kind' | 'playbookName' | 'stageVerdict' | 'reworkOfRunId'>,
): { stage: string | null; verdict: StageVerdict | null; rework: boolean } | null {
  if (run.kind === 'stage') {
    return {
      stage: `Stage: ${run.playbookName ?? 'playbook deleted'}`,
      verdict: run.stageVerdict ?? null,
      rework: false,
    }
  }
  if (run.reworkOfRunId) return { stage: null, verdict: null, rework: true }
  return null
}

/** A continue chain's cost and tokens added up; null where no run reported any. */
export function chainTotals(chain: RunChainLink[]): {
  costUsd: number | null
  inputTokens: number | null
  outputTokens: number | null
} {
  const sum = (values: (number | null)[]) =>
    values.some((value) => value !== null)
      ? values.reduce<number>((total, value) => total + (value ?? 0), 0)
      : null
  return {
    costUsd: sum(chain.map((link) => link.costUsd)),
    inputTokens: sum(chain.map((link) => link.inputTokens)),
    outputTokens: sum(chain.map((link) => link.outputTokens)),
  }
}

/**
 * What the run detail says while nothing has picked the run up yet: which runner it waits
 * for and why, when the instance said (`waiting`), so a run held up behind another one does
 * not read as stuck.
 */
export function runWaitingMessage(
  run: Pick<Run, 'status' | 'harness' | 'scheduledFor' | 'waiting'>,
  now: Date = new Date(),
): string | null {
  if (run.status !== 'queued') return null
  const scheduled = runScheduledLabel(run, now)
  if (scheduled) return `${scheduled}. A runner that offers ${run.harness} picks it up after that.`
  const waiting = run.waiting
  if (!waiting) return `Waiting for a runner that offers ${run.harness}…`

  const names = waiting.runners.join(', ')
  const ahead =
    waiting.ahead > 0
      ? ` ${waiting.ahead} queued run${waiting.ahead === 1 ? ' goes' : 's go'} first.`
      : ''
  // Fleet mode's scale from zero helps a run any runner may take, never one sent to a runner.
  const scale = waiting.scaleUp ? ` ${scaleUpMessage(waiting.scaleUp, now)}` : ''
  switch (waiting.reason) {
    case 'runner-offline':
      return `Waiting for ${names}, which is offline. It starts when ${names} comes back.`
    case 'runner-busy':
      return `Waiting for ${names}, which is busy with another run. It starts when that run finishes.${ahead}`
    case 'no-runner':
      return `No online runner offers ${run.harness}. It starts when one connects.${scale}`
    case 'no-matching-runner': {
      const tags = waiting.requiredTags?.length
        ? `the tags ${waiting.requiredTags.join(', ')}`
        : 'the required tags'
      return `No online runner offers ${run.harness} with ${tags}. It starts when one connects.${scale}`
    }
    case 'runners-busy':
      return `Every runner that offers ${run.harness} is busy (${names}). It starts when one of them finishes.${ahead}${scale}`
    case 'no-runner-for-project':
      return `Waiting for a runner that has project ${waiting.projectKey} mapped. No online runner that offers ${run.harness} has a local repository for it: run aictiq runner map ${waiting.projectKey} <path> on one, or add a repository root that holds the project's path hint. It starts after that runner's next heartbeat.`
    case 'runner-missing-project':
      return `Waiting for ${names}, which has no local repository for project ${waiting.projectKey}. Run aictiq runner map ${waiting.projectKey} <path> on it.`
    case 'runner-free':
      return `${names} ${waiting.runners.length === 1 ? 'has' : 'have'} a free slot and should pick it up within seconds.${ahead} If nothing happens, the machine may be finishing another organization's run first.`
  }
}

/**
 * What fleet mode's scale from zero did about the waiting runs: its webhook's last call and
 * how it went, or when it will be called. The call is the organization's, not this run's, so
 * it is said as "last called" rather than claimed for this run.
 */
export function scaleUpMessage(scaleUp: RunScaleUp, now: Date = new Date()): string {
  const when = scaleUp.lastCalledAt ? ` ${since(scaleUp.lastCalledAt, now)}` : ''
  if (scaleUp.error) {
    return `Scale from zero last called its webhook${when}, and the call failed: ${scaleUp.error.replace(/\.$/, '')}.`
  }
  if (scaleUp.lastCalledAt) return `Scale from zero last called its webhook${when}.`
  return `Scale from zero calls its webhook once a run has waited ${durationLabel(scaleUp.afterSeconds)}.`
}

// ── Scheduled runs ──────────────────────────────────────────────────────────────────

/** How long after now a newly scheduled run starts unless the person changes it: overnight, roughly. */
export const DEFAULT_SCHEDULE_DELAY_HOURS = 6

/** The browser's timezone, e.g. `Europe/Sarajevo` - what a scheduled time is entered and shown in. */
export function localTimeZone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone || 'local time'
}

const pad = (value: number) => String(value).padStart(2, '0')

/** A `datetime-local` input's value for a moment, in the browser's timezone, to the minute. */
export function toDateTimeLocalValue(date: Date): string {
  return (
    `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}` +
    `T${pad(date.getHours())}:${pad(date.getMinutes())}`
  )
}

/**
 * The moment a `datetime-local` value names, read in the browser's timezone, or null when it
 * is empty or not a time. The browser parses a date-time without an offset as local time.
 */
export function fromDateTimeLocalValue(value: string): Date | null {
  if (!value) return null
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? null : date
}

/** What the Start at field holds when scheduling is turned on: now + 6 h. */
export function defaultScheduleValue(now: Date = new Date()): string {
  return toDateTimeLocalValue(new Date(now.getTime() + DEFAULT_SCHEDULE_DELAY_HOURS * 3_600_000))
}

/** "Thu 2 Oct, 22:00" in the browser's locale and timezone. */
export function formatScheduledTime(iso: string): string {
  return new Date(iso).toLocaleString(undefined, {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    hour: '2-digit',
    minute: '2-digit',
  })
}

/** A queued run still waiting for its start time - no runner may take it yet. */
export function isWaitingForSchedule(
  run: Pick<Run, 'status' | 'scheduledFor'>,
  now: Date = new Date(),
): boolean {
  return (
    run.status === 'queued' &&
    !!run.scheduledFor &&
    new Date(run.scheduledFor).getTime() > now.getTime()
  )
}

/** "Scheduled for Thu 2 Oct, 22:00" while a run waits for its start time; null otherwise. */
export function runScheduledLabel(
  run: Pick<Run, 'status' | 'scheduledFor'>,
  now: Date = new Date(),
): string | null {
  return isWaitingForSchedule(run, now)
    ? `Scheduled for ${formatScheduledTime(run.scheduledFor!)}`
    : null
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
 * line - it becomes the notice.
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
  /** Null is "any free runner". */
  runnerId?: string | null
}

/** The last playbook, agent and runner used on this project. Storage is a convenience, never a permission. */
export function readRunChoice(projectKey: string): RunChoice | null {
  try {
    const raw = localStorage.getItem(RUN_CHOICE_PREFIX + projectKey)
    if (!raw) return null
    const parsed = JSON.parse(raw) as Partial<RunChoice>
    return {
      playbookId: parsed.playbookId ?? null,
      agentId: parsed.agentId ?? null,
      runnerId: parsed.runnerId ?? null,
    }
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

const REFINE_CHOICE_PREFIX = 'aictiq.refine.'

/** What a refine run was last sent with: kept apart from Hand to agent's, so neither overwrites the other. */
export interface RefineRunChoice {
  /** Null is "any free runner". */
  runnerId: string | null
  /** Null is the refine playbook's harness. */
  harness: string | null
}

/** The last runner and harness used to refine on this project. Storage is a convenience, never a permission. */
export function readRefineRunChoice(projectKey: string): RefineRunChoice | null {
  try {
    const raw = localStorage.getItem(REFINE_CHOICE_PREFIX + projectKey)
    if (!raw) return null
    const parsed = JSON.parse(raw) as Partial<RefineRunChoice>
    return { runnerId: parsed.runnerId ?? null, harness: parsed.harness ?? null }
  } catch {
    return null
  }
}

export function writeRefineRunChoice(projectKey: string, choice: RefineRunChoice): void {
  try {
    localStorage.setItem(REFINE_CHOICE_PREFIX + projectKey, JSON.stringify(choice))
  } catch {
    // A private window loses the memory, not the refinement.
  }
}

/** A runner that has not reported yet may still have the harness; the API decides once it has. */
export const runnerCanRun = (runner: { harnesses: string[] }, harness: string | null) =>
  !harness || runner.harnesses.length === 0 || runner.harnesses.includes(harness)

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
