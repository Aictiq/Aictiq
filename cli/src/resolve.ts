import { readFileSync } from 'node:fs'
import type { Context } from './context.js'
import { CliError, ExitCode } from './errors.js'
import type {
  ItemLabelView,
  TeamView,
  WorkflowStateView,
  WorkflowView,
  WorkItemView,
} from './api/views.js'

/** `ACME-123` → `ACME`. Project keys are `^[A-Z][A-Z0-9]{1,9}$`, so the last dash splits. */
export function projectKeyOf(itemKey: string): string {
  const normalized = itemKey.trim().toUpperCase()
  const dash = normalized.lastIndexOf('-')
  if (dash <= 0) {
    throw new CliError(
      `"${itemKey}" is not a work item key (expected e.g. ACME-123).`,
      ExitCode.Validation,
    )
  }
  return normalized.slice(0, dash)
}

export function normalizeItemKey(itemKey: string): string {
  return itemKey.trim().toUpperCase()
}

export async function getWorkflow(ctx: Context, projectKey: string): Promise<WorkflowView> {
  const workflows = await ctx.client.request<
    WorkflowView[],
    '/api/v1/orgs/{orgSlug}/projects/{projectKey}/workflows',
    'get'
  >('get', '/api/v1/orgs/{orgSlug}/projects/{projectKey}/workflows', {
    path: { orgSlug: await ctx.org(), projectKey },
  })
  const workflow = workflows.find((w) => w.isDefault) ?? workflows[0]
  if (!workflow) throw new CliError(`Project ${projectKey} has no workflow.`, ExitCode.NotFound)
  return workflow
}

/** Matches a state by name, case-insensitively; the error lists what was available. */
export function findState(workflow: WorkflowView, name: string): WorkflowStateView {
  const wanted = name.trim().toLowerCase()
  const state = workflow.states.find((s) => s.name.toLowerCase() === wanted)
  if (!state) {
    const names = workflow.states.map((s) => s.name).join(', ')
    throw new CliError(`No workflow state named "${name}". States: ${names}.`, ExitCode.Validation)
  }
  return state
}

export async function getLabels(ctx: Context, projectKey: string): Promise<ItemLabelView[]> {
  return ctx.client.request<
    ItemLabelView[],
    '/api/v1/orgs/{orgSlug}/projects/{projectKey}/labels',
    'get'
  >('get', '/api/v1/orgs/{orgSlug}/projects/{projectKey}/labels', {
    path: { orgSlug: await ctx.org(), projectKey },
  })
}

export async function getTeams(ctx: Context, projectKey: string): Promise<TeamView[]> {
  return ctx.client.request<
    TeamView[],
    '/api/v1/orgs/{orgSlug}/projects/{projectKey}/teams',
    'get'
  >('get', '/api/v1/orgs/{orgSlug}/projects/{projectKey}/teams', {
    path: { orgSlug: await ctx.org(), projectKey },
  })
}

export async function getItem(ctx: Context, itemKey: string): Promise<WorkItemView> {
  return ctx.client.request<WorkItemView, '/api/v1/orgs/{orgSlug}/items/{itemKey}', 'get'>(
    'get',
    '/api/v1/orgs/{orgSlug}/items/{itemKey}',
    { path: { orgSlug: await ctx.org(), itemKey: normalizeItemKey(itemKey) } },
  )
}

/**
 * `--labels backend,+urgent,-stale`: a bare list replaces, `+`/`-` adjust. Mixing the two
 * is refused rather than guessed at, because "did that clear the others?" is not a
 * question a script should have to answer by reading the result back.
 */
export function resolveLabelIds(
  spec: string,
  available: readonly ItemLabelView[],
  current: readonly ItemLabelView[],
): string[] {
  const tokens = spec
    .split(',')
    .map((t) => t.trim())
    .filter((t) => t.length > 0)
  const adjusting = tokens.some((t) => t.startsWith('+') || t.startsWith('-'))
  const replacing = tokens.some((t) => !t.startsWith('+') && !t.startsWith('-'))
  if (adjusting && replacing) {
    throw new CliError(
      'Use either a plain label list (replaces all) or +/- adjustments, not both.',
      ExitCode.Validation,
    )
  }

  const byName = new Map(available.map((label) => [label.name.toLowerCase(), label]))
  const lookup = (name: string) => {
    const label = byName.get(name.toLowerCase())
    if (!label) {
      throw new CliError(
        `No label "${name}" in this project. Labels: ${available.map((l) => l.name).join(', ') || '(none)'}.`,
        ExitCode.Validation,
      )
    }
    return label.id
  }

  if (!adjusting) return tokens.map(lookup)

  const ids = new Set(current.map((label) => label.id))
  for (const token of tokens) {
    const id = lookup(token.slice(1))
    if (token.startsWith('+')) ids.add(id)
    else ids.delete(id)
  }
  return [...ids]
}

/** `-m "text"` or `--body-file path` (`-` reads stdin). */
export async function readBody(
  message: string | undefined,
  file: string | undefined,
): Promise<string | undefined> {
  if (message !== undefined && file !== undefined) {
    throw new CliError('Pass either -m or --body-file, not both.', ExitCode.Validation)
  }
  if (message !== undefined) return message
  if (file === undefined) return undefined
  if (file === '-') {
    const chunks: Buffer[] = []
    for await (const chunk of process.stdin) chunks.push(Buffer.from(chunk))
    return Buffer.concat(chunks).toString('utf8')
  }
  try {
    return readFileSync(file, 'utf8')
  } catch (cause) {
    throw new CliError(
      `Cannot read ${file}: ${cause instanceof Error ? cause.message : String(cause)}`,
      ExitCode.Validation,
    )
  }
}

/**
 * `ACME-123` + "Fix the login redirect" → `acme-123-fix-the-login-redirect`.
 * The key stays first so a branch name is enough for commit linking to attach
 * the work back to the item.
 */
export function branchName(item: Pick<WorkItemView, 'key' | 'title'>, maxWords = 6): string {
  const slug = item.title
    .toLowerCase()
    .normalize('NFKD')
    .replace(/[^\da-z]+/g, ' ')
    .trim()
    .split(/\s+/)
    .filter((word) => word.length > 0)
    .slice(0, maxWords)
    .join('-')
  return slug ? `${item.key.toLowerCase()}-${slug}` : item.key.toLowerCase()
}
