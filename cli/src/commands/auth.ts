import { Command } from 'commander'
import { AictiqClient } from '../api/client.js'
import type { OrganizationSummary, SessionResponse } from '../api/views.js'
import { clearConfig, configPath, readConfig, writeConfig } from '../config.js'
import { resolveSettings } from '../context.js'
import type { GlobalOptions } from '../context.js'
import { CliError, ExitCode } from '../errors.js'
import { print, printJson, renderFields, renderTable } from '../output.js'
import { promptSecret } from '../prompt.js'

export function authCommand(globals: () => GlobalOptions): Command {
  const auth = new Command('auth').description(
    'Sign in to a Aictiq instance and inspect the session',
  )

  auth
    .command('login')
    .description('Store a personal access token for an instance')
    .option('--url <url>', 'Base URL of the Aictiq instance')
    .option('--token <token>', 'Personal access token (aiq_…); read from stdin when omitted')
    .option('--org <slug>', 'Default organization slug for scoped commands')
    .action(async (options: { url?: string; token?: string; org?: string }) => {
      const global = globals()
      const url = options.url ?? global.url ?? process.env.AICTIQ_URL ?? readConfig().url
      if (!url)
        throw new CliError(
          'Pass --url with the address of your Aictiq instance.',
          ExitCode.Validation,
        )

      const token =
        options.token ??
        global.token ??
        process.env.AICTIQ_TOKEN ??
        (await promptSecret('Personal access token: '))
      if (!token) throw new CliError('No token supplied.', ExitCode.Validation)

      // Verify before writing: a stored token that never worked is worse than no token,
      // because every later command fails somewhere far from the cause.
      const client = new AictiqClient({ baseUrl: url, token })
      const session = await client.request<SessionResponse, '/api/v1/auth/session', 'get'>(
        'get',
        '/api/v1/auth/session',
      )
      const orgs = await client.request<OrganizationSummary[], '/api/v1/orgs', 'get'>(
        'get',
        '/api/v1/orgs',
      )
      const org = options.org ?? global.org ?? (orgs.length === 1 ? orgs[0]!.slug : undefined)

      writeConfig({ url: url.replace(/\/+$/, ''), token, ...(org ? { org } : {}) })

      if (global.json) {
        printJson({
          url,
          org: org ?? null,
          user: session,
          organizations: orgs,
          configPath: configPath(),
        })
        return
      }
      print(`Signed in to ${url} as ${session.email}.`)
      if (org) print(`Default organization: ${org}`)
      else if (orgs.length > 1)
        print(`Several organizations are visible; pass --org next time or set AICTIQ_ORG.`)
      print(`Token stored in ${configPath()} (0600).`)
    })

  auth
    .command('status')
    .description('Show the configured instance, identity and organizations')
    .action(async () => {
      const global = globals()
      const settings = resolveSettings(global)
      if (!settings.url || !settings.token) {
        if (global.json) {
          printJson({ authenticated: false, url: settings.url ?? null, configPath: configPath() })
          return
        }
        throw new CliError('Not signed in. Run `aictiq auth login --url <url>`.', ExitCode.Auth)
      }

      const client = new AictiqClient({ baseUrl: settings.url, token: settings.token })
      const session = await client.request<SessionResponse, '/api/v1/auth/session', 'get'>(
        'get',
        '/api/v1/auth/session',
      )
      const orgs = await client.request<OrganizationSummary[], '/api/v1/orgs', 'get'>(
        'get',
        '/api/v1/orgs',
      )

      if (global.json) {
        printJson({
          authenticated: true,
          url: settings.url,
          org: settings.org ?? null,
          user: session,
          organizations: orgs,
          configPath: configPath(),
        })
        return
      }
      print(
        renderFields([
          ['URL', settings.url],
          ['User', `${session.email} (${session.id})`],
          ['Organization', settings.org ?? '(not set)'],
          ['Config', configPath()],
        ]),
      )
      if (orgs.length > 0) {
        print('')
        print(
          renderTable(orgs, [
            { header: 'SLUG', value: (o) => o.slug },
            { header: 'NAME', value: (o) => o.name },
            { header: 'ROLE', value: (o) => o.role },
          ]),
        )
      }
    })

  auth
    .command('logout')
    .description('Delete the stored token')
    .action(() => {
      clearConfig()
      if (globals().json) printJson({ removed: configPath() })
      else print(`Removed ${configPath()}.`)
    })

  return auth
}
