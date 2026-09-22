import { Command } from 'commander'
import type { ProjectView } from '../api/views.js'
import { createContext } from '../context.js'
import type { GlobalOptions } from '../context.js'
import { print, printJson, renderTable } from '../output.js'

export function projectCommand(globals: () => GlobalOptions): Command {
  const project = new Command('project').description('Projects in the organization')

  project
    .command('list')
    .description('List the projects you can see')
    .option('--archived', 'Include archived projects')
    .action(async (options: { archived?: boolean }) => {
      const ctx = createContext(globals())
      const projects = await ctx.client.request<
        ProjectView[],
        '/api/v1/orgs/{orgSlug}/projects',
        'get'
      >('get', '/api/v1/orgs/{orgSlug}/projects', {
        path: { orgSlug: await ctx.org() },
        query: { includeArchived: options.archived === true },
      })

      if (ctx.json) return printJson(projects)
      if (projects.length === 0) return print('No projects.')
      print(
        renderTable(projects, [
          { header: 'KEY', value: (p) => p.key },
          { header: 'NAME', value: (p) => p.name },
          { header: 'ROLE', value: (p) => p.role },
          { header: 'VISIBILITY', value: (p) => p.visibility },
          { header: 'STATUS', value: (p) => (p.isArchived ? 'archived' : 'active') },
        ]),
      )
    })

  return project
}
