import { Command } from 'commander'
import { randomUUID } from 'node:crypto'
import { existsSync } from 'node:fs'
import { arch, homedir, platform } from 'node:os'
import { resolve } from 'node:path'
import type { GlobalOptions } from '../context.js'
import { CliError, ExitCode, formatProblem } from '../errors.js'
import { print, printJson, renderFields, renderTable } from '../output.js'
import { promptSecret } from '../prompt.js'
import { packageRoot, version } from '../version.js'
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
import { IdleCheckMs, IdleClock, idleExitMinutes, retireEphemeral } from '../runner/idle.js'
import { describeMissing, harnesses, probeAllHarnesses } from '../runner/harness/index.js'
import { readUsageLimits, recordUsageLimits, usageLimitsPath } from '../runner/limits.js'
import { extendProcessPath, harnessDirs, withHarnessDirs } from '../runner/path.js'
import {
  serviceDefinition,
  servicePlatform,
  startedAsService,
  UpdateExitCode,
  type ServicePlatform,
} from '../runner/service.js'
import { RunnerLoop, RunnerRevokedError } from '../runner/loop.js'
import { RunnerSupervisor } from '../runner/supervisor.js'
import { UsagePoller } from '../runner/usagePoll.js'
import { collectTags, normalizeTags } from '../runner/tags.js'
import type { RunnerCapabilities, UpdateFailure } from '../runner/types.js'
import {
  cliEntryPath,
  PackageName,
  rerun,
  SelfUpdater,
  UpdateCheckIntervalMs,
  UpdateRetryMs,
} from '../runner/update.js'
import {
  DefaultAttachmentMaxBytes,
  DefaultAttachmentMaxCount,
  defaultWorkspaceRoot,
  pruneKeptWorkspaces,
} from '../runner/workspace.js'

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
      await storeRegistration(globals, { url, token, name: options.name })
    })

  runner
    .command('enroll')
    .description(
      'Fleet mode: create an ephemeral runner with an enrollment token (jfe_…) and store its secret, as `register` does',
    )
    .option('--url <url>', 'Base URL of the Aictiq instance')
    .option('--token <token>', 'Enrollment token; read from stdin when omitted')
    .option('--name <name>', 'The runner\'s name (default: the instance picks one)')
    .option('--tag <tag>', 'A tag for this runner; repeat it or separate tags with commas', collectTags)
    .action(async (options: { url?: string; token?: string; name?: string; tag?: string[] }) => {
      const global = globals()
      const rawUrl = options.url ?? global.url
      if (!rawUrl) {
        throw new CliError(
          'Pass --url with the address of your Aictiq instance.',
          ExitCode.Validation,
        )
      }
      const tags = normalizeTags(options.tag)
      const enrollmentToken =
        options.token ??
        global.token ??
        process.env.AICTIQ_ENROLL_TOKEN ??
        (await promptSecret('Enrollment token: '))
      if (!enrollmentToken?.startsWith('jfe_')) {
        throw new CliError('An enrollment token starts with jfe_.', ExitCode.Validation)
      }
      const url = rawUrl.replace(/\/+$/, '')
      const name = options.name?.trim() || null
      const enrollment = await new RunnerClient({ baseUrl: url, token: enrollmentToken })
        .enroll(name, tags)
        .catch((error: unknown) => {
          throw enrollmentError(error, name)
        })
      await storeRegistration(globals, {
        url,
        token: enrollment.secret,
        name: name ?? undefined,
        tags,
        ephemeral: true,
      })
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
            const hello = await new RunnerClient({ baseUrl: profile.url, token: profile.token }).hello({
              ...capabilities,
              workspaces: Object.keys(profile.workspaces).sort(),
              repoRoots: [...profile.repoRoots],
            })
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
        renderTable(
          [
            ...capabilities.harnesses.map((h) => ({ name: h.name, version: h.version ?? '', note: '' })),
            ...(capabilities.missingHarnesses ?? []).map((m) => ({
              name: m.name,
              version: 'missing',
              note: describeMissing(m),
            })),
          ],
          [
            { header: 'HARNESS', value: (h) => h.name },
            { header: 'VERSION', value: (h) => h.version },
            { header: 'WHY', value: (h) => h.note },
          ],
        ),
      )
      // A service has its own PATH (its definition's, plus the same per-user directories), so
      // a harness this shell finds can still be missing there: the Factory runner page shows
      // what the service itself reported.
      print('')
      print(renderFields([['PATH', capabilities.path ?? '']]))
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
    .option('--no-auto-update', 'Only log a newer CLI on npm; never install it and restart')
    .option('--tag <tag>', 'A tag reported to every organization; repeat it or separate tags with commas', collectTags)
    .option(
      '--idle-exit <minutes>',
      'Exit once no run has been active for this many minutes (1-1440), deregistering ephemeral runners first',
    )
    .action(
      async (options: {
        parallel: string
        keepWorkspaces?: boolean
        workspaceRoot?: string
        autoUpdate: boolean
        tag?: string[]
        idleExit?: string
      }) => {
        const config = requireConfig()
        const parallel = Number.parseInt(options.parallel, 10)
        if (!Number.isInteger(parallel) || parallel < 1 || parallel > 16) {
          throw new CliError('--parallel is between 1 and 16.', ExitCode.Validation)
        }
        const tags = normalizeTags(options.tag)
        const idleMinutes = idleExitMinutes(options.idleExit)
        // Keeps the machine id, and rewrites a file from before profiles in the new shape.
        writeRunnerConfig(config)

        const log = (message: string) =>
          process.stderr.write(`${new Date().toISOString()} ${message}\n`)
        const workspaceRoot = resolve(options.workspaceRoot ?? defaultWorkspaceRoot())
        // Workspaces failed runs kept for a continue expire while the runner is down too.
        if (options.keepWorkspaces !== true) await pruneKeptWorkspaces(workspaceRoot)
        /**
         * The last self-update that did not take, reported with every heartbeat so the Factory
         * runner page shows it; otherwise it only reaches this process's log.
         */
        let updateFailure: UpdateFailure | null = null
        const failUpdate = (target: string, error: unknown) => {
          const message = error instanceof Error ? error.message : String(error)
          updateFailure = {
            version: target,
            error: message.length > MaxReportedUpdateError ? `${message.slice(0, MaxReportedUpdateError - 1)}…` : message,
            at: new Date().toISOString(),
          }
          return message
        }
        // One for the machine, however many organizations it serves: harness-limits.json is its own.
        const usage = new UsagePoller({
          claudeEnabled: () => readRunnerConfig()?.claudeUsagePoll === true,
          log,
        })
        const createSupervisor = () =>
          new RunnerSupervisor({
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
                probe: async () => {
                  // Re-read, like each run does: a new mapping or root shows up in the web UI's
                  // setup guide on the next heartbeat. Only this profile's are reported.
                  const current = readRunnerConfig()
                  const own = current?.profiles.find((p) => p.token === profile.token) ?? profile
                  await usage.refresh()
                  return {
                    ...(await probe(parallel, config.machineId, own, tags)),
                    service: startedAsService(),
                    ...(updateFailure ? { updateFailure } : {}),
                    usageLimits: readUsageLimits(),
                    claudeUsagePoll: current?.claudeUsagePoll === true,
                  }
                },
                local,
                floor,
                floorKey: profileKey(profile),
                onHello,
                onActivity: () => idle?.touch(),
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
                    onLimits: (harness, limits, observedAt) => {
                      recordUsageLimits(harness, limits, observedAt)
                    },
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

        // runner.json is read again at each check, so `"autoUpdate": false` applies without a restart.
        const autoUpdate = () => options.autoUpdate && readRunnerConfig()?.autoUpdate !== false
        const updater = new SelfUpdater({ current: version, packageRoot, entry: cliEntry(), log })
        /** The version to install once the runs in flight are done, when a check found one. */
        let target: string | null = null
        /** Counts from now: a runner that never gets a run still leaves after the idle time. */
        const idle = idleMinutes === null ? null : new IdleClock(idleMinutes * 60_000)
        let idled = false
        let supervisor = createSupervisor()
        let interrupted = false

        let retry: ReturnType<typeof setTimeout> | undefined
        const check = async (): Promise<void> => {
          clearTimeout(retry)
          const result = await updater.check()
          if (result.status === 'unavailable') {
            log(
              `Update check failed (${result.reason}); staying on ${version} and trying again in ${UpdateRetryMs / 60_000} min`,
            )
            if (!interrupted) retry = setTimeout(() => void check(), UpdateRetryMs)
            return
          }
          if (result.status === 'current') {
            updateFailure = null
            log(`Update check: ${version} is up to date (latest on npm: ${result.latest})`)
            return
          }
          if (!autoUpdate()) {
            log(
              `Update check: ${PackageName} ${result.latest} is available (running ${version}); auto-update is off, so run \`aictiq runner update\``,
            )
            return
          }
          if (interrupted || idled || target) return
          try {
            await updater.installation()
          } catch (error) {
            log(
              `Update check: ${PackageName} ${result.latest} is available (running ${version}), but ${failUpdate(result.latest, error)}`,
            )
            return
          }
          target = result.latest
          const inFlight = supervisor.inFlight
          log(
            `Update check: ${PackageName} ${result.latest} is available (running ${version}); claiming no new runs${
              inFlight > 0 ? ` and waiting for ${inFlight} in flight` : ''
            } before upgrading`,
          )
          supervisor.stop()
        }

        let interrupts = 0
        const onSignal = (signal: NodeJS.Signals) => {
          interrupts++
          interrupted = true
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
        let upgraded: { from: string; to: string } | null = null
        try {
          // Before claiming anything, so an outdated runner upgrades without a run to wait for.
          await check()
          const timer = setInterval(() => void check(), UpdateCheckIntervalMs)
          // `expired` is false while any run is in flight, and stopping waits for the runs in
          // flight anyway (one claimed in the same instant included), so no run is cut short.
          const idleTimer = idle
            ? setInterval(() => {
                if (idled || interrupted || target || !idle.expired(supervisor.inFlight)) return
                idled = true
                log(`No run for ${idleMinutes} min; stopping (--idle-exit)`)
                supervisor.stop()
              }, IdleCheckMs)
            : undefined
          try {
            while (!upgraded && !interrupted && !idled) {
              if (!target) await supervisor.run()
              if (interrupted || idled || !target) break
              try {
                upgraded = await updater.install(target)
              } catch (error) {
                // Back to work on this version; the next check (hours away) tries again.
                log(`Upgrade to ${target} failed (${failUpdate(target, error)}); staying on ${version}`)
                target = null
                supervisor = createSupervisor()
              }
            }
          } finally {
            clearInterval(timer)
            clearInterval(idleTimer)
            clearTimeout(retry)
          }
        } catch (error) {
          throw asCliError(error)
        } finally {
          process.off('SIGINT', onSignal)
          process.off('SIGTERM', onSignal)
        }
        if (idled && !upgraded) {
          await retireEphemeral({
            readConfig: () => readRunnerConfig(),
            writeConfig: (current) => writeRunnerConfig(current),
            client: (profile) => new RunnerClient({ baseUrl: profile.url, token: profile.token }),
            local: log,
          })
          log('Idle; exiting')
          process.exitCode = ExitCode.Ok
          return
        }
        if (!upgraded) return
        if (interrupted) {
          log(`Upgraded ${PackageName} ${upgraded.from} → ${upgraded.to}; stopped, so not restarting`)
          return
        }

        log(`Upgraded ${PackageName} ${upgraded.from} → ${upgraded.to}; restarting`)
        if (startedAsService()) {
          // The service definition starts the same entry again, which is now the new version.
          process.exitCode = UpdateExitCode
          return
        }
        process.exitCode = await rerun(cliEntry(), process.argv.slice(2))
      },
    )

  runner
    .command('usage')
    .description("Show the harness usage this machine last saw, or turn the Claude usage poll on or off")
    .option(
      '--claude-oauth <state>',
      "on: read Claude Code's OAuth token and ask api.anthropic.com for 5-hour and weekly usage every 5 minutes; off: only after runs",
    )
    .action((options: { claudeOauth?: string }) => {
      const config = requireConfig()
      if (options.claudeOauth !== undefined) {
        const state = options.claudeOauth.toLowerCase()
        if (state !== 'on' && state !== 'off') {
          throw new CliError('--claude-oauth is on or off.', ExitCode.Validation)
        }
        config.claudeUsagePoll = state === 'on'
        writeRunnerConfig(config)
      }
      const enabled = config.claudeUsagePoll === true
      const limits = readUsageLimits()
      if (globals().json) {
        printJson({ claudeUsagePoll: enabled, usageLimits: limits })
        return
      }
      if (options.claudeOauth !== undefined && enabled) print(`${ClaudeOAuthNotice}\n`)
      print(
        renderFields([
          ['Claude usage', enabled ? 'polled from Anthropic every 5 minutes' : 'updated after runs only'],
          ['Codex usage', 'read from the newest local Codex session every minute'],
          ['Stored in', usageLimitsPath()],
        ]),
      )
      if (options.claudeOauth !== undefined) {
        print('A running `aictiq runner start` picks this up at its next heartbeat.')
      }
      if (limits.length === 0) return
      print('')
      const percent = (window: { usedPercent: number } | null) => (window ? `${window.usedPercent}%` : '')
      print(
        renderTable(limits, [
          { header: 'HARNESS', value: (l) => l.harness },
          { header: '5H', value: (l) => percent(l.fiveHour) },
          { header: 'WEEKLY', value: (l) => percent(l.weekly) },
          { header: 'WEEKLY RESETS', value: (l) => l.weekly?.resetsAt ?? '' },
          { header: 'AS OF', value: (l) => l.observedAt },
        ]),
      )
    })

  runner
    .command('update')
    .description(`Install the latest ${PackageName} from npm now, with the package manager it was installed with`)
    .action(async () => {
      const log = (message: string) => {
        if (!globals().json) process.stderr.write(`${message}\n`)
      }
      const updater = new SelfUpdater({ current: version, packageRoot, entry: cliEntry(), log })
      const result = await updater.check()
      if (result.status === 'unavailable') {
        throw new CliError(`Cannot check for updates: ${result.reason}`, ExitCode.Error)
      }
      let to = version
      if (result.status === 'newer') {
        try {
          to = (await updater.install(result.latest)).to
        } catch (error) {
          throw new CliError(
            `Upgrade to ${result.latest} failed: ${error instanceof Error ? error.message : String(error)}`,
            ExitCode.Error,
          )
        }
      }
      if (globals().json) {
        printJson({ from: version, to, latest: result.latest, upgraded: to !== version })
        return
      }
      print(
        to === version
          ? `${PackageName} ${version} is up to date (latest on npm: ${result.latest}).`
          : [
              `Upgraded ${PackageName} ${version} → ${to}.`,
              'A running `aictiq runner start` keeps its version until it restarts; with auto-update on it restarts at its next check, once its runs finish.',
            ].join('\n'),
      )
    })

  runner
    .command('install-service')
    .description(
      'Print a service definition that keeps `aictiq runner start` running: a systemd user unit (Linux), a launchd agent (macOS) or a Task Scheduler installer (Windows)',
    )
    .option('--parallel <n>', 'Runs executed at the same time', '1')
    .option('--platform <platform>', 'linux, macos or windows (default: this machine)')
    .option('--tag <tag>', 'A tag the runner reports; repeat it or separate tags with commas', collectTags)
    .action((options: { parallel: string; platform?: string; tag?: string[] }) => {
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
          tags: normalizeTags(options.tag),
          // `runner start` appends these again on every probe, so a harness installed later
          // in one of them is found without regenerating the definition.
          path:
            platform === servicePlatform()
              ? withHarnessDirs(process.env.PATH ?? '/usr/local/bin:/usr/bin:/bin', harnessDirs())
              : (process.env.PATH ?? '/usr/local/bin:/usr/bin:/bin'),
          home: homedir(),
        }),
      )
    })

  return runner
}

