import type { Agent } from '@/api/agents'
import type { FactorySettings, Playbook } from '@/api/playbooks'
import type { ProjectMember } from '@/api/projects'
import type { Runner } from '@/api/runners'
import type { RunnerPlatform } from '@/lib/runners'

/**
 * The Factory setup guide as data: which steps a machine and a project need before an agent
 * can finish a run, and which of them Aictiq can confirm on its own.
 *
 * A step is confirmed by what the API already knows - a runner's hello and heartbeats, a
 * project's factory settings, its agents, playbooks and runs - never by a click. Only what no
 * server can see (a `gh auth login` on the machine) takes a person's tick, and the guide says
 * which kind of check each one is. Nothing here fetches: the view hands in what it loaded, so
 * every rule can be tested.
 */

/**
 * `confirmed`: Aictiq saw it done. `ticked`: the person says so and Aictiq cannot see it.
 * `todo`: not done yet. `unknown`: done or not, Aictiq cannot tell - usually an older runner
 * that does not report it - so a tick is offered. `automatic`: nothing to do.
 */
export type SetupStepState = 'confirmed' | 'ticked' | 'todo' | 'unknown' | 'automatic'

export interface SetupStep {
  id: string
  state: SetupStepState
  /** One line under the title: what Aictiq saw, or what is missing. */
  detail: string | null
  /** A person may tick it: only when Aictiq cannot confirm it. */
  tickable: boolean
}

export const isSetupStepDone = (state: SetupStepState) =>
  state === 'confirmed' || state === 'ticked' || state === 'automatic'

// ── the machine ───────────────────────────────────────────────────────────────────────

export type MachineStepId = 'install' | 'harness' | 'register' | 'start' | 'service'

export const machineStepIds: MachineStepId[] = [
  'install',
  'harness',
  'register',
  'start',
  'service',
]

/**
 * The machine's steps for one runner, or for none yet. Each confirmation is something the
 * runner itself reported: a hello proves the CLI and the secret, its capabilities name the
 * harnesses, a recent heartbeat is "started", and a definition from `install-service` marks
 * itself.
 */
export function machineSteps(runner: Runner | null, ticks: ReadonlySet<string>): SetupStep[] {
  const capabilities = runner?.capabilities ?? null
  const said = runner?.lastSeenAt != null
  const step = (
    id: MachineStepId,
    state: SetupStepState,
    detail: string | null = null,
  ): SetupStep => ({
    id,
    state,
    detail,
    tickable: false,
  })

  const harnesses = capabilities?.harnesses.map((h) => h.name) ?? []

  const service = ((): SetupStep => {
    if (!runner || !said) return step('service', 'todo')
    if (capabilities?.service === true)
      return step('service', 'confirmed', 'Started by its service definition.')
    // An older runner does not say how it was started; only its operator knows.
    if (capabilities?.service == null) {
      return ticks.has('service')
        ? { ...step('service', 'ticked', 'You confirmed it.'), tickable: true }
        : {
            ...step(
              'service',
              'unknown',
              'This runner does not report how it was started. Update the CLI, or tick it yourself.',
            ),
            tickable: true,
          }
    }
    return step(
      'service',
      'todo',
      runner.isOnline ? 'Running from a terminal: it stops when you log out.' : null,
    )
  })()

  return [
    step(
      'install',
      capabilities?.cliVersion ? 'confirmed' : said ? 'confirmed' : 'todo',
      capabilities?.cliVersion
        ? `CLI ${capabilities.cliVersion} on ${capabilities.os ?? '?'}/${capabilities.arch ?? '?'}.`
        : null,
    ),
    harnesses.length > 0
      ? step('harness', 'confirmed', `Found ${harnesses.join(', ')}.`)
      : step('harness', 'todo', said ? 'The runner found no harness on its PATH.' : null),
    runner && said
      ? step('register', 'confirmed', `Registered as ${runner.name}.`)
      : step(
          'register',
          'todo',
          runner ? `${runner.name} is registered here but has not said hello yet.` : null,
        ),
    runner?.isOnline && !runner.isDisabled
      ? step('start', 'confirmed', 'Online now.')
      : step(
          'start',
          'todo',
          runner?.isDisabled ? 'Disabled: enable it under Runners.' : said ? 'Offline.' : null,
        ),
    service,
  ]
}

/** The service tab to open on: what the runner reported, else a guess from the browser. */
export function runnerPlatformOf(runner: Runner | null): RunnerPlatform | null {
  const os = runner?.capabilities?.os
  if (os === 'darwin') return 'macos'
  if (os === 'win32') return 'windows'
  if (os === 'linux') return 'linux'
  return null
}

