import { apiFetch } from '@/utils/api'

/**
 * Runners: processes on machines an organization controls (a VPS, a laptop, a CI box) that
 * execute factory runs with a coding harness. The roster is an organization Admin's; the
 * runner itself speaks a separate protocol with its own `jrn_` secret, which this client
 * never sends - it only shows the secret once, the moment it is minted.
 */

export interface RunnerHarness {
  /** As a playbook names it: `claude`, `codex`, `opencode`, `cursor`, `copilot`. */
  name: string
  version: string | null
}

/** A harness the CLI knows that the runner does not offer, and why. */
export interface RunnerMissingHarness {
  name: string
  /** `not-on-path`: no executable found; `version-failed`: found, but `--version` failed. */
  reason: 'not-on-path' | 'version-failed'
  /** The executable looked for, or the one found. */
  command: string
}

/** The runner's last self-update that did not take; it keeps working on its current version. */
export interface RunnerUpdateFailure {
  version: string
  error: string
  at: string
}

/** One usage window of a harness account: how much of its allowance is used, and when it starts over. */
export interface RunnerUsageWindow {
  /** 0 to 100; a little more past the limit. */
  usedPercent: number
  resetsAt: string | null
}

/**
 * The 5-hour and weekly allowance a harness account on the machine last reported in a run's
 * output. Only harnesses that report one appear (Claude Code and Codex today).
 */
export interface RunnerUsageLimits {
  harness: string
  /** When a run last read it; the figures are only as current as this. */
  observedAt: string
  fiveHour: RunnerUsageWindow | null
  weekly: RunnerUsageWindow | null
}

/** What the runner reported about itself on its last hello or heartbeat. */
export interface RunnerCapabilities {
  v: number
  harnesses: RunnerHarness[]
  os: string | null
  arch: string | null
  cliVersion: string | null
  maxParallel: number
  /** The same for every organization one machine serves; only groups them for display. */
  machineId?: string | null
  /** Started by a definition from `install-service`. Absent from runners older than the setup guide. */
  service?: boolean | null
  /** This organization's project keys the runner maps to a clone. Absent from older runners. */
  workspaces?: string[] | null
  /** This organization's repository roots on the machine. Absent from older runners. */
  repoRoots?: string[] | null
  /** Known harnesses the runner does not offer, and why. Absent from older runners. */
  missingHarnesses?: RunnerMissingHarness[] | null
  /** The PATH the runner looked for harnesses on: a service's, not a login shell's. */
  path?: string | null
  /** Set while the runner's last self-update attempt failed. */
  updateFailure?: RunnerUpdateFailure | null
  /** Harness allowances seen in runs. Absent from older runners and until a run reports one. */
  usageLimits?: RunnerUsageLimits[] | null
  /** Claude usage is read from Anthropic between runs (`aictiq runner usage --claude-oauth on`). */
  claudeUsagePoll?: boolean | null
}

export interface Runner {
  id: string
  name: string
  /** `jrn_a1b2c3d4…` - all that survives of the secret. */
  tokenDisplay: string
  registeredBy: string
  registeredByName: string | null
  capabilities: RunnerCapabilities | null
  lastSeenAt: string | null
  /** Decided by the API, so every client agrees on what "online" means. */
  isOnline: boolean
  isDisabled: boolean
  createdAt: string
}

export interface RunnerIssued {
  runner: Runner
  /** Shown once. Only its hash is stored. */
  secret: string
}

/** One of the organizations a machine already runs for, and its runner there. */
export interface RunnerMachineOrganization {
  slug: string
  name: string
  runnerId: string
  runnerName: string
}

/**
 * A machine the caller already runs for other organizations they administer: their own runners
 * there, one entry per machine however many organizations it serves.
 */
export interface RunnerMachine {
  /** Its most recently seen runner; what `sameMachineAs` names. */
  runnerId: string
  name: string
  capabilities: RunnerCapabilities | null
  lastSeenAt: string | null
  isOnline: boolean
  /** This organization already has a runner on the same machine. */
  isConnectedHere: boolean
  organizations: RunnerMachineOrganization[]
}

export const listRunners = (slug: string) => apiFetch<Runner[]>(`/orgs/${slug}/runners`)

/**
 * A runner as someone starting a run sees it. Open to factory operators, not only Admins,
 * so it carries nothing but what picking one needs. Disabled runners are left out.
 */
export interface RunnerChoice {
  id: string
  name: string
  /** What the runner last reported; empty until it has said hello. */
  harnesses: string[]
  isOnline: boolean
  /** Harness allowances the runner last reported. Absent from older servers and runners. */
  usageLimits?: RunnerUsageLimits[] | null
}

export const listRunnerChoices = (slug: string) =>
  apiFetch<RunnerChoice[]>(`/orgs/${slug}/runners/choices`)

export const listRunnerMachinesElsewhere = (slug: string) =>
  apiFetch<RunnerMachine[]>(`/orgs/${slug}/runners/elsewhere`)

export const registerRunner = (slug: string, name: string) =>
  apiFetch<RunnerIssued>(`/orgs/${slug}/runners`, { method: 'POST', body: { name } })

/**
 * Registers a machine that already runs for another organization. It gets its own secret here;
 * pasted on the machine, it becomes one more profile there. The name defaults to the machine's.
 */
export const registerRunnerOnMachine = (slug: string, sameMachineAs: string, name?: string) =>
  apiFetch<RunnerIssued>(`/orgs/${slug}/runners`, {
    method: 'POST',
    body: name ? { name, sameMachineAs } : { sameMachineAs },
  })

/**
 * No version to echo: the runner rewrites its own row on every heartbeat, so a version token
 * would make every edit here race a machine. Both fields are absolute assignments.
 */
export const updateRunner = (
  slug: string,
  runnerId: string,
  body: { name?: string; disabled?: boolean },
) => apiFetch<Runner>(`/orgs/${slug}/runners/${runnerId}`, { method: 'PATCH', body })

/** Retires it for good: its secret stops working and it leaves the roster. */
export const deleteRunner = (slug: string, runnerId: string) =>
  apiFetch<void>(`/orgs/${slug}/runners/${runnerId}`, { method: 'DELETE' })

/** A new secret; the old one stops working at once, so the machine must be re-registered. */
export const rotateRunner = (slug: string, runnerId: string) =>
  apiFetch<RunnerIssued>(`/orgs/${slug}/runners/${runnerId}/rotate`, { method: 'POST' })
