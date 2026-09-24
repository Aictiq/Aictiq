import { Command } from 'commander'
import { randomUUID } from 'node:crypto'
import { existsSync } from 'node:fs'
import { arch, homedir, platform } from 'node:os'
import { resolve } from 'node:path'
import type { GlobalOptions } from '../context.js'
import { CliError, ExitCode } from '../errors.js'
import { print, printJson, renderFields, renderTable } from '../output.js'
import { promptSecret } from '../prompt.js'
import { version } from '../version.js'
import { RunnerClient, RunnerHttpError } from '../runner/client.js'
import {
  profileKey,
  profileLabel,
  readRunnerConfig,
  runnerConfigPath,
  sameInstance,
  writeRunnerConfig,
} from '../runner/config.js'
import type { RunnerConfig, RunnerProfile } from '../runner/config.js'
import { executeRun } from '../runner/execute.js'
import { harnesses, probeHarnesses } from '../runner/harness/index.js'
import { serviceDefinition, servicePlatform, type ServicePlatform } from '../runner/service.js'
import { RunnerLoop, RunnerRevokedError } from '../runner/loop.js'
import { RunnerSupervisor } from '../runner/supervisor.js'
import type { RunnerCapabilities } from '../runner/types.js'
import { DefaultAttachmentMaxBytes, DefaultAttachmentMaxCount, defaultWorkspaceRoot } from '../runner/workspace.js'

/**
 * `aictiq runner` - the worker on the factory floor. It authenticates with a
 * `jrn_` runner secret kept in its own `runner.json`, never with the person's PAT.
 */
