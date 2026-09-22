import { Command, Option } from 'commander'
import type { CommentView, ItemLinkView, PagedResult, WorkItemView } from '../api/views.js'
import type { WorkItemPriority, WorkItemType } from '../api/views.js'
import { createContext } from '../context.js'
import type { Context, GlobalOptions } from '../context.js'
import { CliError, ExitCode } from '../errors.js'
import { formatDate, formatNumber, print, printJson, renderFields, renderTable } from '../output.js'
import {
  branchName,
  findState,
  getItem,
  getLabels,
  getTeams,
  getWorkflow,
  normalizeItemKey,
  projectKeyOf,
  readBody,
  resolveLabelIds,
} from '../resolve.js'

const types: WorkItemType[] = ['epic', 'feature', 'story', 'task', 'bug']
const priorities: WorkItemPriority[] = ['none', 'low', 'medium', 'high', 'urgent']

export function itemCommand(globals: () => GlobalOptions): Command {
  const item = new Command('item').description('Work items')

  item
    .command('list')
    .description('List work items in a project')
    .requiredOption('-p, --project <key>', 'Project key, e.g. ACME')
    .option(
      '-f, --filter <expression>',
      'Item filter, e.g. "state:active assignee:@me label:backend"',
    )
    .option('-q, --query <text>', 'Full-text search within the project')
    .option('-s, --sort <field>', 'Sort expression accepted by the API')
    .option('--page <n>', 'Page number', '1')
    .option('--page-size <n>', 'Items per page (max 100)', '25')
    .action(
      async (options: {
        project: string
        filter?: string
        query?: string
        sort?: string
        page: string
        pageSize: string
      }) => {
        const ctx = createContext(globals())
        const page = await ctx.client.request<
          PagedResult<WorkItemView>,
          '/api/v1/orgs/{orgSlug}/projects/{projectKey}/items',
          'get'
        >('get', '/api/v1/orgs/{orgSlug}/projects/{projectKey}/items', {
          path: { orgSlug: await ctx.org(), projectKey: options.project.toUpperCase() },
          query: {
            filter: options.filter,
            q: options.query,
            sort: options.sort,
            page: options.page,
            pageSize: options.pageSize,
          },
        })

        if (ctx.json) return printJson(page)
        if (page.items.length === 0) return print('No items match.')
        print(
          renderTable(page.items, [
            { header: 'KEY', value: (i) => i.key },
            { header: 'TYPE', value: (i) => i.type },
            { header: 'STATE', value: (i) => i.stateCategory },
            { header: 'PRIORITY', value: (i) => i.priority },
            { header: 'PTS', value: (i) => formatNumber(i.points), align: 'right' },
            { header: 'TITLE', value: (i) => i.title },
          ]),
        )
        print('')
        print(`Page ${page.page} of ${Math.max(1, page.totalPages)} · ${page.totalCount} items`)
      },
    )

  item
    .command('view')
    .description('Show one work item')
    .argument('<key>', 'Item key, e.g. ACME-123')
    .option('--comments', 'Include the comment thread')
    .option('--links', 'Include external links')
    .action(async (key: string, options: { comments?: boolean; links?: boolean }) => {
      const ctx = createContext(globals())
      const orgSlug = await ctx.org()
      const itemKey = normalizeItemKey(key)
      const found = await getItem(ctx, itemKey)
      const comments = options.comments ? await listComments(ctx, orgSlug, itemKey) : undefined
      const links = options.links ? await listLinks(ctx, orgSlug, itemKey) : undefined

      if (ctx.json) {
        return printJson({
          ...found,
          ...(comments ? { comments } : {}),
          ...(links ? { links } : {}),
        })
      }

      const state = await stateName(ctx, found)
      print(`${found.key}  ${found.title}`)
      print('')
      print(
        renderFields([
          ['Type', found.type],
          ['State', `${state} (${found.stateCategory})`],
          ['Priority', found.priority],
          ['Assignee', found.assigneeId ?? '(unassigned)'],
          ['Claimed by', found.claimedBy ?? undefined],
          ['Points', formatNumber(found.points)],
          ['Remaining hours', formatNumber(found.remainingHours)],
          ['Labels', found.labels.map((l) => l.name).join(', ')],
          ['Blocked', found.blocked ? 'yes' : ''],
          ['Updated', formatDate(found.updatedAt)],
          ['Version', String(found.version)],
        ]),
      )
      if (found.descriptionMarkdown.trim().length > 0) {
        print('')
        print(found.descriptionMarkdown.trim())
      }
      if (links && links.length > 0) {
        print('')
        print(
          renderTable(links, [
            { header: 'KIND', value: (l) => l.kind },
            { header: 'URL', value: (l) => l.url },
            { header: 'TITLE', value: (l) => l.title ?? '' },
          ]),
        )
      }
      if (comments) {
        print('')
        for (const comment of comments) {
          if (comment.deletedAt) continue
          print(
            `--- ${comment.author.displayName}${comment.author.isAgent ? ' [agent]' : ''} · ${formatDate(comment.createdAt)}`,
          )
          print(comment.bodyMarkdown.trim())
          print('')
        }
      }
    })

  item
    .command('create')
    .description('Create a work item')
    .requiredOption('-p, --project <key>', 'Project key, e.g. ACME')
    .addOption(new Option('-t, --type <type>', 'Item type').choices(types).default('task'))
    .requiredOption('--title <title>', 'Item title')
    .option('-m, --body <markdown>', 'Description in Markdown')
    .option('--body-file <path>', 'Read the description from a file, or - for stdin')
    .addOption(new Option('--priority <priority>', 'Priority').choices(priorities))
    .option('--parent <key>', 'Parent item key, e.g. ACME-100')
    .option('--team <key>', 'Team key or name')
    .option('--labels <names>', 'Comma-separated label names')
    .option('--points <n>', 'Story points')
    .option('--estimate <hours>', 'Original estimate in hours')
    .action(
      async (options: {
        project: string
        type: WorkItemType
        title: string
        body?: string
        bodyFile?: string
        priority?: WorkItemPriority
        parent?: string
        team?: string
        labels?: string
        points?: string
        estimate?: string
      }) => {
        const ctx = createContext(globals())
        const projectKey = options.project.toUpperCase()
        const description = await readBody(options.body, options.bodyFile)
        const parent = options.parent ? await getItem(ctx, options.parent) : undefined
        const teamId = options.team ? await resolveTeamId(ctx, projectKey, options.team) : undefined
        const labelIds = options.labels
          ? resolveLabelIds(options.labels, await getLabels(ctx, projectKey), [])
          : undefined

        const created = await ctx.client.request<
          WorkItemView,
          '/api/v1/orgs/{orgSlug}/projects/{projectKey}/items',
          'post'
        >('post', '/api/v1/orgs/{orgSlug}/projects/{projectKey}/items', {
          path: { orgSlug: await ctx.org(), projectKey },
          body: {
            type: options.type,
            title: options.title,
            ...(description === undefined ? {} : { descriptionMarkdown: description }),
            ...(options.priority ? { priority: options.priority } : {}),
            ...(parent ? { parentId: parent.id } : {}),
            ...(teamId ? { teamId } : {}),
            ...(labelIds ? { labelIds } : {}),
            ...(options.points ? { points: number(options.points, 'points') } : {}),
            ...(options.estimate ? { estimateHours: number(options.estimate, 'estimate') } : {}),
          },
        })

        if (ctx.json) return printJson(created)
        print(`${created.key}  ${created.title}`)
      },
    )

  item
    .command('update')
    .description('Update a work item')
    .argument('<key>', 'Item key, e.g. ACME-123')
    .option('--title <title>', 'New title')
    .option('-m, --body <markdown>', 'New description in Markdown')
    .option('--body-file <path>', 'Read the description from a file, or - for stdin')
    .addOption(new Option('--priority <priority>', 'Priority').choices(priorities))
    .addOption(new Option('--type <type>', 'Item type').choices(types))
    .option('--assignee <userId>', 'Assignee user id, or "none" to unassign')
    .option('--labels <names>', 'Label names: a plain list replaces, +name/-name adjusts')
    .option('--points <n>', 'Story points')
    .option('--estimate <hours>', 'Original estimate in hours')
    .option('--remaining <hours>', 'Remaining hours')
    .option('--version <n>', 'Expected version; read from the item when omitted')
    .action(
      async (
        key: string,
        options: {
          title?: string
          body?: string
          bodyFile?: string
          priority?: WorkItemPriority
          type?: WorkItemType
          assignee?: string
          labels?: string
          points?: string
          estimate?: string
          remaining?: string
          version?: string
        },
      ) => {
        const ctx = createContext(globals())
        const itemKey = normalizeItemKey(key)
        const current = await getItem(ctx, itemKey)
        const description = await readBody(options.body, options.bodyFile)
        const labelIds = options.labels
          ? resolveLabelIds(
              options.labels,
              await getLabels(ctx, projectKeyOf(itemKey)),
              current.labels,
            )
          : undefined

        const updated = await ctx.client.request<
          WorkItemView,
          '/api/v1/orgs/{orgSlug}/items/{itemKey}',
          'patch'
        >('patch', '/api/v1/orgs/{orgSlug}/items/{itemKey}', {
          path: { orgSlug: await ctx.org(), itemKey },
          body: {
            // PATCH is an absolute assignment for the nullable fields - an omitted
            // `parentId` unparents the item, an omitted `assigneeId` unassigns it. So the
            // body starts from what the item currently holds and the flags overwrite it;
            // otherwise `--priority high` would quietly detach a story from its feature.
            type: current.type,
            title: current.title,
            descriptionMarkdown: current.descriptionMarkdown,
            stateId: current.stateId,
            priority: current.priority,
            assigneeId: current.assigneeId,
            teamId: current.teamId,
            parentId: current.parentId,
            points: current.points,
            estimateHours: current.estimateHours,
            remainingHours: current.remainingHours,
            completedHours: current.completedHours,
            dueDate: current.dueDate,
            version: options.version ? number(options.version, 'version') : current.version,
            ...(options.title === undefined ? {} : { title: options.title }),
            ...(description === undefined ? {} : { descriptionMarkdown: description }),
            ...(options.priority ? { priority: options.priority } : {}),
            ...(options.type ? { type: options.type } : {}),
            ...(options.assignee === undefined
              ? {}
              : { assigneeId: options.assignee === 'none' ? null : options.assignee }),
            ...(labelIds ? { labelIds } : {}),
            ...(options.points ? { points: number(options.points, 'points') } : {}),
            ...(options.estimate ? { estimateHours: number(options.estimate, 'estimate') } : {}),
            ...(options.remaining
              ? { remainingHours: number(options.remaining, 'remaining') }
              : {}),
          },
        })

        if (ctx.json) return printJson(updated)
        print(`${updated.key} updated (version ${updated.version}).`)
      },
    )

  item
    .command('move')
    .description('Move an item: to another workflow state, sprint, parent or backlog rank')
    .argument('<key>', 'Item key, e.g. ACME-123')
    .option('--state <name>', 'Workflow state to transition to, e.g. "In Review"')
    .option('--sprint <sprintId>', 'Sprint id, or "none" to return it to the backlog')
    .option('--parent <key>', 'New parent item key')
    .option('--after <key>', 'Rank it directly after this item')
    .option('--before <key>', 'Rank it directly before this item')
    .option('--version <n>', 'Expected version; read from the item when omitted')
    .action(
      async (
        key: string,
        options: {
          state?: string
          sprint?: string
          parent?: string
          after?: string
          before?: string
          version?: string
        },
      ) => {
        const ctx = createContext(globals())
        const orgSlug = await ctx.org()
        const itemKey = normalizeItemKey(key)
        const current = await getItem(ctx, itemKey)
        const version = options.version ? number(options.version, 'version') : current.version
        const ranking =
          options.sprint !== undefined ||
          options.parent !== undefined ||
          options.after !== undefined ||
          options.before !== undefined

        if (!options.state && !ranking) {
          throw new CliError(
            'Pass --state, or one of --sprint/--parent/--after/--before.',
            ExitCode.Validation,
          )
        }

        let result: WorkItemView = current
        if (options.state) {
          const workflow = await getWorkflow(ctx, projectKeyOf(itemKey))
          const state = findState(workflow, options.state)
          result = await ctx.client.request<
            WorkItemView,
            '/api/v1/orgs/{orgSlug}/items/{itemKey}/transition',
            'post'
          >('post', '/api/v1/orgs/{orgSlug}/items/{itemKey}/transition', {
            path: { orgSlug, itemKey },
            body: { toStateId: state.id, version },
          })
        }
        if (ranking) {
          result = await ctx.client.request<
            WorkItemView,
            '/api/v1/orgs/{orgSlug}/items/{itemKey}/move',
            'post'
          >('post', '/api/v1/orgs/{orgSlug}/items/{itemKey}/move', {
            path: { orgSlug, itemKey },
            body: {
              // The transition above already spent the version it read.
              version: options.state ? result.version : version,
              ...(options.sprint === undefined
                ? {}
                : options.sprint === 'none'
                  ? { removeSprint: true }
                  : { sprintId: options.sprint }),
              ...(options.parent === undefined
                ? {}
                : { parentKey: normalizeItemKey(options.parent) }),
              ...(options.after === undefined ? {} : { afterKey: normalizeItemKey(options.after) }),
              ...(options.before === undefined
                ? {}
                : { beforeKey: normalizeItemKey(options.before) }),
            },
          })
        }

        if (ctx.json) return printJson(result)
        print(`${result.key} moved (version ${result.version}).`)
      },
    )

  // Claim is the compare-and-swap: it needs the version it expects to find, so that two
  // agents racing for the same item produce one claim and one 409 (exit code 3). Release
  // and heartbeat act on the claim that already exists and carry no version.
  item
    .command('claim')
    .description('Claim an item for the authenticated identity (compare-and-swap)')
    .argument('<key>', 'Item key, e.g. ACME-123')
    .option('--version <n>', 'Expected version; read from the item when omitted')
    .action(async (key: string, options: { version?: string }) => {
      const ctx = createContext(globals())
      const itemKey = normalizeItemKey(key)
      const version = options.version
        ? number(options.version, 'version')
        : (await getItem(ctx, itemKey)).version
      const result = await ctx.client.request<
        WorkItemView,
        '/api/v1/orgs/{orgSlug}/items/{itemKey}/claim',
        'post'
      >('post', '/api/v1/orgs/{orgSlug}/items/{itemKey}/claim', {
        path: { orgSlug: await ctx.org(), itemKey },
        body: { version },
      })
      if (ctx.json) return printJson(result)
      print(`${itemKey} claimed.`)
    })

  item
    .command('release')
    .description('Release a claim you hold')
    .argument('<key>', 'Item key, e.g. ACME-123')
    .action(async (key: string) => {
      const ctx = createContext(globals())
      const itemKey = normalizeItemKey(key)
      // Release and heartbeat answer 204: they act on a claim that already exists and
      // have nothing to return, so --json reports what happened rather than an empty body.
      await ctx.client.request<void, '/api/v1/orgs/{orgSlug}/items/{itemKey}/release', 'post'>(
        'post',
        '/api/v1/orgs/{orgSlug}/items/{itemKey}/release',
        { path: { orgSlug: await ctx.org(), itemKey } },
      )
      if (ctx.json) return printJson({ key: itemKey, released: true })
      print(`${itemKey} released.`)
    })

  item
    .command('heartbeat')
    .description('Refresh the claim heartbeat so the claim does not go stale')
    .argument('<key>', 'Item key, e.g. ACME-123')
    .action(async (key: string) => {
      const ctx = createContext(globals())
      const itemKey = normalizeItemKey(key)
      await ctx.client.request<void, '/api/v1/orgs/{orgSlug}/items/{itemKey}/heartbeat', 'post'>(
        'post',
        '/api/v1/orgs/{orgSlug}/items/{itemKey}/heartbeat',
        { path: { orgSlug: await ctx.org(), itemKey } },
      )
      if (ctx.json) return printJson({ key: itemKey, heartbeat: new Date().toISOString() })
      print(`${itemKey} heartbeat sent.`)
    })

  item
    .command('comment')
    .description('Add a comment to an item')
    .argument('<key>', 'Item key, e.g. ACME-123')
    .option('-m, --body <markdown>', 'Comment body in Markdown')
    .option('--body-file <path>', 'Read the body from a file, or - for stdin')
    .action(async (key: string, options: { body?: string; bodyFile?: string }) => {
      const ctx = createContext(globals())
      const body = await readBody(options.body, options.bodyFile)
      if (!body || body.trim().length === 0) {
        throw new CliError('Pass -m "text" or --body-file <path>.', ExitCode.Validation)
      }
      const comment = await ctx.client.request<
        CommentView,
        '/api/v1/orgs/{orgSlug}/items/{itemKey}/comments',
        'post'
      >('post', '/api/v1/orgs/{orgSlug}/items/{itemKey}/comments', {
        path: { orgSlug: await ctx.org(), itemKey: normalizeItemKey(key) },
        body: { bodyMarkdown: body },
      })

      if (ctx.json) return printJson(comment)
      print(`Comment added to ${normalizeItemKey(key)}.`)
    })

  item
    .command('subtask')
    .description('Create a child item under an existing one')
    .argument('<key>', 'Parent item key, e.g. ACME-123')
    .requiredOption('--title <title>', 'Subtask title')
    .addOption(new Option('-t, --type <type>', 'Item type').choices(types).default('task'))
    .option('-m, --body <markdown>', 'Description in Markdown')
    .option('--body-file <path>', 'Read the description from a file, or - for stdin')
    .option('--estimate <hours>', 'Original estimate in hours')
    .action(
      async (
        key: string,
        options: {
          title: string
          type: WorkItemType
          body?: string
          bodyFile?: string
          estimate?: string
        },
      ) => {
        const ctx = createContext(globals())
        const parentKey = normalizeItemKey(key)
        const parent = await getItem(ctx, parentKey)
        const description = await readBody(options.body, options.bodyFile)

        const created = await ctx.client.request<
          WorkItemView,
          '/api/v1/orgs/{orgSlug}/projects/{projectKey}/items',
          'post'
        >('post', '/api/v1/orgs/{orgSlug}/projects/{projectKey}/items', {
          path: { orgSlug: await ctx.org(), projectKey: projectKeyOf(parentKey) },
          body: {
            type: options.type,
            title: options.title,
            parentId: parent.id,
            ...(parent.teamId ? { teamId: parent.teamId } : {}),
            ...(description === undefined ? {} : { descriptionMarkdown: description }),
            ...(options.estimate ? { estimateHours: number(options.estimate, 'estimate') } : {}),
          },
        })

        if (ctx.json) return printJson(created)
        print(`${created.key}  ${created.title}`)
      },
    )

  item
    .command('link')
    .description('Attach an external link to an item')
    .argument('<key>', 'Item key, e.g. ACME-123')
    .option('--url <url>', 'Any public URL')
    .option('--pr <url>', 'Pull request URL (an alias for --url)')
    .action(async (key: string, options: { url?: string; pr?: string }) => {
      const ctx = createContext(globals())
      const url = options.url ?? options.pr
      if (!url) throw new CliError('Pass --url <url> or --pr <url>.', ExitCode.Validation)
      const link = await ctx.client.request<
        ItemLinkView,
        '/api/v1/orgs/{orgSlug}/items/{itemKey}/links',
        'post'
      >('post', '/api/v1/orgs/{orgSlug}/items/{itemKey}/links', {
        path: { orgSlug: await ctx.org(), itemKey: normalizeItemKey(key) },
        body: { url },
      })

      if (ctx.json) return printJson(link)
      print(`Linked ${link.url} to ${normalizeItemKey(key)}.`)
    })

  item
    .command('branch')
    .description('Print the branch name convention for an item')
    .argument('<key>', 'Item key, e.g. ACME-123')
    .action(async (key: string) => {
      const ctx = createContext(globals())
      const found = await getItem(ctx, key)
      const branch = branchName(found)
      if (ctx.json) return printJson({ key: found.key, branch })
      print(branch)
    })

  return item
}

