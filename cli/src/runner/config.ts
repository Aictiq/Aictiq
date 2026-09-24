import { createHash, randomUUID } from 'node:crypto'
import { chmodSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { dirname, join } from 'node:path'
import { DefaultAttachmentMaxBytes, DefaultAttachmentMaxCount } from './workspace.js'

export interface RunnerAttachmentConfig {
  maxCount: number
  maxBytes: number
}

/**
 * One organization this machine runs for: its own secret, and its own repository map and
 * roots. Nothing here is shared between profiles - a path hint from one organization is
 * never resolved under another organization's roots, and a project key that exists in two
 * organizations maps to two different clones.
 */
export interface RunnerProfile {
  url: string
  token: string
  /**
   * The organization slug the instance named on hello. Absent only in a profile carried over
   * from a `runner.json` written before profiles existed, until its first hello.
   */
  organization?: string
  /** Project key → path of a local clone, for projects whose repository source is `local`. */
  workspaces: Record<string, string>
  /**
   * Directories under which a project's path hint (set in the web UI) is used when the
   * project has no `workspaces` entry. Absolute, or starting with `~/`.
   */
  repoRoots: string[]
}

/**
 * What `aictiq runner register` writes. Deliberately a separate file from `config.json`:
 * a runner's credential is not a person's, and a machine that is both somebody's laptop
 * and a runner must not have one overwrite the other.
 */
export interface RunnerConfig {
  /**
   * Random, kept for the life of this file, and reported to every organization, so the web UI
   * can show this machine once however many organizations it serves. It authorizes nothing.
   */
  machineId: string
  /** A local label for this machine (each instance keeps its own runner name). */
  name?: string
  profiles: RunnerProfile[]
  attachments: RunnerAttachmentConfig
}

export function runnerConfigPath(env: NodeJS.ProcessEnv = process.env): string {
  const base = env.AICTIQ_CONFIG_HOME ?? env.XDG_CONFIG_HOME ?? join(homedir(), '.config')
  return join(base, 'aictiq', 'runner.json')
}

/**
 * Null when the file is missing or unreadable. A file from before profiles (`url` and `token`
 * at the top level) reads as one profile, and a file without a machine id gets a new one,
 * which is kept from the next write on.
 */
export function readRunnerConfig(path = runnerConfigPath()): RunnerConfig | null {
  if (!existsSync(path)) return null
  let parsed: unknown
  try {
    parsed = JSON.parse(readFileSync(path, 'utf8'))
  } catch {
    return null
  }
  if (typeof parsed !== 'object' || parsed === null) return null
  const raw = parsed as Record<string, unknown>

  let profiles: RunnerProfile[]
  if (Array.isArray(raw.profiles)) {
    profiles = raw.profiles.flatMap((entry) => {
      const profile = readProfile(entry)
      return profile ? [profile] : []
    })
  } else {
    const legacy = readProfile(raw)
    if (!legacy) return null
    profiles = [legacy]
  }

  const rawLimits =
    typeof raw.attachments === 'object' && raw.attachments !== null
      ? (raw.attachments as Record<string, unknown>)
      : {}
  return {
    machineId:
      typeof raw.machineId === 'string' && UuidPattern.test(raw.machineId)
        ? raw.machineId
        : randomUUID(),
    ...(typeof raw.name === 'string' ? { name: raw.name } : {}),
    profiles,
    attachments: {
      maxCount: validLimit(rawLimits.maxCount, DefaultAttachmentMaxCount),
      maxBytes: validLimit(rawLimits.maxBytes, DefaultAttachmentMaxBytes),
    },
  }
}

export function writeRunnerConfig(config: RunnerConfig, path = runnerConfigPath()): void {
  mkdirSync(dirname(path), { recursive: true, mode: 0o700 })
  const document = {
    machineId: config.machineId,
    ...(config.name ? { name: config.name } : {}),
    attachments: config.attachments,
    profiles: config.profiles.map((profile) => ({
      ...(profile.organization ? { organization: profile.organization } : {}),
      url: profile.url,
      token: profile.token,
      workspaces: profile.workspaces,
      repoRoots: profile.repoRoots,
    })),
  }
  writeFileSync(path, `${JSON.stringify(document, null, 2)}\n`, { mode: 0o600 })
  // The mode argument only applies on creation; narrow a file someone widened.
  chmodSync(path, 0o600)
}

/** How the runner names a profile in its log and its messages. */
export function profileLabel(profile: Pick<RunnerProfile, 'organization' | 'url'>): string {
  if (profile.organization) return profile.organization
  try {
    return new URL(profile.url).host
  } catch {
    return profile.url
  }
}

/**
 * A profile's identity for the runner's own bookkeeping (the floor), never its secret: keys
 * end up in messages, and a secret must not.
 */
export function profileKey(profile: Pick<RunnerProfile, 'token'>): string {
  return createHash('sha256').update(profile.token).digest('hex').slice(0, 16)
}

export function sameInstance(left: string, right: string): boolean {
  return left.replace(/\/+$/, '').toLowerCase() === right.replace(/\/+$/, '').toLowerCase()
}

const UuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

function readProfile(value: unknown): RunnerProfile | null {
  if (typeof value !== 'object' || value === null) return null
  const { url, token, organization, workspaces, repoRoots } = value as Record<string, unknown>
  if (typeof url !== 'string' || typeof token !== 'string') return null

  const mapped: Record<string, string> = {}
  if (typeof workspaces === 'object' && workspaces !== null) {
    for (const [key, path] of Object.entries(workspaces)) {
      if (typeof path === 'string') mapped[key] = path
    }
  }
  const roots = Array.isArray(repoRoots)
    ? repoRoots.filter((root): root is string => typeof root === 'string' && root.trim() !== '')
    : []
  return {
    url,
    token,
    ...(typeof organization === 'string' && organization !== '' ? { organization } : {}),
    workspaces: mapped,
    repoRoots: roots,
  }
}

function validLimit(value: unknown, fallback: number): number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : fallback
}
