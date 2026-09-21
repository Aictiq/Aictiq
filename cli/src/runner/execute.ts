import { execFile } from 'node:child_process'
import { stripVTControlCharacters } from 'node:util'
import { RunnerHttpError, RunnerNetworkError } from './client.js'
import type { RunnerClient } from './client.js'
import { LogStreamer } from './log.js'
import { spawnSupervised } from './process.js'
import type { SupervisedProcess } from './process.js'
import { RunFailure } from './types.js'
import type { ClaimedRun, FinishReport, HarnessAdapter, RunnerHello } from './types.js'
import { provisionWorkspace } from './workspace.js'
import type { Workspace, WorkspaceOptions } from './workspace.js'

const MaxSummary = 4000
const MaxFailureReason = 500
const PullRequestUrl = /https:\/\/github\.com\/[\w.-]+\/[\w.-]+\/pull\/\d+/g

export interface ExecuteOptions {
  client: RunnerClient
  hello: RunnerHello
  adapters: Partial<Record<string, HarnessAdapter>>
  /** Workspace settings shared by every run; the per-run hooks are filled in here. */
  workspace: Omit<WorkspaceOptions, 'event' | 'refreshCloneToken' | 'signal'>
  /** The runner's own secret, redacted from the log like the run's tokens. */
  runnerToken: string
  /** The runner is shutting down and will not wait: stop the harness and report `cancelled`. */
  shutdown?: AbortSignal
  /** Runner-side messages for the operator's terminal (never sent to the instance). */
  local?: (message: string) => void
  provision?: (run: ClaimedRun, options: WorkspaceOptions) => Promise<Workspace>
  findPullRequest?: (
    checkout: string,
    branch: string,
    env: NodeJS.ProcessEnv,
  ) => Promise<string | null>
  heartbeatMs?: number
  graceMs?: number
  flushIntervalMs?: number
}

/**
 * Executes one claimed run from `started` to `finish`. The runner decides nothing about
 * the item — transitions, the outcome comment and the claim release are the server's
 * business once `finish` lands. What it does own: a workspace, a harness process,
 * the log, the heartbeat, and killing the process when told to or when time runs out.
 *
 * Returns the report it sent, or null when the server had already closed the run (a
 * sweeper's verdict, or a finish that was refused as a duplicate) and there was nothing
 * left to report.
 */