async function listComments(ctx: Context, orgSlug: string, itemKey: string) {
  const page = await ctx.client.request<
    PagedResult<CommentView>,
    '/api/v1/orgs/{orgSlug}/items/{itemKey}/comments',
    'get'
  >('get', '/api/v1/orgs/{orgSlug}/items/{itemKey}/comments', {
    path: { orgSlug, itemKey },
    query: { pageSize: 100 },
  })
  return page.items
}

async function listLinks(ctx: Context, orgSlug: string, itemKey: string) {
  return ctx.client.request<ItemLinkView[], '/api/v1/orgs/{orgSlug}/items/{itemKey}/links', 'get'>(
    'get',
    '/api/v1/orgs/{orgSlug}/items/{itemKey}/links',
    { path: { orgSlug, itemKey } },
  )
}

async function stateName(ctx: Context, item: WorkItemView): Promise<string> {
  const workflow = await getWorkflow(ctx, projectKeyOf(item.key))
  return workflow.states.find((s) => s.id === item.stateId)?.name ?? item.stateCategory
}

async function resolveTeamId(ctx: Context, projectKey: string, wanted: string): Promise<string> {
  const teams = await getTeams(ctx, projectKey)
  const needle = wanted.trim().toLowerCase()
  const team = teams.find(
    (t) => t.key.toLowerCase() === needle || t.name.toLowerCase() === needle || t.id === wanted,
  )
  if (!team) {
    throw new CliError(
      `No team "${wanted}" in ${projectKey}. Teams: ${teams.map((t) => t.key).join(', ') || '(none)'}.`,
      ExitCode.Validation,
    )
  }
  return team.id
}

function number(raw: string, field: string): number {
  const parsed = Number(raw)
  if (!Number.isFinite(parsed)) {
    throw new CliError(`--${field} must be a number, got "${raw}".`, ExitCode.Validation)
  }
  return parsed
}
