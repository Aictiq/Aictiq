/**
 * The runner's shared vocabulary: what the instance hands over on a claim
 * (`RunnerRunClaimed`), what the runner reports about itself (`RunnerCapabilities`, v1),
 * and the seams between the poll loop, the workspace and the harness adapters.
 */

export type HarnessName = 'claude' | 'codex' | 'opencode'

export interface HarnessInfo {
  name: string
  version: string | null
}

/** `RunnerCapabilities` on the server - a versioned contract, not an implementation detail. */
export interface RunnerCapabilities {
  v: 1
  harnesses: HarnessInfo[]
  os: string | null
  arch: string | null
  cliVersion: string | null
  maxParallel: number
  /** The same for every organization this machine serves; groups them in the web UI, authorizes nothing. */
  machineId?: string
}

export interface RunnerHello {
  runnerId: string
  name: string
  organizationSlug: string
  heartbeatIntervalSeconds: number
  pollTimeoutSeconds: number
  maxLogBytes: number
  maxLogBatchBytes: number
  maxRunMinutes: number
}

export interface RunRepo {
  /** `github` when the project has a binding, `local` when the runner finds the working copy. */
  source: 'github' | 'local'
  repoFullName: string | null
  cloneToken: string | null
  localPathHint: string | null
}

/** Metadata for an item attachment which the runner can make available beside its checkout. */
export interface RunAttachment {
  id: string
  fileName: string
  contentType: string
  sizeBytes: number
  /** Undefined for an attachment on the item description itself. */
  commentId?: string | null
}

export interface ClaimedRun {
  runId: string
  itemId: string
  itemKey: string
  projectId: string
  projectKey: string
  organizationSlug: string
  harness: string
  prompt: string
  playbookRevisionId: string | null
  repo: RunRepo
  defaultBranch: string
  branchName: string
  maxMinutes: number
  aictiqUrl: string | null
  agentToken: string
  agentTokenDisplay: string | null
  heartbeatIntervalSeconds: number
}

export type LogStream = 'stdout' | 'stderr' | 'event'

export type RunnerOutcome = 'succeeded' | 'failed' | 'cancelled'

export interface FinishReport {
  outcome: RunnerOutcome
  exitCode?: number | null
  summary?: string | null
  pullRequestUrl?: string | null
  costUsd?: number | null
  inputTokens?: number | null
  outputTokens?: number | null
  failureReason?: string | null
}

/** One stdout line of a harness, as the adapter reads it. */
export interface ParsedLine {
  /** What goes into the run log; null drops the line (e.g. a noisy init event). */
  log: string | null
  progress?: string
  costUsd?: number
  inputTokens?: number
  outputTokens?: number
  /** The cost and token counts are this step's alone, to add to the run's total rather than replace it. */
  accumulate?: boolean
  /** A final answer the harness produced, used as the success summary. */
  result?: string
}

export interface HarnessInvocation {
  command: string
  args: string[]
  /** Written to the child's stdin and then closed, when the harness reads its prompt there. */
  stdin?: string
  env?: Record<string, string>
}

export interface HarnessOutcome {
  outcome: 'succeeded' | 'failed'
  summary: string | null
  failureReason: string | null
}

/** Everything an adapter may need to start a harness for one run. */
export interface InvocationContext {
  prompt: string
  promptFile: string
  /** The git checkout the harness works in (its cwd). */
  workspace: string
  /** Read-only, per-run files that live beside the checkout and must never be committed. */
  attachmentsDir: string
  /**
   * A Claude-style MCP config (`{ mcpServers: { aictiq: { command, args } } }`) written
   * outside the checkout, so nothing the agent might commit carries it.
   */
  mcpConfigFile: string
  /** The same server as a command line, for harnesses configured by flags or their own file. */
  mcpServer: { command: string; args: string[] }
}

export interface HarnessAdapter {
  readonly name: HarnessName
  /** The executable's `--version`, or null when it is not on PATH. */
  available(env?: NodeJS.ProcessEnv): Promise<HarnessInfo | null>
  /**
   * How to start the harness. The prompt is already in `promptFile` (0600, outside the
   * checkout): adapters pass it on stdin or by path rather than in argv, which has a length
   * limit and is readable by every user through /proc.
   */
  invocation(context: InvocationContext): HarnessInvocation
  parse(line: string): ParsedLine
  outcome(exitCode: number | null, lastLines: string[], lastResult: string | null): HarnessOutcome
}

/** A failure the runner reports as the run's `failureReason` rather than crashing on. */
export class RunFailure extends Error {
  readonly reason: string
  constructor(reason: string, message: string) {
    super(message)
    this.name = 'RunFailure'
    this.reason = reason
  }
}