export async function executeRun(
  run: ClaimedRun,
  options: ExecuteOptions,
): Promise<FinishReport | null> {
  const { client } = options
  const local = options.local ?? (() => {})
  const log = new LogStreamer({
    send: (chunks) => client.log(run.runId, chunks),
    secrets: [run.agentToken, options.runnerToken, run.repo.cloneToken ?? ''],
    maxBatchBytes: options.hello.maxLogBatchBytes,
    flushIntervalMs: options.flushIntervalMs ?? 1000,
    local: (message) => local(`${run.itemKey}: ${message}`),
  })
  const event = (line: string) => log.push('event', line)

  let harness: SupervisedProcess | undefined
  let stopReason: 'cancelled' | 'timed-out' | 'shutdown' | 'closed' | undefined
  const abort = new AbortController()
  const stop = (reason: NonNullable<typeof stopReason>) => {
    if (stopReason) return
    stopReason = reason
    abort.abort()
    void harness?.stop(options.graceMs ?? 10_000)
  }

  const onShutdown = () => stop('shutdown')
  options.shutdown?.addEventListener('abort', onShutdown, { once: true })
  if (options.shutdown?.aborted) onShutdown()

  let workspace: Workspace | undefined
  let revoked: RunnerHttpError | undefined
  let heartbeat: NodeJS.Timeout | undefined
  let beating: Promise<void> = Promise.resolve()
  let deadline: NodeJS.Timeout | undefined

  const report = await (async (): Promise<FinishReport | null> => {
    try {
      await client.started(run.runId)
    } catch (error) {
      // 409: the run was cancelled or swept between the claim and now. Nothing to execute.
      if (error instanceof RunnerHttpError && (error.status === 409 || error.status === 404)) {
        local(`${run.itemKey}: the instance closed run ${run.runId} before it started`)
        return null
      }
      throw error
    }
    event(`Runner picked up ${run.itemKey} with ${run.harness}`)

    // The heartbeat starts before anything slow: the item's claim was taken at dispatch and
    // goes stale if nobody refreshes it while the workspace is being cloned.
    const beat = async () => {
      try {
        const { cancelRequested } = await client.runHeartbeat(run.runId)
        if (cancelRequested) {
          event('Cancel requested; stopping the harness')
          stop('cancelled')
        }
      } catch (error) {
        if (error instanceof RunnerHttpError && error.revoked) {
          // The runner's secret is dead, so no finish can land either: stop now and let the
          // loop exit rather than run the harness on until the next runner heartbeat.
          revoked ??= error
          stop('closed')
          return
        }
        if (error instanceof RunnerHttpError && (error.status === 409 || error.status === 404)) {
          local(`${run.itemKey}: the instance closed run ${run.runId}; stopping it here`)
          stop('closed')
          return
        }
        local(`${run.itemKey}: run heartbeat failed: ${message(error)}`)
      }
      try {
        await client.itemHeartbeat(run)
      } catch (error) {
        local(`${run.itemKey}: item heartbeat failed: ${message(error)}`)
      }
    }
    const tick = () => {
      beating = beat()
    }
    tick()
    heartbeat = setInterval(
      tick,
      options.heartbeatMs ?? run.heartbeatIntervalSeconds * 1000,
    )
    deadline = setTimeout(() => {
      event(`The run reached its limit of ${run.maxMinutes} minutes; stopping the harness`)
      stop('timed-out')
    }, run.maxMinutes * 60_000)

    const adapter = options.adapters[run.harness]
    const info = adapter ? await adapter.available() : null
    if (!adapter || !info) {
      event(`Harness "${run.harness}" is not installed on this runner`)
      return failed(
        'harness-unavailable',
        `This runner has no working "${run.harness}" on its PATH.`,
      )
    }
    event(`Using ${run.harness} ${info.version ?? ''}`.trim())

    try {
      workspace = await (options.provision ?? provisionWorkspace)(run, {
        ...options.workspace,
        event,
        refreshCloneToken: () => client.repoToken(run.runId),
        attachments: {
          list: () => client.listAttachments(run),
          download: (attachmentId) => client.downloadAttachment(run, attachmentId),
        },
        signal: abort.signal,
      })
    } catch (error) {
      if (stopReason) return stopped(null, [])
      if (error instanceof RunFailure) {
        event(error.message)
        return failed(error.reason, error.message)
      }
      event(`Workspace provisioning failed: ${message(error)}`)
      return failed('workspace-failed', message(error))
    }
    if (stopReason) return stopped(null, [])

    const invocation = adapter.invocation({
      prompt: workspace.prompt,
      promptFile: workspace.promptFile,
      workspace: workspace.checkout,
      attachmentsDir: workspace.attachmentsDir,
      mcpConfigFile: workspace.mcpConfigFile,
      mcpServer: options.workspace.mcpServer,
    })
    const env: NodeJS.ProcessEnv = {
      ...process.env,
      ...invocation.env,
      ...workspace.env,
      // The agent reaches the instance as itself, never as the runner: `aictiq mcp` and the
      // CLI both read these, and they exist only in this process tree.
      AICTIQ_URL: run.aictiqUrl ?? client.baseUrl,
      AICTIQ_TOKEN: run.agentToken,
      AICTIQ_ORG: run.organizationSlug,
      AICTIQ_ITEM: run.itemKey,
      AICTIQ_RUN: run.runId,
    }

    const lastLines: string[] = []
    let lastResult: string | null = null
    let pullRequestUrl: string | null = null
    let costUsd: number | undefined
    let inputTokens: number | undefined
    let outputTokens: number | undefined
    const remember = (line: string) => {
      lastLines.push(line)
      if (lastLines.length > 20) lastLines.shift()
    }

    event(`Starting ${invocation.command} in ${workspace.checkout}`)
    harness = spawnSupervised({
      command: invocation.command,
      args: invocation.args,
      cwd: workspace.checkout,
      env,
      ...(invocation.stdin === undefined ? {} : { stdin: invocation.stdin }),
      onLine: (stream, raw) => {
        // Harnesses colour their errors even into a pipe; the log view and the summary
        // are plain text.
        const line = stripVTControlCharacters(raw)
        for (const match of line.matchAll(PullRequestUrl)) pullRequestUrl = match[0]
        if (stream === 'stderr') {
          log.push('stderr', line)
          remember(line)
          return
        }
        const parsed = adapter.parse(line)
        const tally = (total: number | undefined, value: number | undefined) =>
          value === undefined ? total : parsed.accumulate ? (total ?? 0) + value : value
        costUsd = tally(costUsd, parsed.costUsd)
        inputTokens = tally(inputTokens, parsed.inputTokens)
        outputTokens = tally(outputTokens, parsed.outputTokens)
        if (parsed.result !== undefined) lastResult = parsed.result
        if (parsed.log !== null) {
          log.push('stdout', parsed.log)
          remember(parsed.log)
        }
      },
    })
    // A stop requested while the process was being spawned must still reach it.
    if (stopReason) void harness.stop(options.graceMs ?? 10_000)

    const exitCode = await harness.exited
    event(exitCode === null ? 'Harness was killed' : `Harness exited with code ${exitCode}`)
    const usage = { costUsd, inputTokens, outputTokens, exitCode }
    if (stopReason) return stopped(exitCode, lastLines, usage)

    const verdict = adapter.outcome(exitCode, lastLines, lastResult)
    if (verdict.outcome === 'succeeded' && !pullRequestUrl) {
      pullRequestUrl = await (options.findPullRequest ?? findPullRequest)(
        workspace.checkout,
        run.branchName,
        env,
      )
    }
    if (pullRequestUrl) event(`Pull request: ${pullRequestUrl}`)
    return {
      outcome: verdict.outcome,
      exitCode,
      summary: truncate(verdict.summary, MaxSummary),
      pullRequestUrl,
      failureReason:
        verdict.outcome === 'failed'
          ? truncate(verdict.failureReason ?? 'harness-failed', MaxFailureReason)
          : null,
      costUsd: costUsd ?? null,
      inputTokens: inputTokens ?? null,
      outputTokens: outputTokens ?? null,
    }
  })()
    .catch((error: unknown): FinishReport | null => {
      if (error instanceof RunnerHttpError && error.revoked) throw error
      local(`${run.itemKey}: ${message(error)}`)
      event(`Runner error: ${message(error)}`)
      return failed('runner-error', message(error))
    })
    .finally(() => {
      clearInterval(heartbeat)
      clearTimeout(deadline)
      options.shutdown?.removeEventListener('abort', onShutdown)
    })

  if (report?.outcome === 'failed' && report.failureReason)
    event(`Run failed: ${report.failureReason}`)
  // A beat still in flight would otherwise reach the instance after the finish, and its
  // item heartbeat with a token the finish just revoked.
  await beating
  await log.close()
  await workspace?.cleanup()
  if (revoked) throw revoked
  if (!report) return null
  return (await sendFinish(client, run, report, local)) ? report : null

  function failed(reason: string, detail: string): FinishReport {
    return {
      outcome: 'failed',
      failureReason: truncate(reason, MaxFailureReason),
      summary: truncate(detail, MaxSummary),
    }
  }

  function stopped(
    exitCode: number | null,
    lines: string[],
    usage: Partial<FinishReport> = {},
  ): FinishReport | null {
    const tail = lines.length > 0 ? lines.join('\n') : null
    switch (stopReason) {
      case 'closed':
        return null
      case 'timed-out':
        // `timed_out` is the server's verdict alone (its sweeper would reach it too); the
        // runner reports a failure whose reason says the same thing.
        return {
          ...usage,
          outcome: 'failed',
          exitCode,
          failureReason: 'timed-out',
          summary: truncate(tail, MaxSummary),
        }
      case 'shutdown':
        return {
          ...usage,
          outcome: 'cancelled',
          exitCode,
          failureReason: 'runner-shutdown',
          summary: 'The runner was stopped before the run finished.',
        }
      default:
        return { ...usage, outcome: 'cancelled', exitCode, summary: truncate(tail, MaxSummary) }
    }
  }
}

