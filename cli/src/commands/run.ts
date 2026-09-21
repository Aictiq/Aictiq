import { Command } from 'commander'
import type {
  AgentView,
  PagedResult,
  PlaybookView,
  RunLogEntry,
  RunLogPage,
  RunStatus,
  RunView,
} from '../api/views.js'
import { createContext } from '../context.js'
import type { Context, GlobalOptions } from '../context.js'
import { CliError, ExitCode } from '../errors.js'
import {
  formatDateTime,
  formatNumber,
  print,
  printJson,
  renderFields,
  renderTable,
} from '../output.js'
import { normalizeItemKey, projectKeyOf } from '../resolve.js'

const statuses: RunStatus[] = [
  'queued',
  'assigned',
  'running',
  'succeeded',
  'failed',
  'cancelled',
  'timedOut',
]
const terminal = new Set<RunStatus>(['succeeded', 'failed', 'cancelled', 'timedOut'])

/** One page of the log per request; the API clamps at 1000. */
const logPageSize = 200

/**
 * The server's "log truncated" marker sits at `int.MaxValue` (`RunLogChunk.TruncatedSeq`);
 * `truncated` on the page is the flag, so the marker row itself is not printed as output.
 */
const truncatedSeq = 2147483647

/**
 * `--follow` polls rather than joining the hub: a CLI has no SignalR client and a poll every
 * two seconds is the right cost for a log that grows a few lines at a time. The interval is
 * an environment variable so a test does not spend two real seconds per iteration.
 */
const defaultPollMs = 2000

export function pollIntervalMs(env: NodeJS.ProcessEnv = process.env): number {
  const raw = env.AICTIQ_RUN_POLL_MS
  if (raw === undefined || raw === '') return defaultPollMs
  const parsed = Number(raw)
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : defaultPollMs
}