/**
 * What `register` and `enroll` share once they hold a `jrn_` secret: say hello with it, then
 * store it as this organization's profile. `enroll` marks the profile ephemeral.
 */
async function storeRegistration(
  globals: () => GlobalOptions,
  registration: { url: string; token: string; name?: string; tags?: string[]; ephemeral?: boolean },
): Promise<void> {
  const { url, token } = registration
  const existing = readRunnerConfig()
  const machineId = existing?.machineId ?? randomUUID()
  // Verify before writing, as `auth login` does: a stored secret that never worked
  // turns into a service that restarts forever somewhere nobody is looking.
  // A registration runs in a terminal, so it is not a service yet: the guide says so rather
  // than "cannot tell" until `runner start` reports for itself.
  const capabilities = { ...(await probe(1, machineId, undefined, registration.tags)), service: false }
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
    ...(registration.ephemeral ? { ephemeral: true as const } : {}),
  }
  const next = index >= 0 ? profiles.map((p, i) => (i === index ? profile : p)) : [...profiles, profile]
  const name = registration.name ?? existing?.name
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
    registration.ephemeral
      ? `Enrolled ephemeral runner "${hello.name}" with ${url} (${hello.organizationSlug}).`
      : `Registered runner "${hello.name}" with ${url} (${hello.organizationSlug}).`,
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
    lines.push(
      registration.ephemeral
        ? 'Start it with `aictiq runner start --idle-exit <minutes>`, so it deregisters itself once idle.'
        : 'Start it with `aictiq runner start`.',
    )
  }
  print(lines.join('\n'))
}

