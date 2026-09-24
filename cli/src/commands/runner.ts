import { Command } from 'commander'
import { existsSync } from 'node:fs'
import { arch, platform } from 'node:os'
import { resolve } from 'node:path'
import type { GlobalOptions } from '../context.js'
import { CliError, ExitCode } from '../errors.js'
import { print, printJson, renderFields, renderTable } from '../output.js'
import { promptSecret } from '../prompt.js'
import { version } from '../version.js'
import { RunnerClient, RunnerHttpError } from '../runner/client.js'
import { readRunnerConfig, runnerConfigPath, writeRunnerConfig } from '../runner/config.js'
import type { RunnerConfig } from '../runner/config.js'
import { executeRun } from '../runner/execute.js'
import { harnesses, probeHarnesses } from '../runner/harness/index.js'
import { RunnerLoop, RunnerRevokedError } from '../runner/loop.js'
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
    .description('Store a runner secret (jrn_…) from Factory → Runners and check it')
    // Not requiredOption: the root command defines --url and --token too, and commander hands
    // them to the root, so they are read from both places exactly as `auth login` does.
    .option('--url <url>', 'Base URL of the Aictiq instance')
    .option('--token <token>', 'Runner secret; read from stdin when omitted')
    .option('--name <name>', 'A local label for this runner (the instance keeps its own name)')
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

      // Verify before writing, as `auth login` does: a stored secret that never worked
      // turns into a service that restarts forever somewhere nobody is looking.
      const capabilities = await probe(1)
      const hello = await new RunnerClient({ baseUrl: url, token })
        .hello(capabilities)
        .catch((error: unknown) => {
          throw asCliError(error)
        })

      const existing = readRunnerConfig()
      const config: RunnerConfig = {
        url,
        token,
        ...(options.name ? { name: options.name } : existing?.name ? { name: existing.name } : {}),
        // Re-registering (a rotated secret) keeps the repository map someone built up.
        workspaces: existing?.workspaces ?? {},
        repoRoots: existing?.repoRoots ?? [],
        attachments: existing?.attachments ?? { maxCount: DefaultAttachmentMaxCount, maxBytes: DefaultAttachmentMaxBytes },
      }
      writeRunnerConfig(config)

      if (globals().json) {
        printJson({ url, runner: hello, capabilities, configPath: runnerConfigPath() })
        return
      }
      print(
        `Registered runner "${hello.name}" with ${url} (${hello.organizationSlug}).\n` +
          `Harnesses: ${capabilities.harnesses.map((h) => h.name).join(', ') || 'none found on PATH'}\n` +
          `Config written to ${runnerConfigPath()}. Start it with \`aictiq runner start\`.`,
      )
    })

  runner
    .command('map')
    .description(
      'Map a project key to a local clone, for projects whose repository source is local',
    )
    .argument('<projectKey>', 'Project key, e.g. ACME')
    .argument('[path]', 'Path of the clone (default: the current directory); omit with --remove')
    .option('--remove', 'Remove the mapping')
    .action((projectKey: string, path: string | undefined, options: { remove?: boolean }) => {
      const config = requireConfig()
      const key = projectKey.toUpperCase()
      if (options.remove) {
        delete config.workspaces[key]
      } else {
        const absolute = resolve(path ?? '.')
        if (!existsSync(absolute))
          throw new CliError(`${absolute} does not exist.`, ExitCode.Validation)
        config.workspaces[key] = absolute
      }
      writeRunnerConfig(config)
      print(
        options.remove ? `Removed the mapping for ${key}.` : `${key} → ${config.workspaces[key]}`,
      )
    })

  runner
    .command('root')
    .description(
      "Trust the web UI's path hint for local projects whose clone is under this directory",
    )
    .argument('[path]', 'Directory that holds your clones (default: the current directory)')
    .option('--remove', 'Stop trusting path hints under this directory')
    .action((path: string | undefined, options: { remove?: boolean }) => {
      const config = requireConfig()
      const absolute = resolve(path ?? '.')
      if (options.remove) {
        config.repoRoots = config.repoRoots.filter((root) => resolve(root) !== absolute)
      } else {
        if (!existsSync(absolute))
          throw new CliError(`${absolute} does not exist.`, ExitCode.Validation)
        if (!config.repoRoots.some((root) => resolve(root) === absolute))
          config.repoRoots.push(absolute)
      }
      writeRunnerConfig(config)
      print(
        options.remove
          ? `Path hints under ${absolute} are no longer used.`
          : `Path hints under ${absolute} are used for projects without a mapping.`,
      )
    })

  runner
    .command('status')
    .description('Show the registration, detected harnesses and mapped repositories')
    .action(async () => {
      const config = requireConfig()
      const capabilities = await probe(1)
      let registration: unknown
      let registrationText: string
      try {
        const hello = await new RunnerClient({ baseUrl: config.url, token: config.token }).hello(
          capabilities,
        )
        registration = hello
        registrationText = `ok - "${hello.name}" in ${hello.organizationSlug}`
      } catch (error) {
        registration = { error: error instanceof Error ? error.message : String(error) }
        registrationText = `failing - ${error instanceof Error ? error.message : String(error)}`
        // A health check scripted around `status` must be able to tell a dead secret apart.
        if (error instanceof RunnerHttpError && error.revoked) process.exitCode = ExitCode.Auth
      }

      if (globals().json) {
        printJson({ url: config.url, registration, capabilities, workspaces: config.workspaces, repoRoots: config.repoRoots, attachments: config.attachments })
        return
      }
      print(
        renderFields([
          ['Instance', config.url],
          ['Registration', registrationText],
          ['Config', runnerConfigPath()],
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
      print('')
      const mapped = Object.entries(config.workspaces)
      print(
        mapped.length === 0
          ? 'No local repositories mapped (`aictiq runner map <KEY> <path>`).'
          : renderTable(mapped, [
              { header: 'PROJECT', value: ([key]) => key },
              { header: 'REPOSITORY', value: ([, path]) => path },
            ]),
      )
      print('')
      print(
        config.repoRoots.length === 0
          ? 'No repository roots; path hints from the web UI are not used (`aictiq runner root <path>`).'
          : renderTable(config.repoRoots, [{ header: 'REPOSITORY ROOT', value: (root) => root }]),
      )
    })

  runner
    .command('start')
    .description('Poll for runs and execute them until interrupted')
    .option('--parallel <n>', 'Runs executed at the same time', '1')
    .option('--keep-workspaces', 'Leave each run’s checkout on disk after it finishes')
    .option('--workspace-root <path>', 'Where run checkouts are created')
    .action(
      async (options: { parallel: string; keepWorkspaces?: boolean; workspaceRoot?: string }) => {
        const config = requireConfig()
        const parallel = Number.parseInt(options.parallel, 10)
        if (!Number.isInteger(parallel) || parallel < 1 || parallel > 16) {
          throw new CliError('--parallel is between 1 and 16.', ExitCode.Validation)
        }

        const client = new RunnerClient({ baseUrl: config.url, token: config.token })
        const local = (message: string) =>
          process.stderr.write(`${new Date().toISOString()} ${message}\n`)
        const workspaceRoot = resolve(options.workspaceRoot ?? defaultWorkspaceRoot())
        const loop = new RunnerLoop({
          client,
          parallel,
          probe: () => probe(parallel),
          local,
          execute: (run, hello, shutdown) => {
            // Re-read per run, so a new mapping or root applies without restarting the
            // runner. The secret stays the one this process started with.
            const current = readRunnerConfig() ?? config
            return executeRun(run, {
              client,
              hello,
              adapters: harnesses,
              runnerToken: config.token,
              shutdown,
              local,
              workspace: {
                root: workspaceRoot,
                repositories: current.workspaces,
                repoRoots: current.repoRoots,
                keep: options.keepWorkspaces === true,
                mcpServer: mcpServerCommand(),
                attachmentMaxCount: current.attachments.maxCount,
                attachmentMaxBytes: current.attachments.maxBytes,
              },
            })
          },
        })

        let interrupts = 0
        const onSignal = (signal: NodeJS.Signals) => {
          interrupts++
          if (interrupts === 1 && loop.inFlight > 0) {
            local(
              `${signal}: finishing ${loop.inFlight} run(s) in flight; send it again to cancel them`,
            )
            loop.stop()
          } else if (interrupts === 1) {
            loop.stop()
          } else {
            local(`${signal}: cancelling runs in flight`)
            loop.abort()
          }
        }
        process.on('SIGINT', onSignal)
        process.on('SIGTERM', onSignal)
        try {
          await loop.run()
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
    .description('Print a systemd user unit that keeps `aictiq runner start` running')
    .option('--parallel <n>', 'Runs executed at the same time', '1')
    .action((options: { parallel: string }) => {
      print(systemdUnit(process.execPath, cliEntry(), options.parallel))
    })

  return runner
}

export function systemdUnit(node: string, entry: string, parallel: string): string {
  return `# Save as ~/.config/systemd/user/aictiq-runner.service, then:
#   systemctl --user daemon-reload
#   systemctl --user enable --now aictiq-runner
#   loginctl enable-linger "$USER"   # keep it running after you log out
#
# The runner executes agents as this user, with this user's harness sign-ins and git
# credentials: one runner is one trust domain. SIGTERM lets runs in flight finish;
# TimeoutStopSec bounds how long systemd waits before it cancels them.
[Unit]
Description=Aictiq runner
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=${node} ${entry} runner start --parallel ${parallel}
Restart=on-failure
RestartSec=10
# Exit 5 means the secret was revoked; restarting cannot fix that.
RestartPreventExitStatus=5
KillMode=mixed
TimeoutStopSec=15min
Environment=PATH=${process.env.PATH ?? '/usr/local/bin:/usr/bin:/bin'}

[Install]
WantedBy=default.target
`
}

async function probe(maxParallel: number): Promise<RunnerCapabilities> {
  return {
    v: 1,
    harnesses: await probeHarnesses(),
    os: platform(),
    arch: arch(),
    cliVersion: version,
    maxParallel,
  }
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
  if (!config) {
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
