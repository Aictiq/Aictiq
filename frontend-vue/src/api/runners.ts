import { apiFetch } from '@/utils/api'

/**
 * Runners: processes on machines an organization controls (a VPS, a laptop, a CI box) that
 * execute factory runs with a coding harness. The roster is an organization Admin's; the
 * runner itself speaks a separate protocol with its own `jrn_` secret, which this client
 * never sends - it only shows the secret once, the moment it is minted.
 */

export interface RunnerHarness {
  /** As a playbook names it: `claude`, `codex`, `opencode`. */
  name: string
  version: string | null
}

/** What the runner reported about itself on its last hello or heartbeat. */
export interface RunnerCapabilities {
  v: number
  harnesses: RunnerHarness[]
  os: string | null
  arch: string | null
  cliVersion: string | null
  maxParallel: number
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

export const listRunners = (slug: string) => apiFetch<Runner[]>(`/orgs/${slug}/runners`)

export const registerRunner = (slug: string, name: string) =>
  apiFetch<RunnerIssued>(`/orgs/${slug}/runners`, { method: 'POST', body: { name } })

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