export function runnerCommand(globals: () => GlobalOptions): Command {
  const runner = new Command('runner').description(
    'Run agent work dispatched from a Aictiq instance on this machine',
  )

  runner
    .command('register')
    .description(
      'Store a runner secret (jrn_…) from Factory → Runners and check it. A secret from another organization adds it to this machine',
    )
    // Not requiredOption: the root command defines --url and --token too, and commander hands
    // them to the root, so they are read from both places exactly as `auth login` does.
    .option('--url <url>', 'Base URL of the Aictiq instance')
    .option('--token <token>', 'Runner secret; read from stdin when omitted')
    .option('--name <name>', 'A local label for this machine (each instance keeps its own name)')
    .action(async (options: { url?: string; token?: string; name?: string }) => {
      const global = globals()
      const rawUrl = options.url ?? global.url
      if (!rawUrl) {
        throw new CliError(
          'Pass --url with the address of your Aictiq instance.',
          ExitCode.Validation,
        )
      }
      const token =
        options.token ??
        global.token ??
        process.env.AICTIQ_RUNNER_TOKEN ??
        (await promptSecret('Runner secret: '))
      if (!token?.startsWith('jrn_')) {
        throw new CliError(
          'A runner secret starts with jrn_. Create one under Factory → Runners.',
          ExitCode.Validation,
        )
      }
      const url = rawUrl.replace(/\/+$/, '')

      const existing = readRunnerConfig()
      const machineId = existing?.machineId ?? randomUUID()
      // Verify before writing, as `auth login` does: a stored secret that never worked
      // turns into a service that restarts forever somewhere nobody is looking.
      const capabilities = await probe(1, machineId)
      const hello = await new RunnerClient({ baseUrl: url, token })
        .hello(capabilities)
        .catch((error: unknown) => {
          throw asCliError(error)
        })

      const profiles = existing?.profiles ?? []
      const index = await matchingProfile(profiles, url, token, hello.organizationSlug, capabilities)
      const previous = index >= 0 ? profiles[index] : undefined
      const profile: RunnerProfile = {
        url,
        token,
        organization: hello.organizationSlug,
        // Re-registering one organization (a rotated secret) keeps the repository map
        // someone built up; a new organization starts with its own, empty one.
        workspaces: previous?.workspaces ?? {},
        repoRoots: previous?.repoRoots ?? [],
      }
      const next = index >= 0 ? profiles.map((p, i) => (i === index ? profile : p)) : [...profiles, profile]
      const name = options.name ?? existing?.name
      writeRunnerConfig({
        machineId,
        ...(name ? { name } : {}),
        profiles: next,
        attachments: existing?.attachments ?? { maxCount: DefaultAttachmentMaxCount, maxBytes: DefaultAttachmentMaxBytes },
      })

      if (globals().json) {
        printJson({
          url,
          runner: hello,
          capabilities,
          configPath: runnerConfigPath(),
          organizations: next.map(profileLabel),
        })
        return
      }
      const organizations = next.map(profileLabel)
      const lines = [
        `Registered runner "${hello.name}" with ${url} (${hello.organizationSlug}).`,
        `Harnesses: ${capabilities.harnesses.map((h) => h.name).join(', ') || 'none found on PATH'}`,
        `Config written to ${runnerConfigPath()}.`,
      ]
      if (next.length > 1) {
        lines.push(
          '',
          `This machine now runs for ${next.length} organizations: ${organizations.join(', ')}.`,
          'Runs from different organizations never execute at the same time.',
          `Repository roots and mappings are per organization: \`aictiq runner root <path> --org ${hello.organizationSlug}\`.`,
          'A running `aictiq runner start` picks this up within a few seconds; otherwise start it.',
        )
      } else {
        lines.push('Start it with `aictiq runner start`.')
      }
      print(lines.join('\n'))
    })

  runner
    .command('map')
    .description(
      'Map a project key to a local clone, for projects whose repository source is local',
    )
    .argument('<projectKey>', 'Project key, e.g. ACME')
    .argument('[path]', 'Path of the clone (default: the current directory); omit with --remove')
    .option('--remove', 'Remove the mapping')
    .option('--org <slug>', 'The organization the mapping is for, when this machine runs for several')
    .action((projectKey: string, path: string | undefined, options: { remove?: boolean; org?: string }) => {
      const config = requireConfig()
      const profile = selectProfile(config, options.org ?? globals().org)
      const key = projectKey.toUpperCase()
      if (options.remove) {
        delete profile.workspaces[key]
      } else {
        const absolute = resolve(path ?? '.')
        if (!existsSync(absolute))
          throw new CliError(`${absolute} does not exist.`, ExitCode.Validation)
        profile.workspaces[key] = absolute
      }
      writeRunnerConfig(config)
      const where = config.profiles.length > 1 ? ` (${profileLabel(profile)})` : ''
      print(
        options.remove
          ? `Removed the mapping for ${key}${where}.`
          : `${key} → ${profile.workspaces[key]}${where}`,
      )
    })

  runner
    .command('root')
    .description(
      "Trust the web UI's path hint for local projects whose clone is under this directory",
    )
    .argument('[path]', 'Directory that holds your clones (default: the current directory)')
    .option('--remove', 'Stop trusting path hints under this directory')
    .option('--org <slug>', 'The organization whose path hints to trust, when this machine runs for several')
    .action((path: string | undefined, options: { remove?: boolean; org?: string }) => {
      const config = requireConfig()
      const profile = selectProfile(config, options.org ?? globals().org)
      const absolute = resolve(path ?? '.')
      if (options.remove) {
        profile.repoRoots = profile.repoRoots.filter((root) => resolve(root) !== absolute)
      } else {
        if (!existsSync(absolute))
          throw new CliError(`${absolute} does not exist.`, ExitCode.Validation)
        if (!profile.repoRoots.some((root) => resolve(root) === absolute))
          profile.repoRoots.push(absolute)
      }
      writeRunnerConfig(config)
      const whose = config.profiles.length > 1 ? `${profileLabel(profile)}'s path hints` : 'Path hints'
      print(
        options.remove
          ? `${whose} under ${absolute} are no longer used.`
          : `${whose} under ${absolute} are used for projects without a mapping.`,
      )
    })

  runner
    .command('remove')
    .description('Stop running for one organization on this machine (the other organizations are kept)')
    .argument('<org>', 'Organization slug, as `aictiq runner status` lists it')
    .action((org: string) => {
      const config = requireConfig()
      const profile = selectProfile(config, org)
      config.profiles = config.profiles.filter((p) => p !== profile)
      writeRunnerConfig(config)
      print(
        `Removed ${profileLabel(profile)} from this machine. A running \`aictiq runner start\` stops taking its runs within a few seconds.\n` +
          `Its secret still works until you disable or delete the runner in ${profileLabel(profile)}'s Factory → Runners.`,
      )
    })

  runner
    .command('status')
    .description('Show each registration, detected harnesses and mapped repositories')
    .action(async () => {
      const config = requireConfig()
      const capabilities = await probe(1, config.machineId)
      const registrations = await Promise.all(
        config.profiles.map(async (profile) => {
          try {
            const hello = await new RunnerClient({ baseUrl: profile.url, token: profile.token }).hello(capabilities)
            return { profile, hello, text: `ok - "${hello.name}" in ${hello.organizationSlug}`, revoked: false }
          } catch (error) {
            const message = error instanceof Error ? error.message : String(error)
            return {
              profile,
              error: message,
              text: `failing - ${message}`,
              revoked: error instanceof RunnerHttpError && error.revoked,
            }
          }
        }),
      )
      // A health check scripted around `status` must be able to tell a dead secret apart.
      if (registrations.some((r) => r.revoked)) process.exitCode = ExitCode.Auth

      if (globals().json) {
        printJson({
          machineId: config.machineId,
          capabilities,
          attachments: config.attachments,
          profiles: registrations.map((r) => ({
            url: r.profile.url,
            organization: r.hello?.organizationSlug ?? r.profile.organization ?? null,
            registration: r.hello ?? { error: r.error },
            workspaces: r.profile.workspaces,
            repoRoots: r.profile.repoRoots,
          })),
        })
        return
      }
      print(
        renderFields([
          ['Config', runnerConfigPath()],
          ['Machine', config.machineId],
          ['Workspaces', defaultWorkspaceRoot()],
          ['Attachments', `${config.attachments.maxCount} files / ${formatBytes(config.attachments.maxBytes)} per run`],
          ['CLI', `${version} (${capabilities.os}/${capabilities.arch})`],
        ]),
      )
      print('')
      print(
        capabilities.harnesses.length === 0
          ? 'No harness found on PATH (claude, codex, opencode).'
          : renderTable(capabilities.harnesses, [
              { header: 'HARNESS', value: (h) => h.name },
              { header: 'VERSION', value: (h) => h.version ?? '' },
            ]),
      )
      for (const { profile, hello, text } of registrations) {
        const org = hello?.organizationSlug ?? profileLabel(profile)
        print('')
        print(
          renderFields([
            ['Organization', org],
            ['Instance', profile.url],
            ['Registration', text],
          ]),
        )
        const mapped = Object.entries(profile.workspaces)
        print(
          mapped.length === 0
            ? `No local repositories mapped (\`aictiq runner map <KEY> <path> --org ${org}\`).`
            : renderTable(mapped, [
                { header: 'PROJECT', value: ([key]) => key },
                { header: 'REPOSITORY', value: ([, path]) => path },
              ]),
        )
        print(
          profile.repoRoots.length === 0
            ? `No repository roots; path hints from ${org} are not used (\`aictiq runner root <path> --org ${org}\`).`
            : renderTable(profile.repoRoots, [{ header: 'REPOSITORY ROOT', value: (root) => root }]),
        )
      }
    })

  runner
    .command('start')
    .description('Poll every registered organization for runs and execute them until interrupted')
    .option('--parallel <n>', 'Runs of one organization executed at the same time', '1')
    .option('--keep-workspaces', 'Leave each run’s checkout on disk after it finishes')
    .option('--workspace-root <path>', 'Where run checkouts are created')
    .action(
      async (options: { parallel: string; keepWorkspaces?: boolean; workspaceRoot?: string }) => {
        const config = requireConfig()
        const parallel = Number.parseInt(options.parallel, 10)
        if (!Number.isInteger(parallel) || parallel < 1 || parallel > 16) {
          throw new CliError('--parallel is between 1 and 16.', ExitCode.Validation)
        }
        // Keeps the machine id, and rewrites a file from before profiles in the new shape.
        writeRunnerConfig(config)

        const log = (message: string) =>
          process.stderr.write(`${new Date().toISOString()} ${message}\n`)
        const workspaceRoot = resolve(options.workspaceRoot ?? defaultWorkspaceRoot())
        const supervisor = new RunnerSupervisor({
          readConfig: () => readRunnerConfig(),
          local: log,
          learned: (profile) => {
            // Re-read rather than write what this process holds: `register` or `root` may
            // have changed the file since.
            const current = readRunnerConfig()
            const stored = current?.profiles.find((p) => p.token === profile.token)
            if (!current || !stored || stored.organization === profile.organization) return
            stored.organization = profile.organization
            writeRunnerConfig(current)
          },
          createLoop: (profile, floor, onHello) => {
            const client = new RunnerClient({ baseUrl: profile.url, token: profile.token })
            const local = (message: string) => log(`[${profileLabel(profile)}] ${message}`)
            return new RunnerLoop({
              client,
              parallel,
              probe: () => probe(parallel, config.machineId),
              local,
              floor,
              floorKey: profileKey(profile),
              onHello,
              execute: (run, hello, shutdown) => {
                // Re-read per run, so a new mapping or root applies without restarting the
                // runner. Only this profile's: another organization's roots never apply.
                const current = readRunnerConfig()
                const own = current?.profiles.find((p) => p.token === profile.token) ?? profile
                return executeRun(run, {
                  client,
                  hello,
                  adapters: harnesses,
                  runnerToken: profile.token,
                  shutdown,
                  local,
                  workspace: {
                    root: workspaceRoot,
                    repositories: own.workspaces,
                    repoRoots: own.repoRoots,
                    keep: options.keepWorkspaces === true,
                    mcpServer: mcpServerCommand(),
                    attachmentMaxCount: (current ?? config).attachments.maxCount,
                    attachmentMaxBytes: (current ?? config).attachments.maxBytes,
                  },
                })
              },
            })
          },
        })

        let interrupts = 0
        const onSignal = (signal: NodeJS.Signals) => {
          interrupts++
          if (interrupts === 1 && supervisor.inFlight > 0) {
            log(
              `${signal}: finishing ${supervisor.inFlight} run(s) in flight; send it again to cancel them`,
            )
            supervisor.stop()
          } else if (interrupts === 1) {
            supervisor.stop()
          } else {
            log(`${signal}: cancelling runs in flight`)
            supervisor.abort()
          }
        }
        process.on('SIGINT', onSignal)
        process.on('SIGTERM', onSignal)
        try {
          await supervisor.run()
        } catch (error) {
          throw asCliError(error)
        } finally {
          process.off('SIGINT', onSignal)
          process.off('SIGTERM', onSignal)
        }
      },
    )

  runner
    .command('install-service')
    .description(
      'Print a service definition that keeps `aictiq runner start` running: a systemd user unit (Linux), a launchd agent (macOS) or a Task Scheduler installer (Windows)',
    )
    .option('--parallel <n>', 'Runs executed at the same time', '1')
    .option('--platform <platform>', 'linux, macos or windows (default: this machine)')
    .action((options: { parallel: string; platform?: string }) => {
      const platform = options.platform ?? servicePlatform()
      if (!isServicePlatform(platform)) {
        throw new CliError(
          `Unknown platform "${platform}". Use linux, macos or windows.`,
          ExitCode.Validation,
        )
      }
      print(
        serviceDefinition(platform, {
          node: process.execPath,
          entry: cliEntry(),
          parallel: options.parallel,
          path: process.env.PATH ?? '/usr/local/bin:/usr/bin:/bin',
          home: homedir(),
        }),
      )
    })

  return runner
}

