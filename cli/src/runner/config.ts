import { chmodSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { dirname, join } from 'node:path'
import { DefaultAttachmentMaxBytes, DefaultAttachmentMaxCount } from './workspace.js'

export interface RunnerAttachmentConfig {
  maxCount: number
  maxBytes: number
}

/**
 * What `aictiq runner register` writes. Deliberately a separate file from `config.json`:
 * a runner's credential is not a person's, and a machine that is both somebody's laptop
 * and a runner must not have one overwrite the other.
 */
export interface RunnerConfig {
  url: string
  token: string
  name?: string
  /** Project key → path of a local clone, for projects whose repository source is `local`. */
  workspaces: Record<string, string>
  /**
   * Directories under which a project's path hint (set in the web UI) is used when the
   * project has no `workspaces` entry. Absolute, or starting with `~/`.
   */
  repoRoots: string[]
  attachments: RunnerAttachmentConfig
}

export function runnerConfigPath(env: NodeJS.ProcessEnv = process.env): string {
  const base = env.AICTIQ_CONFIG_HOME ?? env.XDG_CONFIG_HOME ?? join(homedir(), '.config')
  return join(base, 'aictiq', 'runner.json')
}

export function readRunnerConfig(path = runnerConfigPath()): RunnerConfig | null {
  if (!existsSync(path)) return null
  let parsed: unknown
  try {
    parsed = JSON.parse(readFileSync(path, 'utf8'))
  } catch {
    return null
  }
  if (typeof parsed !== 'object' || parsed === null) return null
  const { url, token, name, workspaces, repoRoots, attachments } = parsed as Record<string, unknown>
  if (typeof url !== 'string' || typeof token !== 'string') return null

  const mapped: Record<string, string> = {}
  if (typeof workspaces === 'object' && workspaces !== null) {
    for (const [key, value] of Object.entries(workspaces)) {
      if (typeof value === 'string') mapped[key] = value
    }
  }
  const roots = Array.isArray(repoRoots)
    ? repoRoots.filter((root): root is string => typeof root === 'string' && root.trim() !== '')
    : []
  const rawLimits = typeof attachments === 'object' && attachments !== null ? attachments as Record<string, unknown> : {}
  return {
    url,
    token,
    ...(typeof name === 'string' ? { name } : {}),
    workspaces: mapped,
    repoRoots: roots,
    attachments: {
      maxCount: validLimit(rawLimits.maxCount, DefaultAttachmentMaxCount),
      maxBytes: validLimit(rawLimits.maxBytes, DefaultAttachmentMaxBytes),
    },
  }
}

export function writeRunnerConfig(config: RunnerConfig, path = runnerConfigPath()): void {
  mkdirSync(dirname(path), { recursive: true, mode: 0o700 })
  writeFileSync(path, `${JSON.stringify(config, null, 2)}\n`, { mode: 0o600 })
  // The mode argument only applies on creation; narrow a file someone widened.
  chmodSync(path, 0o600)
}

function validLimit(value: unknown, fallback: number): number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : fallback
}
