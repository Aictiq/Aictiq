import { AictiqClient } from './api/client.js'
import type { OrganizationSummary } from './api/views.js'
import { readConfig } from './config.js'
import type { StoredConfig } from './config.js'
import { CliError, ExitCode } from './errors.js'

export interface GlobalOptions {
  url?: string
  token?: string
  org?: string
  json?: boolean
}

/**
 * Flags beat environment beats config file — the precedence in 03-API-MCP-CLI.md §4.
 * CI sets `AICTIQ_URL`/`AICTIQ_TOKEN` and never writes a config file; a person logs in
 * once and passes neither.
 */
export function resolveSettings(
  options: GlobalOptions,
  env: NodeJS.ProcessEnv = process.env,
  stored: StoredConfig = readConfig(),
): StoredConfig {
  return {
    url: options.url ?? env.AICTIQ_URL ?? stored.url,
    token: options.token ?? env.AICTIQ_TOKEN ?? stored.token,
    org: options.org ?? env.AICTIQ_ORG ?? stored.org,
  }
}

export interface Context {
  client: AictiqClient
  /** The organization slug every scoped route needs; resolved lazily. */
  org(): Promise<string>
  json: boolean
}

export function createContext(options: GlobalOptions): Context {
  const settings = resolveSettings(options)
  if (!settings.url) {
    throw new CliError('No Aictiq URL. Run `aictiq auth login --url <url>`.', ExitCode.Auth)
  }
  if (!settings.token) {
    throw new CliError('No personal access token. Run `aictiq auth login`.', ExitCode.Auth)
  }

  const client = new AictiqClient({ baseUrl: settings.url, token: settings.token })
  let resolved: Promise<string> | undefined

  return {
    client,
    json: options.json === true,
    org() {
      if (settings.org) return Promise.resolve(settings.org)
      // A token bound to one organization has exactly one answer here, which is why the
      // CLI can be run with no org configured at all. Anything else has to be told which.
      resolved ??= resolveOrg(client)
      return resolved
    },
  }
}

async function resolveOrg(client: AictiqClient): Promise<string> {
  const orgs = await client.request<OrganizationSummary[], '/api/v1/orgs', 'get'>(
    'get',
    '/api/v1/orgs',
  )
  if (orgs.length === 0) {
    throw new CliError('This account is not a member of any organization.', ExitCode.NotFound)
  }
  if (orgs.length > 1) {
    const slugs = orgs.map((o) => o.slug).join(', ')
    throw new CliError(
      `This token can see several organizations (${slugs}). Pass --org, set AICTIQ_ORG, or run \`aictiq auth login --org <slug>\`.`,
      ExitCode.Validation,
    )
  }
  return orgs[0]!.slug
}
