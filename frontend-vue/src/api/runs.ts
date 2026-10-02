import type { Paged } from '@/api/items'
import type { PlaybookHarness } from '@/api/playbooks'
import { apiFetch } from '@/utils/api'

/**
 * Agent runs: one dispatch of a playbook by an agent on a runner.
 *
 * Two visibility tiers, like the API's: anyone who can see the item can see a run's
 * status, agent and pull request - the run is the item's history. The raw output (the
 * log, the prompt snapshot, the failure reason) belongs to factory operators; the log
 * and cancel endpoints answer 403 `factory-not-permitted` to anyone else, and the
 * single-run read silently strips the prompt and failure reason.
 */

/**
 * The run's lifecycle, in order. Everything before `succeeded` is *live*: the item is
 * claimed and exactly one live run per item is a database guarantee.
 */
export type RunStatus =
  | 'queued'
  | 'assigned'
  | 'running'
  | 'succeeded'
  | 'failed'
  | 'cancelled'
  | 'timedOut'

export interface Run {
  id: string
  projectId: string
  itemId: string
  itemKey: string
  playbookId: string
  playbookName: string | null
  agentId: string
  agentName: string | null
  /**
   * The person who dispatched the run - who may cancel it. Null when a rule dispatched it
   * instead; `ruleId`/`ruleName` say which one.
   */
  requestedBy: string | null
  /** Set when an automation rule dispatched this run rather than a person. */
  ruleId: string | null
  /** Null once the rule is deleted; the run keeps `ruleId` so "Rule (deleted)" can still be said. */
  ruleName: string | null
  runnerId: string | null
  runnerName: string | null
  /** The runner the run was sent to; null when any free runner may take it. */
  requestedRunnerId: string | null
  /** Null once that runner is deleted. */
  requestedRunnerName: string | null
  status: RunStatus
  harness: PlaybookHarness
  playbookRevisionId: string | null
  maxMinutes: number
  queuedAt: string
  /** When the run may start (UTC); null when it was queued to start as soon as a runner is free. */
  scheduledFor?: string | null
  assignedAt: string | null
  startedAt: string | null
  finishedAt: string | null
  lastHeartbeatAt: string | null
  cancelRequested: boolean
  outcomeSummary: string | null
  pullRequestUrl: string | null
  exitCode: number | null
  costUsd: number | null
  inputTokens: number | null
  outputTokens: number | null
  /** Operators only, and only on the single-run read. Null on lists and for stakeholders. */
  failureReason: string | null
  promptSnapshot: string | null
  /** xmin. */
  version: number
  /** An implement run delivers code; a refine run rewrites the ticket. */
  kind?: 'implement' | 'refine'
  /** The harness session a continue run resumes. Operators only, single-run read only. */
  sessionId?: string | null
  /** The failed run this one continues; null for a run that started fresh. */
  continuesRunId?: string | null
  /** The run that continued this one, once there is one. */
  continuedByRunId?: string | null
  /** Queued by the server after a transient failure rather than by a person. */
  autoContinued?: boolean
  /** Single-run read: the run can be continued now (Continue). */
  continuable?: boolean
  /** Single-run read: the item has a newer run, so neither Continue nor Retry applies. */
  superseded?: boolean
  /** Single-run read: the runs from the first failure to the last continue, oldest first. */
  chain?: RunChainLink[] | null
}

/** One run of a continue chain, with what it cost on its own. */
export interface RunChainLink {
  id: string
  status: RunStatus
  autoContinued: boolean
  queuedAt: string
  finishedAt: string | null
  costUsd: number | null
  inputTokens: number | null
  outputTokens: number | null
}

export type RunLogStream = 'stdout' | 'stderr' | 'event'

export interface RunLogLine {
  seq: number
  at: string
  stream: RunLogStream
  text: string
}

export interface RunLogPage {
  items: RunLogLine[]
  truncated: boolean
}

export interface ListRunsOptions {
  /** Project key. A project the caller cannot see is an empty page, not a 404. */
  project?: string
  /** Agent user id. */
  agent?: string
  status?: RunStatus
  /** Item key, e.g. `PROJ-12`. */
  item?: string
  page?: number
  pageSize?: number
}

export interface DispatchRunBody {
  /** Null names the project's default playbook; with no default the API refuses. */
  playbookId?: string | null
  /** Null names the project's default agent; with no default the API refuses. */
  agentId?: string | null
  /** Only this runner takes the run. Null lets any free runner take it. */
  runnerId?: string | null
  /** When the run may start, as an ISO time with its offset. Null starts it now; a past time is refused. */
  scheduledFor?: string | null
}

const runsBase = (slug: string) => `/orgs/${slug}/runs`

/** Dispatches a run from an item. The dispatch claims the item for the agent in the same transaction. */
export const dispatchRun = (slug: string, itemKey: string, body: DispatchRunBody) =>
  apiFetch<Run>(`/orgs/${slug}/items/${itemKey}/runs`, { method: 'POST', body })

/** Every run of one item, newest first - the item page's run history. */
export const listItemRuns = (slug: string, itemKey: string, page = 1, pageSize = 25) =>
  apiFetch<Paged<Run>>(`/orgs/${slug}/items/${itemKey}/runs`, { query: { page, pageSize } })

/** An organization's runs, newest first, filtered by project, agent, status and/or item. */
export const listRuns = (slug: string, options: ListRunsOptions = {}) =>
  apiFetch<Paged<Run>>(runsBase(slug), { query: { ...options } })

export const getRun = (slug: string, runId: string) => apiFetch<Run>(`${runsBase(slug)}/${runId}`)

/**
 * The raw log after a sequence number (`-1` for everything). The live hub carries the
 * text but not the stream or time of each line - this endpoint is the only source of
 * both, and the cursor semantics are what heals any gap the event stream leaves.
 */
export const getRunLog = (slug: string, runId: string, after = -1, pageSize = 500) =>
  apiFetch<RunLogPage>(`${runsBase(slug)}/${runId}/log`, { query: { after, pageSize } })

/**
 * Cancels a run: terminal at once while queued; a *request* the runner acknowledges on
 * finish while it is already on the machine. Asking twice is the same request.
 */
export const cancelRun = (slug: string, runId: string) =>
  apiFetch<void>(`${runsBase(slug)}/${runId}/cancel`, { method: 'POST' })

/**
 * Continues a failed or timed-out run: a new run resumes its harness session in the workspace
 * its runner kept. It counts as a run, and is refused like a dispatch (409) when the item is
 * claimed, the organization is read-only, or the run cannot be continued.
 */
export const continueRun = (slug: string, runId: string) =>
  apiFetch<Run>(`${runsBase(slug)}/${runId}/continue`, { method: 'POST' })