export function runCommand(globals: () => GlobalOptions): Command {
  const run = new Command('run').description('Factory runs: hand an item to an agent and watch it')

  run
    .command('start')
    .description('Dispatch a run for a work item')
    .argument('<key>', 'Work item key, e.g. ACME-123')
    .option('--playbook <nameOrId>', "One of the project's playbooks; the default when omitted")
    .option(
      '--agent <nameOrId>',
      "An agent of the project; the project's default agent when omitted",
    )
    .action(async (key: string, options: { playbook?: string; agent?: string }) => {
      const ctx = createContext(globals())
      const orgSlug = await ctx.org()
      const itemKey = normalizeItemKey(key)
      const playbookId = options.playbook
        ? await resolvePlaybookId(ctx, projectKeyOf(itemKey), options.playbook)
        : undefined
      const agentId = options.agent ? await resolveAgentId(ctx, options.agent) : undefined

      const started = await ctx.client.request<
        RunView,
        '/api/v1/orgs/{orgSlug}/items/{itemKey}/runs',
        'post'
      >('post', '/api/v1/orgs/{orgSlug}/items/{itemKey}/runs', {
        path: { orgSlug, itemKey },
        body: {
          ...(playbookId === undefined ? {} : { playbookId }),
          ...(agentId === undefined ? {} : { agentId }),
        },
      })

      if (ctx.json) return printJson(started)
      print(
        renderFields([
          ['Run', started.id],
          ['Item', started.itemKey],
          ['Status', started.status],
          ['Agent', started.agentName ?? started.agentId],
          ['Playbook', started.playbookName ?? started.playbookId],
        ]),
      )
    })

  run
    .command('list')
    .description('List runs, newest first')
    .option('-p, --project <key>', 'Only runs in this project')
    .option('--status <status>', `One of ${statuses.join(', ')}`)
    .option('--agent <id>', 'Only runs by this agent (user id)')
    .option('--item <key>', 'Only runs for this item')
    .option('--page <n>', 'Page number', '1')
    .option('--page-size <n>', 'Runs per page (max 100)', '25')
    .action(
      async (options: {
        project?: string
        status?: string
        agent?: string
        item?: string
        page: string
        pageSize: string
      }) => {
        const ctx = createContext(globals())
        if (options.status !== undefined && !statuses.includes(options.status as RunStatus)) {
          throw new CliError(
            `Unknown run status "${options.status}". Statuses: ${statuses.join(', ')}.`,
            ExitCode.Validation,
          )
        }
        const page = await ctx.client.request<
          PagedResult<RunView>,
          '/api/v1/orgs/{orgSlug}/runs',
          'get'
        >('get', '/api/v1/orgs/{orgSlug}/runs', {
          path: { orgSlug: await ctx.org() },
          query: {
            project: options.project?.toUpperCase(),
            status: options.status,
            agent: options.agent,
            item: options.item === undefined ? undefined : normalizeItemKey(options.item),
            page: options.page,
            pageSize: options.pageSize,
          },
        })

        if (ctx.json) return printJson(page)
        if (page.items.length === 0) return print('No runs match.')
        print(
          renderTable(page.items, [
            { header: 'ID', value: (r) => r.id },
            { header: 'ITEM', value: (r) => r.itemKey },
            { header: 'STATUS', value: (r) => r.status },
            { header: 'AGENT', value: (r) => r.agentName ?? r.agentId },
            { header: 'PLAYBOOK', value: (r) => r.playbookName ?? r.playbookId },
            { header: 'QUEUED', value: (r) => formatDateTime(r.queuedAt) },
            { header: 'FINISHED', value: (r) => formatDateTime(r.finishedAt) },
            { header: 'PR', value: (r) => r.pullRequestUrl ?? '' },
          ]),
        )
        print('')
        print(`Page ${page.page} of ${Math.max(1, page.totalPages)} · ${page.totalCount} runs`)
      },
    )

  run
    .command('view')
    .description('Show a run')
    .argument('<runId>', 'Run id')
    .action(async (runId: string) => {
      const ctx = createContext(globals())
      const found = await getRun(ctx, await ctx.org(), runId)
      if (ctx.json) return printJson(found)

      const cost =
        found.costUsd === null && found.inputTokens === null && found.outputTokens === null
          ? undefined
          : [
              found.costUsd === null ? undefined : `$${found.costUsd.toFixed(4)}`,
              found.inputTokens === null && found.outputTokens === null
                ? undefined
                : `${formatNumber(found.inputTokens) || '0'} in / ${formatNumber(found.outputTokens) || '0'} out tokens`,
            ]
              .filter((part) => part !== undefined)
              .join(' · ')

      print(
        renderFields([
          ['Run', found.id],
          ['Item', found.itemKey],
          ['Status', found.status],
          ['Agent', found.agentName ?? found.agentId],
          ['Playbook', found.playbookName ?? found.playbookId],
          ['Runner', found.runnerName ?? found.runnerId ?? undefined],
          ['Harness', found.harness],
          [
            'Requested by',
            found.requestedBy ?? (found.ruleId ? `rule: ${found.ruleName ?? '(deleted)'}` : undefined),
          ],
          ['Max minutes', String(found.maxMinutes)],
          ['Queued', formatDateTime(found.queuedAt)],
          ['Assigned', formatDateTime(found.assignedAt)],
          ['Started', formatDateTime(found.startedAt)],
          ['Finished', formatDateTime(found.finishedAt)],
          ['Last heartbeat', formatDateTime(found.lastHeartbeatAt)],
          ['Cancel requested', found.cancelRequested ? 'yes' : undefined],
          ['Outcome', found.outcomeSummary ?? undefined],
          ['PR', found.pullRequestUrl ?? undefined],
          ['Exit code', found.exitCode === null ? undefined : String(found.exitCode)],
          ['Cost', cost],
          ['Failure', found.failureReason ?? undefined],
        ]),
      )
    })

  run
    .command('logs')
    .description("Print a run's log; --follow keeps polling until the run finishes")
    .argument('<runId>', 'Run id')
    .option('-f, --follow', 'Poll for new output every 2 s until the run is finished')
    .action(async (runId: string, options: { follow?: boolean }) => {
      const ctx = createContext(globals())
      await streamLog(ctx, await ctx.org(), runId, options.follow === true, pollIntervalMs())
    })

  run
    .command('cancel')
    .description('Ask a run to stop; the runner ends the harness and the run records cancelled')
    .argument('<runId>', 'Run id')
    .action(async (runId: string) => {
      const ctx = createContext(globals())
      await ctx.client.request<undefined, '/api/v1/orgs/{orgSlug}/runs/{runId}/cancel', 'post'>(
        'post',
        '/api/v1/orgs/{orgSlug}/runs/{runId}/cancel',
        { path: { orgSlug: await ctx.org(), runId } },
      )
      if (ctx.json) return printJson({ id: runId, cancelRequested: true })
      print(`Cancel requested for run ${runId}.`)
    })

  return run
}