async function probe(maxParallel: number, machineId: string): Promise<RunnerCapabilities> {
  return {
    v: 1,
    harnesses: await probeHarnesses(),
    os: platform(),
    arch: arch(),
    cliVersion: version,
    maxParallel,
    machineId,
  }
}

/**
 * The profile a registration replaces: the same organization on the same instance (a
 * rotated secret). A profile from before profiles existed has no organization yet; it is
 * asked, and if its secret no longer works at all while it is the only one, it is taken to be
 * this organization's previous secret, as re-registering always treated it.
 */
async function matchingProfile(
  profiles: RunnerProfile[],
  url: string,
  token: string,
  organization: string,
  capabilities: RunnerCapabilities,
): Promise<number> {
  const exact = profiles.findIndex(
    (p) => p.token === token || (sameInstance(p.url, url) && p.organization === organization),
  )
  if (exact >= 0) return exact

  for (const [index, profile] of profiles.entries()) {
    if (profile.organization || !sameInstance(profile.url, url)) continue
    try {
      const hello = await new RunnerClient({ baseUrl: profile.url, token: profile.token }).hello(capabilities)
      profile.organization = hello.organizationSlug
      if (hello.organizationSlug === organization) return index
    } catch (error) {
      if (error instanceof RunnerHttpError && error.revoked && profiles.length === 1) return index
    }
  }
  return -1
}

