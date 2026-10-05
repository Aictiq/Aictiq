/**
 * The runner's shared vocabulary: what the instance hands over on a claim
 * (`RunnerRunClaimed`), what the runner reports about itself (`RunnerCapabilities`, v1),
 * and the seams between the poll loop, the workspace and the harness adapters.
 */

export type HarnessName = 'claude' | 'codex' | 'opencode' | 'cursor'

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
  /**
   * Set by `runner start`: whether a service definition from `install-service` started it, so
   * the web UI's setup guide can tell a runner that survives a reboot from one in a terminal.
   */
  service?: boolean
  /** This organization's project keys mapped with `runner map`. Reported to that organization only. */
  workspaces?: string[]
  /** This organization's repository roots (`runner root`), so the web UI can check a path hint. */
  repoRoots?: string[]
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
  /** Absent on older servers: keep the branch-and-PR workflow. */
  workOnDefaultBranch?: boolean
  maxMinutes: number
  aictiqUrl: string | null
  agentToken: string
  agentTokenDisplay: string | null
  heartbeatIntervalSeconds: number
  /**
   * Set when the run continues a failed one: the harness resumes `sessionId` in the
   * workspace the failed run kept on this runner, instead of starting fresh.
   */
  resume?: RunResume | null
  /**
   * Set when someone asked the agent for more work in an item comment, after an earlier
   * implement run. `branchName` is then the earlier run's branch, `resume` is null, and the
   * prompt already quotes the request; the runner resumes the earlier session when it still has
   * its workspace and decides which branch the work goes on.
   */
  followUp?: RunFollowUp | null
}

export interface RunResume {
  /** The failed run whose kept workspace this run works in. */
  continuesRunId: string
  sessionId: string
  /** Why the failed run stopped, told to the agent. */
  failureReason: string | null
}

export interface RunFollowUp {
  /** The earlier implement run this one follows up; its kept workspace holds the session. */
  previousRunId: string
  /** That run's harness session, or null when it never reported one. */
  sessionId: string | null
  /** The branch the earlier run worked on (also sent as run.branchName). */
  previousBranchName: string
  /** The pull request the earlier run opened, when it reported one. */
  pullRequestUrl: string | null
  /** The branch to start from the default branch when that pull request was merged or closed. */
  newBranchName: string
  /** The comment text the person wrote: what they want changed. */
  instruction: string
  /** Display name of the person who asked, or null. */
  requestedByName: string | null
  /** The comment that asked; the server replies in its thread when the run finishes. */
  commentId: string
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
  /** The harness session, so the run can be continued later. */
  sessionId?: string | null
  /** The absolute checkout the session lives in, kept for a person to resume it by hand. */
  workspacePath?: string | null
  /**
   * The branch the run delivered on, sent only when it is not the claimed `branchName`: a
   * follow-up whose earlier pull request was merged or closed starts a new branch.
   */
  branchName?: string | null
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
  /** The harness's session or thread id, when this line names it. */
  sessionId?: string
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
  /** Resume this harness session rather than start one; `prompt` is then the continue message. */
  resumeSessionId?: string
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
  /**
   * Files the harness needs in the checkout for one invocation, such as a config it only
   * reads from there. Runs before the harness starts; the returned function puts the checkout
   * back once it has exited, so nothing outlives the run or reaches a commit.
   */
  prepare?(context: InvocationContext): Promise<() => Promise<void>>
  parse(line: string): ParsedLine
  /** `resuming` is set when the invocation resumed a session, so a lost session can be told apart. */
  outcome(
    exitCode: number | null,
    lastLines: string[],
    lastResult: string | null,
    resuming?: boolean,
  ): HarnessOutcome
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