async function getRun(ctx: Context, orgSlug: string, runId: string): Promise<RunView> {
  return ctx.client.request<RunView, '/api/v1/orgs/{orgSlug}/runs/{runId}', 'get'>(
    'get',
    '/api/v1/orgs/{orgSlug}/runs/{runId}',
    { path: { orgSlug, runId } },
  )
}

/**
 * Prints the log from the start and, when following, keeps going until the run is in a
 * terminal state and one last page after that has been drained — the status is read
 * *before* each page, so output written between the last page and the finish is never
 * missed. A full page is followed by another request straight away; a short one waits.
 */
async function streamLog(
  ctx: Context,
  orgSlug: string,
  runId: string,
  follow: boolean,
  pollMs: number,
): Promise<void> {
  let after = -1
  let notedTruncation = false
  for (;;) {
    const finished = follow ? terminal.has((await getRun(ctx, orgSlug, runId)).status) : true
    for (;;) {
      const page = await ctx.client.request<
        RunLogPage,
        '/api/v1/orgs/{orgSlug}/runs/{runId}/log',
        'get'
      >('get', '/api/v1/orgs/{orgSlug}/runs/{runId}/log', {
        path: { orgSlug, runId },
        query: { after, pageSize: logPageSize },
      })
      for (const entry of page.items) {
        after = Math.max(after, entry.seq)
        if (entry.seq !== truncatedSeq) print(formatLogEntry(entry))
      }
      if (page.truncated && !notedTruncation) {
        notedTruncation = true
        print('(log truncated)')
      }
      if (page.items.length < logPageSize) break
    }
    if (finished) return
    await new Promise<void>((resolve) => setTimeout(resolve, pollMs))
  }
}

export function formatLogEntry(entry: RunLogEntry): string {
  const text = entry.text.endsWith('\n') ? entry.text.slice(0, -1) : entry.text
  if (entry.stream === 'stdout') return text
  const prefix = entry.stream === 'stderr' ? '[stderr] ' : '[event] '
  return text
    .split('\n')
    .map((line) => `${prefix}${line}`)
    .join('\n')
}

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

/** An id passes through; a name is matched case-insensitively against the project's playbooks. */
async function resolvePlaybookId(
  ctx: Context,
  projectKey: string,
  playbook: string,
): Promise<string> {
  if (uuid.test(playbook)) return playbook
  const playbooks = await ctx.client.request<
    PlaybookView[],
    '/api/v1/orgs/{orgSlug}/projects/{projectKey}/playbooks',
    'get'
  >('get', '/api/v1/orgs/{orgSlug}/projects/{projectKey}/playbooks', {
    path: { orgSlug: await ctx.org(), projectKey },
  })
  const wanted = playbook.trim().toLowerCase()
  const matches = playbooks.filter((p) => p.name.toLowerCase() === wanted)
  if (matches.length === 1) return matches[0]!.id
  if (matches.length === 0) {
    throw new CliError(
      `No playbook "${playbook}" in ${projectKey}. Playbooks: ${playbooks.map((p) => p.name).join(', ') || '(none)'}.`,
      ExitCode.Validation,
    )
  }
  throw new CliError(
    `Several playbooks in ${projectKey} are named "${playbook}"; pass one by id: ${matches.map((p) => p.id).join(', ')}.`,
    ExitCode.Validation,
  )
}

/** An id passes through; a display name is matched case-insensitively against the organization's agents. */
async function resolveAgentId(ctx: Context, agent: string): Promise<string> {
  if (uuid.test(agent)) return agent
  const agents = await ctx.client.request<AgentView[], '/api/v1/orgs/{orgSlug}/agents', 'get'>(
    'get',
    '/api/v1/orgs/{orgSlug}/agents',
    { path: { orgSlug: await ctx.org() } },
  )
  const byId = agents.find((a) => a.userId === agent)
  if (byId) return byId.userId
  const wanted = agent.trim().toLowerCase()
  const matches = agents.filter((a) => a.displayName.toLowerCase() === wanted)
  if (matches.length === 1) return matches[0]!.userId
  if (matches.length === 0) {
    throw new CliError(
      `No agent "${agent}" in this organization. Agents: ${agents.map((a) => a.displayName).join(', ') || '(none)'}.`,
      ExitCode.Validation,
    )
  }
  throw new CliError(
    `Several agents are named "${agent}"; pass one by id: ${matches.map((a) => a.userId).join(', ')}.`,
    ExitCode.Validation,
  )
}