async function probe(
  maxParallel: number,
  machineId: string,
  profile?: Pick<RunnerProfile, 'workspaces' | 'repoRoots'>,
  tags: string[] = [],
): Promise<RunnerCapabilities> {
  // A service's PATH was frozen when its definition was generated, and lacks every per-user
  // directory a shell profile adds; the probe, each harness and git all use the extended one.
  const path = extendProcessPath()
  const { harnesses: found, missing } = await probeAllHarnesses()
  return {
    v: 1,
    harnesses: found,
    missingHarnesses: missing,
    path: path.length > MaxReportedPath ? `${path.slice(0, MaxReportedPath - 1)}…` : path,
    os: platform(),
    arch: arch(),
    cliVersion: version,
    maxParallel,
    machineId,
    ...(profile
      ? { workspaces: Object.keys(profile.workspaces).sort(), repoRoots: [...profile.repoRoots] }
      : {}),
    ...(tags.length > 0 ? { tags: [...tags] } : {}),
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

/** Printed when the Claude usage poll is turned on; docs/harness-usage-limits.md repeats it. */
const ClaudeOAuthNotice =
  "The runner will read Claude Code's OAuth access token on this machine and send it only to " +
  'api.anthropic.com to read your 5-hour and weekly usage. The token never leaves this machine ' +
  'for any other host, is not stored by Aictiq, and is not sent to the Aictiq server. Only the ' +
  'percentages and reset times are.'

/** What the instance accepts as `capabilities.path` and `capabilities.updateFailure.error`. */
const MaxReportedPath = 4096
const MaxReportedUpdateError = 1000

/** The CLI that is running now, so the agent's MCP bridge is this exact version. */
function cliEntry(): string {
  return cliEntryPath()
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

/** `runner enroll`'s refusals, in the terms of an enrollment token rather than a runner secret. */
function enrollmentError(error: unknown, name: string | null): unknown {
  if (error instanceof RunnerHttpError) {
    if (error.status === 401) {
      return new CliError(
        'The enrollment token is not valid, or fleet mode is off for that organization.',
        ExitCode.Auth,
        error.problem,
      )
    }
    if (error.status === 409) {
      return new CliError(
        name
          ? `A runner named "${name}" already exists in that organization. Pass another --name.`
          : formatProblem(error.problem, 'That runner name is taken. Pass another --name.'),
        ExitCode.Conflict,
        error.problem,
      )
    }
    if (error.status === 400) {
      return new CliError(formatProblem(error.problem, error.message), ExitCode.Validation, error.problem)
    }
  }
  return asCliError(error)
}

function isServicePlatform(value: string): value is ServicePlatform {
  return value === 'linux' || value === 'macos' || value === 'windows'
}