function selectProfile(config: RunnerConfig, org: string | undefined): RunnerProfile {
  const serves = config.profiles.map(profileLabel).join(', ')
  if (org) {
    const wanted = org.toLowerCase()
    const found = config.profiles.find((p) => profileLabel(p).toLowerCase() === wanted)
    if (!found) {
      throw new CliError(`This machine does not run for ${org}. It runs for: ${serves}.`, ExitCode.Validation)
    }
    return found
  }
  if (config.profiles.length === 1) return config.profiles[0]!
  throw new CliError(
    `This machine runs for several organizations (${serves}). Say which with --org <slug>.`,
    ExitCode.Validation,
  )
}

/** The CLI that is running now, so the agent's MCP bridge is this exact version. */
function cliEntry(): string {
  return resolve(process.argv[1] ?? 'aictiq')
}

function mcpServerCommand(): { command: string; args: string[] } {
  return { command: process.execPath, args: [cliEntry(), 'mcp'] }
}

function formatBytes(value: number): string {
  return value >= 1024 * 1024 ? `${(value / (1024 * 1024)).toFixed(1)} MiB` : `${value} bytes`
}

function requireConfig(): RunnerConfig {
  const config = readRunnerConfig()
  if (!config || config.profiles.length === 0) {
    throw new CliError(
      `This machine is not registered as a runner (${runnerConfigPath()}). Run \`aictiq runner register --url <url> --token <jrn_…>\`.`,
      ExitCode.Auth,
    )
  }
  return config
}

function asCliError(error: unknown): unknown {
  if (error instanceof RunnerRevokedError) return new CliError(error.message, ExitCode.Auth)
  if (error instanceof RunnerHttpError) {
    return new CliError(
      error.status === 401
        ? `The instance refused the runner secret: ${error.message}`
        : error.message,
      error.status === 401 ? ExitCode.Auth : ExitCode.Error,
    )
  }
  return error
}

function isServicePlatform(value: string): value is ServicePlatform {
  return value === 'linux' || value === 'macos' || value === 'windows'
}