// ── the project ───────────────────────────────────────────────────────────────────────

export type ProjectStepId =
  'agent' | 'repository' | 'checkout' | 'push' | 'connection' | 'playbook' | 'first-run'

export interface ProjectFacts {
  projectKey: string
  settings: FactorySettings | null
  agents: Agent[] | null
  members: ProjectMember[] | null
  playbooks: Playbook[] | null
  /** Succeeded runs of this project, from the API's total count. */
  succeededRuns: number | null
  /** The runner the guide follows; null when none is chosen yet. */
  runner: Runner | null
  /**
   * What every usable runner of the organization offers, for when no runner is chosen (or the
   * person may not see the roster, only the runners they can pick at run time).
   */
  offeredHarnesses: string[]
}

/** A project that was never configured answers with a synthesized default and no `updatedAt`. */
export function repositoryConfigured(settings: FactorySettings | null): boolean {
  if (!settings) return false
  return settings.repoSource === 1
    ? settings.updatedAt !== null
    : (settings.repoFullName ?? '').trim().length > 0
}

/**
 * Whether a path hint lies under one of the runner's reported roots. A `~/` hint is matched
 * against a root under a home directory, since the runner expands `~` itself; anything that
 * cannot be decided here is `null`. The runner still decides for itself on every run.
 */
