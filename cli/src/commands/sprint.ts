import { Command } from 'commander'
import type { SprintView, TeamView } from '../api/views.js'
import { createContext } from '../context.js'
import type { Context, GlobalOptions } from '../context.js'
import { CliError, ExitCode } from '../errors.js'
import { formatNumber, print, printJson, renderFields, renderTable } from '../output.js'
import { getTeams } from '../resolve.js'

interface TaskboardCell {
  key: string
  name: string
  remainingHours: number
  tasks: { key: string; title: string; assigneeId: string | null }[]
}

interface TaskboardView {
  sprintId: string
  rows: { parentKey: string | null; name: string; remainingHours: number; cells: TaskboardCell[] }[]
  unparentedTasks: TaskboardCell[]
}

export function sprintCommand(globals: () => GlobalOptions): Command {
  const sprint = new Command('sprint').description('Sprints')

  sprint
    .command('list')
    .description("List a team's sprints, newest first")
    .requiredOption('-t, --team <key>', 'Team key, name or id')
    .option('-p, --project <key>', 'Project key; required when the team is named, not an id')
    .action(async (options: { team: string; project?: string }) => {
      const ctx = createContext(globals())
      const teamId = await resolveTeamId(ctx, options.team, options.project)
      const sprints = await ctx.client.request<
        SprintView[],
        '/api/v1/orgs/{orgSlug}/teams/{teamId}/sprints',
        'get'
      >('get', '/api/v1/orgs/{orgSlug}/teams/{teamId}/sprints', {
        path: { orgSlug: await ctx.org(), teamId },
      })

      if (ctx.json) return printJson(sprints)
      if (sprints.length === 0) return print('No sprints.')
      print(
        renderTable(sprints, [
          { header: 'ID', value: (s) => s.id },
          { header: 'NAME', value: (s) => s.name },
          { header: 'STATE', value: (s) => s.state },
          { header: 'STARTS', value: (s) => s.startsOn },
          { header: 'ENDS', value: (s) => s.endsOn },
          {
            header: 'DONE',
            value: (s) => `${s.progress.completedItems}/${s.progress.totalItems}`,
            align: 'right',
          },
        ]),
      )
    })

  sprint
    .command('view')
    .description('Show a sprint and its taskboard')
    .argument('<sprintId>', 'Sprint id')
    .option('--taskboard', 'Include the taskboard rows')
    .action(async (sprintId: string, options: { taskboard?: boolean }) => {
      const ctx = createContext(globals())
      const orgSlug = await ctx.org()
      const found = await ctx.client.request<
        SprintView,
        '/api/v1/orgs/{orgSlug}/sprints/{sprintId}',
        'get'
      >('get', '/api/v1/orgs/{orgSlug}/sprints/{sprintId}', { path: { orgSlug, sprintId } })
      const taskboard = options.taskboard
        ? await ctx.client.request<
            TaskboardView,
            '/api/v1/orgs/{orgSlug}/sprints/{sprintId}/taskboard',
            'get'
          >('get', '/api/v1/orgs/{orgSlug}/sprints/{sprintId}/taskboard', {
            path: { orgSlug, sprintId },
          })
        : undefined

      if (ctx.json) return printJson({ ...found, ...(taskboard ? { taskboard } : {}) })

      print(
        renderFields([
          ['Sprint', found.name],
          ['Goal', found.goal],
          ['State', found.state],
          ['Dates', `${found.startsOn} → ${found.endsOn}`],
          ['Days left', String(found.daysLeft)],
          ['Items', `${found.progress.completedItems}/${found.progress.totalItems}`],
          [
            'Points',
            `${formatNumber(found.progress.pointsDone)}/${formatNumber(found.progress.pointsTotal)}`,
          ],
          ['Remaining hours', formatNumber(found.progress.remainingHours)],
        ]),
      )
      if (taskboard) {
        for (const row of taskboard.rows) {
          print('')
          print(`${row.parentKey ?? '(no parent)'}  ${row.name}`)
          for (const cell of row.cells) {
            for (const task of cell.tasks) {
              print(`  [${cell.name}] ${task.key}  ${task.title}`)
            }
          }
        }
      }
    })

  return sprint
}

/** A team id passes through; a key or name needs the project whose teams to search. */
async function resolveTeamId(ctx: Context, team: string, projectKey?: string): Promise<string> {
  if (/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(team)) return team
  if (!projectKey) {
    throw new CliError(
      'Pass -p <projectKey> alongside a team key or name, or give the team id.',
      ExitCode.Validation,
    )
  }
  const teams: TeamView[] = await getTeams(ctx, projectKey.toUpperCase())
  const needle = team.trim().toLowerCase()
  const found = teams.find((t) => t.key.toLowerCase() === needle || t.name.toLowerCase() === needle)
  if (!found) {
    throw new CliError(
      `No team "${team}" in ${projectKey}. Teams: ${teams.map((t) => t.key).join(', ') || '(none)'}.`,
      ExitCode.Validation,
    )
  }
  return found.id
}