/** Reports the outcome, retrying while the instance is unreachable; false if it was refused. */
async function sendFinish(
  client: RunnerClient,
  run: ClaimedRun,
  report: FinishReport,
  local: (message: string) => void,
): Promise<boolean> {
  for (let attempt = 0; ; attempt++) {
    try {
      await client.finish(run.runId, report)
      local(
        `${run.itemKey}: ${report.outcome}${report.failureReason ? ` (${report.failureReason})` : ''}`,
      )
      return true
    } catch (error) {
      if (error instanceof RunnerNetworkError && attempt < 30) {
        await new Promise((resolve) => setTimeout(resolve, Math.min(30_000, 1000 * 2 ** attempt)))
        continue
      }
      if (error instanceof RunnerHttpError && error.revoked) throw error
      // 409: already finished (a sweeper got there first). Anything else: the server's
      // sweepers will close the run; looping here would only hold the slot.
      local(`${run.itemKey}: could not report the outcome: ${message(error)}`)
      return false
    }
  }
}

/** `gh pr view <branch>`, when `gh` is installed and signed in; null otherwise. */
export function findPullRequest(
  checkout: string,
  branch: string,
  env: NodeJS.ProcessEnv,
): Promise<string | null> {
  return new Promise((resolve) => {
    execFile(
      'gh',
      ['pr', 'view', branch, '--json', 'url', '--jq', '.url'],
      { cwd: checkout, env, timeout: 15_000 },
      (error, stdout) => {
        const url = error ? '' : stdout.trim()
        resolve(/^https:\/\/\S+\/pull\/\d+$/.test(url) ? url : null)
      },
    )
  })
}

function truncate(value: string | null | undefined, max: number): string | null {
  if (!value) return null
  return value.length <= max ? value : `${value.slice(0, max - 1)}…`
}

function message(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}