export function hintUnderRoot(hint: string, roots: readonly string[]): boolean | null {
  const clean = (path: string) =>
    path
      .trim()
      .replace(/\\/g, '/')
      .replace(/(.)\/+$/, '$1')
  const path = clean(hint)
  if (!path) return null
  const inside = (candidate: string, root: string) =>
    candidate === root || candidate.startsWith(root === '/' ? '/' : `${root}/`)
  if (path.startsWith('~/')) {
    const rest = path.slice(1)
    // `/home/ana/src` covers `~/src/x` when `~` is `/home/ana`; which home it is only the
    // runner knows, so a match there is as far as this can go.
    const homed = roots
      .map(clean)
      .map((root) => /^(\/home\/[^/]+|\/Users\/[^/]+|\/root)(\/.*)?$/.exec(root))
      .filter((m): m is RegExpExecArray => m !== null)
    if (homed.length === 0) return roots.length === 0 ? false : null
    return homed.some((m) => inside(`${m[1]}${rest}`, clean(m[0])))
  }
  if (!path.startsWith('/') && !/^[A-Za-z]:\//.test(path)) return null
  return roots.map(clean).some((root) => inside(path, root))
}

export function projectSteps(facts: ProjectFacts, ticks: ReadonlySet<string>): SetupStep[] {
  const step = (
    id: ProjectStepId,
    state: SetupStepState,
    detail: string | null = null,
    tickable = false,
  ): SetupStep => ({
    id,
    state,
    detail,
    tickable,
  })
  const tickableStep = (id: ProjectStepId, unknownDetail: string): SetupStep =>
    ticks.has(id)
      ? step(id, 'ticked', 'You confirmed it.', true)
      : step(id, 'unknown', unknownDetail, true)

  const { settings, runner } = facts
  const local = settings?.repoSource === 1
  const delivered = (facts.succeededRuns ?? 0) > 0

  // An agent: active, and on this project's roster, or no run can be handed to it.
  const agent = (() => {
    if (!facts.agents || !facts.members) return step('agent', 'todo')
    const onProject = new Set(facts.members.filter((m) => m.isAgent).map((m) => m.userId))
    const ready = facts.agents.filter((a) => a.isActive && onProject.has(a.userId))
    if (ready.length > 0) {
      const preferred = ready.find((a) => a.userId === settings?.defaultAgentId) ?? ready[0]!
      return step('agent', 'confirmed', `${preferred.displayName} can take this project's items.`)
    }
    return step(
      'agent',
      'todo',
      facts.agents.some((a) => a.isActive)
        ? 'An agent exists, but none is a member of this project yet.'
        : 'No active agent in this organization yet.',
    )
  })()

  const repository = repositoryConfigured(settings)
    ? step(
        'repository',
        'confirmed',
        local
          ? `Runner-local checkout${settings?.localPathHint ? ` at ${settings.localPathHint}` : ''}, branch ${settings?.defaultBranch}.`
          : `${settings?.repoFullName}, branch ${settings?.defaultBranch}.`,
      )
    : step('repository', 'todo', 'Choose a GitHub binding or a checkout on the runner.')

  const checkout = (() => {
    if (!repositoryConfigured(settings)) return step('checkout', 'todo')
    if (!local) return step('checkout', 'automatic', 'Each run clones the bound repository itself.')
    if (delivered) return step('checkout', 'confirmed', 'A run already worked in it.')
    const capabilities = runner?.capabilities
    if (!runner) {
      // Only Admins see the roster; everyone else can only say it is done.
      return facts.offeredHarnesses.length > 0
        ? tickableStep(
            'checkout',
            'Only Admins see what a runner maps. Ask one, or tick it yourself.',
          )
        : step('checkout', 'todo', 'No runner has reported in yet.')
    }
    if (!capabilities || capabilities.workspaces == null || capabilities.repoRoots == null) {
      return tickableStep(
        'checkout',
        `${runner.name} does not report its mappings. Update the CLI, or tick it yourself.`,
      )
    }
    if (capabilities.workspaces.includes(facts.projectKey)) {
      return step('checkout', 'confirmed', `${runner.name} maps ${facts.projectKey} to a clone.`)
    }
    const hint = settings?.localPathHint ?? ''
    const under = hint ? hintUnderRoot(hint, capabilities.repoRoots) : false
    if (under === true)
      return step('checkout', 'confirmed', `${hint} is under a repository root of ${runner.name}.`)
    if (under === null) {
      return tickableStep(
        'checkout',
        `Aictiq cannot tell whether ${hint} is under one of ${runner.name}'s roots.`,
      )
    }
    return step(
      'checkout',
      'todo',
      capabilities.repoRoots.length === 0
        ? `${runner.name} has no repository root and no mapping for ${facts.projectKey}.`
        : `${hint || 'The path hint'} is not under a root of ${runner.name}, and ${facts.projectKey} is not mapped.`,
    )
  })()

  const push = (() => {
    if (!repositoryConfigured(settings)) return step('push', 'todo')
    if (!local)
      return step('push', 'automatic', 'Each run gets a short-lived GitHub App credential.')
    if (delivered) return step('push', 'confirmed', 'A run already pushed its work.')
    return tickableStep(
      'push',
      'Aictiq cannot see the machine’s git sign-in. Tick it once gh auth status is happy.',
    )
  })()

  const connection = step(
    'connection',
    'automatic',
    'Each run gets the Aictiq MCP server and its own short-lived agent token.',
  )

  const playbook = (() => {
    const playbooks = facts.playbooks
    if (!playbooks) return step('playbook', 'todo')
    if (playbooks.length === 0) return step('playbook', 'todo', 'This project has no playbook yet.')
    const offered = new Set(
      runner
        ? runner.isDisabled
          ? []
          : (runner.capabilities?.harnesses.map((h) => h.name) ?? [])
        : facts.offeredHarnesses,
    )
    const runnable = playbooks.filter((p) => offered.has(p.harness))
    if (runnable.length > 0) {
      const shown = runnable.find((p) => p.isDefault) ?? runnable[0]!
      return step(
        'playbook',
        'confirmed',
        `${shown.name} (${shown.harness})${playbooks.length > 1 ? ` and ${playbooks.length - 1} more` : ''}.`,
      )
    }
    const needs = [...new Set(playbooks.map((p) => p.harness))].join(' or ')
    return step(
      'playbook',
      'todo',
      offered.size === 0
        ? `Its playbooks use ${needs}; no runner has reported a harness yet.`
        : `Its playbooks use ${needs}, which ${runner ? runner.name : 'no runner'} offers.`,
    )
  })()

  const firstRun = delivered
    ? step(
        'first-run',
        'confirmed',
        facts.succeededRuns === 1 ? 'One run succeeded.' : `${facts.succeededRuns} runs succeeded.`,
      )
    : step('first-run', 'todo')

  return [agent, repository, checkout, push, connection, playbook, firstRun]
}

// ── the person's ticks ────────────────────────────────────────────────────────────────

const TICKS_PREFIX = 'aictiq.setup.'

/**
 * A tick only records what someone said about a machine Aictiq cannot see, so it lives in this
 * browser and nowhere else; a blocked storage reads as no ticks.
 */
export function readSetupTicks(userId: string, slug: string, scope: string): Set<string> {
  try {
    const raw = localStorage.getItem(`${TICKS_PREFIX}${userId}.${slug}.${scope}`)
    const parsed = raw ? (JSON.parse(raw) as unknown) : []
    return new Set(
      Array.isArray(parsed) ? parsed.filter((v): v is string => typeof v === 'string') : [],
    )
  } catch {
    return new Set()
  }
}

export function writeSetupTicks(
  userId: string,
  slug: string,
  scope: string,
  ticks: ReadonlySet<string>,
): void {
  try {
    localStorage.setItem(`${TICKS_PREFIX}${userId}.${slug}.${scope}`, JSON.stringify([...ticks]))
  } catch {
    // A private window forgets the tick, not the work.
  }
}
