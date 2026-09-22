import { Command } from 'commander'
import type { WikiPageView, WikiTreePageView } from '../api/views.js'
import { createContext } from '../context.js'
import type { Context, GlobalOptions } from '../context.js'
import { CliError, ExitCode } from '../errors.js'
import { print, printJson, renderTable } from '../output.js'
import { readBody } from '../resolve.js'

export function wikiCommand(globals: () => GlobalOptions): Command {
  const wiki = new Command('wiki').description('Project wiki pages')

  wiki
    .command('tree')
    .description('List the page tree of a project')
    .requiredOption('-p, --project <key>', 'Project key, e.g. ACME')
    .action(async (options: { project: string }) => {
      const ctx = createContext(globals())
      const pages = await fetchTree(ctx, options.project.toUpperCase())
      if (ctx.json) return printJson(pages)
      if (pages.length === 0) return print('No wiki pages.')
      const paths = pathsOf(pages)
      print(
        renderTable(pages, [
          { header: 'PATH', value: (p) => paths.get(p.id) ?? p.slug },
          { header: 'TITLE', value: (p) => p.title },
        ]),
      )
    })

  wiki
    .command('get')
    .description('Print a wiki page as Markdown')
    .argument('<project>', 'Project key, e.g. ACME')
    .argument('<path>', 'Slug path, e.g. specs/login')
    .action(async (project: string, path: string) => {
      const ctx = createContext(globals())
      const pages = await fetchTree(ctx, project.toUpperCase())
      const page = findByPath(pages, path)
      if (!page) throw new CliError(`No wiki page at "${path}" in ${project}.`, ExitCode.NotFound)
      const full = await ctx.client.request<
        WikiPageView,
        '/api/v1/orgs/{orgSlug}/wiki/pages/{pageId}',
        'get'
      >('get', '/api/v1/orgs/{orgSlug}/wiki/pages/{pageId}', {
        path: { orgSlug: await ctx.org(), pageId: page.id },
      })
      if (ctx.json) return printJson(full)
      print(full.contentMarkdown)
    })

  wiki
    .command('put')
    .description('Create or replace a wiki page from Markdown')
    .argument('<project>', 'Project key, e.g. ACME')
    .argument('<path>', 'Slug path, e.g. specs/login')
    .option('--file <path>', 'Markdown file, or - for stdin', '-')
    .option('-m, --body <markdown>', 'Markdown body given inline')
    .option('--title <title>', 'Page title; defaults to the last path segment')
    .option('--summary <text>', 'Revision summary recorded in the history')
    .action(
      async (
        project: string,
        path: string,
        options: { file?: string; body?: string; title?: string; summary?: string },
      ) => {
        const ctx = createContext(globals())
        const orgSlug = await ctx.org()
        const projectKey = project.toUpperCase()
        const content = await readBody(
          options.body,
          options.body ? undefined : (options.file ?? '-'),
        )
        if (content === undefined || content.trim().length === 0) {
          throw new CliError('The page body is empty.', ExitCode.Validation)
        }

        const pages = await fetchTree(ctx, projectKey)
        const existing = findByPath(pages, path)
        if (existing) {
          // The version comes from the page we are about to replace, so a concurrent edit
          // 409s (exit 3) instead of silently winning.
          const current = await ctx.client.request<
            WikiPageView,
            '/api/v1/orgs/{orgSlug}/wiki/pages/{pageId}',
            'get'
          >('get', '/api/v1/orgs/{orgSlug}/wiki/pages/{pageId}', {
            path: { orgSlug, pageId: existing.id },
          })
          const updated = await ctx.client.request<
            WikiPageView,
            '/api/v1/orgs/{orgSlug}/wiki/pages/{pageId}',
            'patch'
          >('patch', '/api/v1/orgs/{orgSlug}/wiki/pages/{pageId}', {
            path: { orgSlug, pageId: existing.id },
            body: {
              contentMd: content,
              version: current.version,
              ...(options.title ? { title: options.title } : {}),
              ...(options.summary ? { summary: options.summary } : {}),
            },
          })
          if (ctx.json) return printJson(updated)
          return print(`Updated ${path} (revision ${updated.revisionNumber}).`)
        }

        const segments = path.split('/').filter((s) => s.length > 0)
        const parentPath = segments.slice(0, -1).join('/')
        const parent = parentPath ? findByPath(pages, parentPath) : undefined
        if (parentPath && !parent) {
          throw new CliError(
            `The parent page "${parentPath}" does not exist; create it first.`,
            ExitCode.NotFound,
          )
        }
        const created = await ctx.client.request<
          WikiPageView,
          '/api/v1/orgs/{orgSlug}/projects/{projectKey}/wiki/pages',
          'post'
        >('post', '/api/v1/orgs/{orgSlug}/projects/{projectKey}/wiki/pages', {
          path: { orgSlug, projectKey },
          body: {
            title: options.title ?? titleFrom(segments.at(-1) ?? path),
            contentMd: content,
            ...(parent ? { parentId: parent.id } : {}),
          },
        })
        if (ctx.json) return printJson(created)
        // The API derives the slug from the title, so the page may not sit at the path
        // that was asked for. Saying where it landed beats a silent surprise next `get`.
        print(`Created ${created.slug} (${created.title}).`)
      },
    )

  return wiki
}

async function fetchTree(ctx: Context, projectKey: string): Promise<WikiTreePageView[]> {
  return ctx.client.request<
    WikiTreePageView[],
    '/api/v1/orgs/{orgSlug}/projects/{projectKey}/wiki/tree',
    'get'
  >('get', '/api/v1/orgs/{orgSlug}/projects/{projectKey}/wiki/tree', {
    path: { orgSlug: await ctx.org(), projectKey },
  })
}

/** Slugs are unique per parent, so a page is addressed by the path of slugs to it. */
function pathsOf(pages: readonly WikiTreePageView[]): Map<string, string> {
  const byId = new Map(pages.map((page) => [page.id, page]))
  const paths = new Map<string, string>()
  const walk = (page: WikiTreePageView): string => {
    const cached = paths.get(page.id)
    if (cached) return cached
    const parent = page.parentId ? byId.get(page.parentId) : undefined
    const path = parent ? `${walk(parent)}/${page.slug}` : page.slug
    paths.set(page.id, path)
    return path
  }
  for (const page of pages) walk(page)
  return paths
}

function findByPath(
  pages: readonly WikiTreePageView[],
  path: string,
): WikiTreePageView | undefined {
  const wanted = path
    .split('/')
    .filter((s) => s.length > 0)
    .join('/')
    .toLowerCase()
  const paths = pathsOf(pages)
  return pages.find((page) => paths.get(page.id)?.toLowerCase() === wanted)
}

function titleFrom(slug: string): string {
  const words = slug.replace(/[-_]+/g, ' ').trim()
  return words.charAt(0).toUpperCase() + words.slice(1)
}
